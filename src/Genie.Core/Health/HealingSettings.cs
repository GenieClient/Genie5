using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Encodings.Web;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

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
    private bool _includeInternal = true;
    private bool _includeScars    = true;
    private bool _halfOnMajor;
    private bool _leaveBleeders;
    private bool _overlayFigure;
    private string[] _touchPatterns = DefaultTouchPatterns;
    private (string Pattern, Regex Regex)[] _touchRegexes = Compile(DefaultTouchPatterns);

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


    /// <summary>Bulk takes (Take All / Take Minor) include internal wounds.
    /// On by default.</summary>
    public bool IncludeInternal { get => Get(ref _includeInternal); set => Set(ref _includeInternal, value); }

    /// <summary>Bulk takes include scars. On by default.</summary>
    public bool IncludeScars { get => Get(ref _includeScars); set => Set(ref _includeScars, value); }

    /// <summary>Bulk takes ask for <c>half</c> of a very damaging or worse
    /// wound instead of all of it, so a deep wound does not drain the
    /// empath in one take.</summary>
    public bool HalfOnMajor { get => Get(ref _halfOnMajor); set => Set(ref _halfOnMajor, value); }

    /// <summary>Bulk takes skip fresh wounds worse than harmful — the ones
    /// still bleeding badly — and leave them for a deliberate click.</summary>
    public bool LeaveBleeders { get => Get(ref _leaveBleeders); set => Set(ref _leaveBleeders, value); }

    /// <summary>The Healing panel draws one figure with all four wound axes
    /// overlaid, instead of four figures (one per axis). Off by default.</summary>
    public bool OverlayFigure { get => Get(ref _overlayFigure); set => Set(ref _overlayFigure, value); }

    private bool Get(ref bool field) { lock (_gate) return field; }

    private void Set(ref bool field, bool value)
    {
        lock (_gate)
        {
            if (field == value) return;
            field = value;
        }
        Changed?.Invoke();
    }


    // ── Touch patterns ───────────────────────────────────────────────────────

    /// <summary>The placeholder a touch pattern uses for the patient's name.</summary>
    public const string NamePlaceholder = "{name}";

    /// <summary>The line DR prints when an empathic link forms — the one
    /// pattern Genie 4's Crutch shipped with.</summary>
    public static readonly string[] DefaultTouchPatterns =
        { "You sense a successful empathic link has been forged between you and {name}." };

    /// <summary>
    /// Lines that mean "a patient is now linked". The text before and after
    /// <c>{name}</c> is matched literally and the name is whatever sits between,
    /// so a player can add the wording a different touch or link produces.
    /// Every pattern is live (Crutch kept one active at a time).
    /// </summary>
    public IReadOnlyList<string> TouchPatterns
    {
        get { lock (_gate) return _touchPatterns; }
    }

    /// <summary>A usable pattern holds <c>{name}</c> once and has some text around it.</summary>
    public static bool IsValidTouchPattern(string? pattern)
    {
        var p = pattern?.Trim();
        if (string.IsNullOrEmpty(p)) return false;
        var i = p.IndexOf(NamePlaceholder, StringComparison.Ordinal);
        if (i < 0 || p.IndexOf(NamePlaceholder, i + NamePlaceholder.Length, StringComparison.Ordinal) >= 0) return false;
        return p.Length > NamePlaceholder.Length;
    }

    /// <summary>Replace the patterns. Invalid and duplicate entries are dropped.
    /// An empty list turns patient detection by message off.</summary>
    public void SetTouchPatterns(IEnumerable<string> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        var clean = patterns.Select(p => p?.Trim() ?? "")
                            .Where(IsValidTouchPattern)
                            .Distinct(StringComparer.Ordinal)
                            .ToArray();
        lock (_gate)
        {
            if (clean.SequenceEqual(_touchPatterns, StringComparer.Ordinal)) return;
            _touchPatterns = clean;
            _touchRegexes  = Compile(clean);
        }
        Changed?.Invoke();
    }

    /// <summary>The patient named by <paramref name="line"/>, if it matches a
    /// touch pattern. The name is cut down to a safe command-line token
    /// (<see cref="HealingCommandBuilder.CleanPatient"/>); false when nothing
    /// usable is left.</summary>
    public bool TryMatchPatient(string? line, out string patient)
    {
        patient = "";
        if (string.IsNullOrEmpty(line) || line.Length > 400) return false;
        (string Pattern, Regex Regex)[] regexes;
        lock (_gate) regexes = _touchRegexes;
        var text = line.Trim();
        foreach (var (_, regex) in regexes)
        {
            var m = regex.Match(text);
            if (!m.Success) continue;
            var name = HealingCommandBuilder.CleanPatient(m.Groups["name"].Value);
            if (name.Length == 0) continue;
            patient = name;
            return true;
        }
        return false;
    }

    private static (string Pattern, Regex Regex)[] Compile(IEnumerable<string> patterns)
        => patterns.Select(p =>
        {
            var i      = p.IndexOf(NamePlaceholder, StringComparison.Ordinal);
            var prefix = Regex.Escape(p[..i]);
            var suffix = Regex.Escape(p[(i + NamePlaceholder.Length)..]);
            return (p, new Regex($"^{prefix}(?<name>.+?){suffix}", RegexOptions.CultureInvariant,
                                 TimeSpan.FromMilliseconds(50)));
        }).ToArray();

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
        public bool IncludeInternal { get; set; } = true;
        public bool IncludeScars    { get; set; } = true;
        public bool HalfOnMajor     { get; set; }
        public bool LeaveBleeders   { get; set; }
        public string[]? TouchPatterns { get; set; }
        public bool OverlayFigure { get; set; }
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
            _includeInternal = model?.IncludeInternal ?? true;
            _includeScars    = model?.IncludeScars    ?? true;
            _halfOnMajor     = model?.HalfOnMajor     ?? false;
            _leaveBleeders   = model?.LeaveBleeders   ?? false;
            _overlayFigure   = model?.OverlayFigure   ?? false;
            // A file without the key keeps the shipped pattern; one with an empty
            // array means the player turned message detection off.
            _touchPatterns = (model?.TouchPatterns ?? DefaultTouchPatterns)
                .Select(p => p?.Trim() ?? "").Where(IsValidTouchPattern).Distinct(StringComparer.Ordinal).ToArray();
            _touchRegexes = Compile(_touchPatterns);
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
            model.IncludeInternal = _includeInternal;
            model.IncludeScars    = _includeScars;
            model.HalfOnMajor     = _halfOnMajor;
            model.LeaveBleeders   = _leaveBleeders;
            model.TouchPatterns   = _touchPatterns;
            model.OverlayFigure   = _overlayFigure;
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
