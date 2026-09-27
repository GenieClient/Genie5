using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Genie.Core.Health;

/// <summary>
/// Per-character Healing panel settings (public #263): the mana and the
/// prep-to-cast wait for each <see cref="HealingSpell"/>, and whether takes are
/// sent <c>quick</c>. Persisted as <see cref="FileName"/> in the profile
/// directory, next to <c>dialogmappings.json</c>.
///
/// <para><b>Genie 4 carry-over.</b> Crutch kept these values as the
/// <c>GCTextBoxMana*</c> / <c>GCTextBoxDelay*</c> variables, so a player who
/// imported their Genie 4 variables already has them. A spell with no value
/// set here falls back to its variable (<see cref="Resolve"/>), and every value
/// set here is mirrored back out under the same name
/// (<see cref="MirrorTo"/>) so a script reading <c>$GCTextBoxManaHW</c> sees
/// what the panel will send.</para>
///
/// <para>Pure data. Nothing here sends anything.</para>
/// </summary>
public sealed class HealingSettings
{
    public const string FileName = "healing.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _gate = new();
    private readonly Dictionary<HealingSpell, int>    _mana  = new();
    private readonly Dictionary<HealingSpell, double> _delay = new();
    private bool _quick;

    /// <summary>A value changed, or a load replaced them all. May fire off the
    /// UI thread.</summary>
    public event Action? Changed;

    /// <summary>Append <c>quick</c> to every take.</summary>
    public bool QuickTake
    {
        get { lock (_gate) return _quick; }
        set
        {
            lock (_gate)
            {
                if (_quick == value) return;
                _quick = value;
            }
            Changed?.Invoke();
        }
    }

    /// <summary>The mana set in the panel, or null when unset.</summary>
    public int? GetMana(HealingSpell spell)
    {
        lock (_gate) return _mana.TryGetValue(spell, out var m) ? m : null;
    }

    /// <summary>The prep-to-cast wait in seconds set in the panel, or null.</summary>
    public double? GetDelay(HealingSpell spell)
    {
        lock (_gate) return _delay.TryGetValue(spell, out var d) ? d : null;
    }

    /// <summary>Set (or with null, clear) a spell's mana. Negative is clamped to 0.</summary>
    public void SetMana(HealingSpell spell, int? mana)
    {
        lock (_gate)
        {
            if (mana is { } m) _mana[spell] = Math.Max(0, m);
            else _mana.Remove(spell);
        }
        Changed?.Invoke();
    }

    /// <summary>Set (or with null, clear) a spell's prep-to-cast wait.</summary>
    public void SetDelay(HealingSpell spell, double? seconds)
    {
        lock (_gate)
        {
            if (seconds is { } s && !double.IsNaN(s)) _delay[spell] = Math.Clamp(s, 0, 60);
            else _delay.Remove(spell);
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// The mana and wait to use for <paramref name="spell"/>: the panel's value,
    /// else the Genie 4 variable read through <paramref name="variable"/>, else
    /// 0. A mana of 0 means "prepare at the minimum" (no amount on the line); a
    /// wait of 0 means "prepare only — the player clicks Cast".
    /// </summary>
    public (int Mana, double Delay) Resolve(HealingSpell spell, Func<string, string?>? variable = null)
    {
        var info  = HealingSpells.Get(spell);
        var mana  = GetMana(spell)  ?? ParseInt(variable?.Invoke(info.ManaVariable))     ?? 0;
        var delay = GetDelay(spell) ?? ParseDouble(variable?.Invoke(info.DelayVariable)) ?? 0;
        return (Math.Max(0, mana), Math.Clamp(delay, 0, 60));
    }

    /// <summary>Write every value set here out under its Genie 4 variable name.
    /// Unset values are left alone, so an imported variable is not clobbered.</summary>
    public void MirrorTo(Action<string, string> setVariable)
    {
        ArgumentNullException.ThrowIfNull(setVariable);
        List<(string, string)> writes = new();
        lock (_gate)
        {
            foreach (var (spell, m) in _mana)
                writes.Add((HealingSpells.Get(spell).ManaVariable, m.ToString(CultureInfo.InvariantCulture)));
            foreach (var (spell, d) in _delay)
                writes.Add((HealingSpells.Get(spell).DelayVariable, FormatSeconds(d)));
        }
        foreach (var (name, value) in writes) setVariable(name, value);
    }

    // ── Persistence ──────────────────────────────────────────────────────────

    private sealed class FileModel
    {
        public bool QuickTake { get; set; }
        public Dictionary<string, SpellModel> Spells { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class SpellModel
    {
        public int?    Mana  { get; set; }
        public double? Delay { get; set; }
    }

    /// <summary>
    /// Replace every value from <paramref name="path"/>. A missing file clears
    /// them (a reconnect as a different character must not carry the last
    /// one's mana over). A torn or unreadable file leaves the current values
    /// untouched and returns false.
    /// </summary>
    public bool Load(string path)
    {
        FileModel? model = null;
        if (File.Exists(path))
        {
            try { model = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(path), JsonOptions); }
            catch { return false; }
            if (model is null) return false;
        }

        lock (_gate)
        {
            _mana.Clear();
            _delay.Clear();
            _quick = model?.QuickTake ?? false;
            if (model is not null)
                foreach (var (key, s) in model.Spells)
                {
                    if (!HealingSpells.TryParse(key, out var spell) || s is null) continue;
                    if (s.Mana  is { } m) _mana[spell]  = Math.Max(0, m);
                    if (s.Delay is { } d && !double.IsNaN(d)) _delay[spell] = Math.Clamp(d, 0, 60);
                }
        }
        Changed?.Invoke();
        return model is not null;
    }

    /// <summary>Write the values to <paramref name="path"/>. False rather than
    /// throwing — a settings write must not take the session down.</summary>
    public bool Save(string path)
    {
        var model = new FileModel();
        lock (_gate)
        {
            model.QuickTake = _quick;
            foreach (var info in HealingSpells.All)
            {
                var hasMana  = _mana.TryGetValue(info.Spell, out var m);
                var hasDelay = _delay.TryGetValue(info.Spell, out var d);
                if (!hasMana && !hasDelay) continue;
                model.Spells[info.Abbreviation] = new SpellModel
                {
                    Mana  = hasMana  ? m : null,
                    Delay = hasDelay ? d : null,
                };
            }
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(model, JsonOptions));
            return true;
        }
        catch { return false; }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Seconds as the command queue reads them: invariant, no
    /// trailing zeros (<c>4</c>, <c>2.5</c>).</summary>
    public static string FormatSeconds(double seconds)
        => seconds.ToString("0.##", CultureInfo.InvariantCulture);

    private static int? ParseInt(string? s)
        => int.TryParse(s?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static double? ParseDouble(string? s)
        => double.TryParse(s?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) &&
           !double.IsNaN(v) ? v : null;
}
