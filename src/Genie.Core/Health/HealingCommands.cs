using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Genie.Core.Health;

/// <summary>The three whole-body conditions a Healing panel can act on.</summary>
public enum HealingCondition
{
    Vitality,
    Poison,
    Disease,
}

/// <summary>
/// What one Healing panel click should send (public #263).
/// </summary>
/// <param name="Lines">Input lines, in order, for the ordinary user-command
/// path (<c>CommandEngine.ProcessInput</c>) — exactly as if the player had
/// typed each one.</param>
/// <param name="PendingCast">Set when a spell was prepared with no
/// prep-to-cast wait configured: the cast line the player's NEXT click on
/// Cast should send. Nothing sends it on its own.</param>
public sealed record HealingAction(IReadOnlyList<string> Lines, string? PendingCast = null)
{
    public static HealingAction None { get; } = new(Array.Empty<string>());

    public bool IsEmpty => Lines.Count == 0 && PendingCast is null;

    internal static HealingAction Of(params string[] lines) => new(lines);
}

/// <summary>
/// Builds the command lines behind every Healing panel button (public #263).
/// Pure: it returns strings and never sends. The panel hands the result to the
/// normal user-command path, and only in response to a click — so the send
/// policy is visible at one call site in the view-model rather than spread
/// through this class.
///
/// <para><b>Command vocabulary.</b> The verbs are the ones Genie 4's Crutch and
/// the community empath scripts send: <c>touch</c> / <c>perceive health</c> to
/// read a patient, <c>take &lt;patient&gt; &lt;part&gt; [internal] [scar]
/// [quick]</c> to transfer a wound, <c>take &lt;patient&gt;
/// vitality|poison|disease</c>, and for the empath's own body
/// <c>prep hw|hs &lt;mana&gt;</c> then <c>cast &lt;part&gt;</c>. This is a
/// clean reimplementation from that observable behaviour, not a port of
/// Crutch's code.</para>
///
/// <para><b>Queued lines.</b> Take All and a prepare-then-cast with a
/// configured wait go through <c>#send</c>, the roundtime-gated command queue
/// the player can also type — one click queues them, the queue releases each
/// as roundtime clears, and <c>#queue clear</c> (the panel's Stop) cancels
/// what has not gone yet. Nothing repeats and nothing starts without a click
/// (docs/POLICY.md: a click is direct user intent).</para>
/// </summary>
public sealed class HealingCommandBuilder
{
    private readonly HealingSettings _settings;
    private readonly char _commandChar;
    private readonly Func<string, string?>? _variable;

    /// <param name="settings">Mana / wait / quick values.</param>
    /// <param name="commandChar">The player's command character (<c>#</c> by
    /// default), used for <c>#send</c> and <c>#queue clear</c>.</param>
    /// <param name="variable">Variable lookup for the Genie 4
    /// <c>GCTextBox*</c> fallback; null to skip it.</param>
    public HealingCommandBuilder(HealingSettings settings, char commandChar = '#',
                                 Func<string, string?>? variable = null)
    {
        _settings    = settings ?? throw new ArgumentNullException(nameof(settings));
        _commandChar = commandChar;
        _variable    = variable;
    }

    // ── Patient identity ─────────────────────────────────────────────────────

    /// <summary>True when <paramref name="patient"/> means the player: blank,
    /// <c>self</c>, <c>me</c> or <c>yourself</c>.</summary>
    public static bool IsSelf(string? patient)
    {
        var p = patient?.Trim() ?? "";
        return p.Length == 0 ||
               p.Equals(PatientHealth.SelfPatient, StringComparison.OrdinalIgnoreCase) ||
               p.Equals("me",       StringComparison.OrdinalIgnoreCase) ||
               p.Equals("yourself", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A patient name safe to put on a command line: the leading run of
    /// letters, apostrophes and hyphens, stopping at the first anything else.
    /// A separator, a command character or a second word would otherwise let a
    /// typed name smuggle a second command into the send. Empty when the name
    /// does not start with a usable character.
    /// </summary>
    public static string CleanPatient(string? patient)
    {
        var t  = (patient ?? "").Trim();
        var sb = new StringBuilder(t.Length);
        foreach (var c in t)
        {
            if (!(char.IsLetter(c) || c is '\'' or '-')) break;
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>The words DR takes for a region id (<c>leftArm</c> →
    /// <c>left arm</c>, <c>nsys</c> → <c>nerves</c>). Unknown ids pass through.</summary>
    public static string PartWords(string regionId)
    {
        if (string.IsNullOrWhiteSpace(regionId)) return "";
        if (regionId.Equals("nsys", StringComparison.OrdinalIgnoreCase)) return "nerves";
        foreach (var side in new[] { "left", "right" })
            if (regionId.Length > side.Length &&
                regionId.StartsWith(side, StringComparison.OrdinalIgnoreCase) &&
                char.IsUpper(regionId[side.Length]))
                return side + " " + regionId[side.Length..].ToLowerInvariant();
        return regionId.ToLowerInvariant();
    }

    // ── Reading a patient ────────────────────────────────────────────────────

    /// <summary><c>perceive health</c> for the player, <c>perceive health
    /// &lt;patient&gt;</c> for anyone else.</summary>
    public HealingAction Perceive(string? patient)
    {
        if (IsSelf(patient)) return HealingAction.Of("perceive health");
        var p = CleanPatient(patient);
        return p.Length == 0 ? HealingAction.None : HealingAction.Of($"perceive health {p}");
    }

    /// <summary><c>touch &lt;patient&gt;</c> — the empath's diagnosis, which DR
    /// answers with the patient's injuries block. Nothing for the player.</summary>
    public HealingAction Touch(string? patient)
    {
        if (IsSelf(patient)) return HealingAction.None;
        var p = CleanPatient(patient);
        return p.Length == 0 ? HealingAction.None : HealingAction.Of($"touch {p}");
    }

    // ── Healing ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A body region was clicked on the given axis. For a patient: take that
    /// wound. For the player: Heal Wounds on a fresh wound, Heal Scars on a
    /// scar, cast at the part.
    /// </summary>
    public HealingAction HealRegion(string? patient, string regionId, InjuryAxis axis)
    {
        var part = PartWords(regionId);
        if (part.Length == 0) return HealingAction.None;

        if (IsSelf(patient))
            return Cast(IsScar(axis) ? HealingSpell.HS : HealingSpell.HW, part);

        var p = CleanPatient(patient);
        return p.Length == 0 ? HealingAction.None : HealingAction.Of(TakeLine(p, part, axis));
    }

    /// <summary>
    /// Transfer one wound DR's dialog did not mark (public #375): the
    /// <c>transfer</c> verb, with DR's own word order
    /// (<c>transfer &lt;patient&gt; internal &lt;part&gt; [scar]</c>, as DR's
    /// injuries dialog writes it and as typed in the 2026-09-28 session).
    ///
    /// <para>Internal axes only. No external <c>transfer</c> line has been seen
    /// from DR or a community script yet, so an external axis returns nothing
    /// rather than a guessed wording (left-click <c>take</c> covers it). Nothing
    /// for the player's own body.</para>
    /// </summary>
    public HealingAction Transfer(string? patient, string regionId, InjuryAxis axis)
    {
        if (IsSelf(patient) || !CanTransfer(axis)) return HealingAction.None;
        var part = PartWords(regionId);
        var p    = CleanPatient(patient);
        if (part.Length == 0 || p.Length == 0) return HealingAction.None;
        return HealingAction.Of($"transfer {p} internal {part}{(IsScar(axis) ? " scar" : "")}");
    }

    /// <summary>True for the axes <see cref="Transfer"/> has a confirmed
    /// wording for: the two internal ones.</summary>
    public static bool CanTransfer(InjuryAxis axis)
        => axis is InjuryAxis.FreshInternal or InjuryAxis.ScarInternal;

    /// <summary>
    /// Take every wound on <paramref name="chart"/>, one queued <c>take</c> per
    /// wounded region and axis: fresh wounds first, then scars, each worst
    /// first — the order an empath heals in. Nothing for the player's own
    /// chart (the panel heals your own body one region at a time) or a
    /// healthy one.
    /// </summary>
    public HealingAction TakeAll(PatientHealth? chart)
    {
        if (chart is null || chart.IsSelf) return HealingAction.None;
        var p = CleanPatient(chart.Patient);
        if (p.Length == 0) return HealingAction.None;

        var wounds = chart.Regions.Values
            .SelectMany(r => r.Axes
                .Where(a => a.Value > WoundSeverity.None)
                .Select(a => (Region: r.Region, Axis: a.Key, Severity: a.Value)))
            .OrderBy(w => IsScar(w.Axis))                 // fresh before scars
            .ThenByDescending(w => w.Severity)            // worst first
            .ThenBy(w => w.Region, StringComparer.OrdinalIgnoreCase)
            .ThenBy(w => w.Axis)
            .ToList();

        return new HealingAction(wounds
            .Select(w => $"{_commandChar}send {TakeLine(p, PartWords(w.Region), w.Axis)}")
            .ToList());
    }

    /// <summary>
    /// Vitality, poison or disease. For a patient: take it. For the player:
    /// Vitality Healing, Flush Poisons or Cure Disease.
    /// </summary>
    public HealingAction TakeCondition(string? patient, HealingCondition condition)
    {
        if (IsSelf(patient))
            return Cast(condition switch
            {
                HealingCondition.Poison  => HealingSpell.FP,
                HealingCondition.Disease => HealingSpell.CD,
                _                        => HealingSpell.VH,
            }, target: null);

        var p = CleanPatient(patient);
        if (p.Length == 0) return HealingAction.None;
        var what = condition.ToString().ToLowerInvariant();
        return HealingAction.Of($"take {p} {what}{QuickSuffix()}");
    }

    /// <summary>
    /// Prepare <paramref name="spell"/> at its configured mana, then cast — at
    /// <paramref name="target"/> when given, else a bare <c>cast</c>. With a
    /// prep-to-cast wait configured the cast is queued behind it
    /// (<c>#send &lt;wait&gt; cast …</c>); with none, only the prep is sent and
    /// the cast line comes back as <see cref="HealingAction.PendingCast"/> for
    /// the player's Cast click.
    /// </summary>
    public HealingAction Cast(HealingSpell spell, string? target)
    {
        var info = HealingSpells.Get(spell);
        var (mana, delay) = _settings.Resolve(spell, _variable);

        var prep = mana > 0 ? $"prep {info.PrepName} {mana}" : $"prep {info.PrepName}";
        var cast = string.IsNullOrWhiteSpace(target) ? "cast" : $"cast {target.Trim()}";

        return delay > 0
            ? HealingAction.Of(prep, $"{_commandChar}send {HealingSettings.FormatSeconds(delay)} {cast}")
            : new HealingAction(new[] { prep }, PendingCast: cast);
    }

    /// <summary>The panel's Stop: <c>#queue clear</c>, dropping any take or
    /// cast still waiting in the queue.</summary>
    public HealingAction StopQueue() => HealingAction.Of($"{_commandChar}queue clear");

    // ── Helpers ──────────────────────────────────────────────────────────────

    private string TakeLine(string patient, string part, InjuryAxis axis)
    {
        var sb = new StringBuilder("take ").Append(patient).Append(' ').Append(part);
        if (axis is InjuryAxis.FreshInternal or InjuryAxis.ScarInternal) sb.Append(" internal");
        if (IsScar(axis)) sb.Append(" scar");
        sb.Append(QuickSuffix());
        return sb.ToString();
    }

    private string QuickSuffix() => _settings.QuickTake ? " quick" : "";

    private static bool IsScar(InjuryAxis axis)
        => axis is InjuryAxis.ScarExternal or InjuryAxis.ScarInternal;
}
