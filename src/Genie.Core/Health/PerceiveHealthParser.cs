using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Genie.Core.Health;

/// <summary>
/// Line-at-a-time parser for DR's <c>perceive health</c> / <c>&lt;patient&gt;'s
/// injuries include</c> plain-text block (public #277).
///
/// <para><b>Why this exists alongside the injuries dialog.</b> The dialog is a
/// strictly poorer source: self only, severity 1–3, and one axis per region
/// chosen by the player's display mode. This block carries any patient, the
/// true 1–13 ladder, and all four Fresh/Scars × External/Internal axes at once
/// — which is the whole reason an empath reads it.</para>
///
/// <para><b>Grammar provenance.</b> Verified against the community
/// <c>empath_heal.cmd</c> in Tirost/DR-Genie-Scripts, which carries the
/// complete pattern set and the full thirteen-rung ladder as separate
/// <c>action</c> lines. The block framing comes from #277.</para>
///
/// <para>Feed it lines and take finished readings with
/// <see cref="TakeCompleted"/>. It never throws on unrecognised text — a
/// perceive block is surrounded by ordinary game output, so "not part of this
/// block" is the common case, not an error.</para>
/// </summary>
public sealed class PerceiveHealthParser
{
    // ── Grammar ──────────────────────────────────────────────────────────────

    /// <summary>Block openers. DR uses the possessive form for a patient and
    /// "Your" for the player.</summary>
    private static readonly Regex SelfOpenRe = new(
        @"^Your injuries include", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PatientOpenRe = new(
        @"^(?<name>.+?)'s injuries include", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>The patient identity DR prints after a perceive / mind-inward
    /// open, before the block itself.</summary>
    private static readonly Regex PresenceRe = new(
        @"^\s*The presence of (?<name>.+?)[.,]\s*$", RegexOptions.Compiled);

    /// <summary>Paired regions — "Wounds to the LEFT ARM:". Checked before the
    /// single-word form, which would otherwise capture only "LEFT".</summary>
    private static readonly Regex SidedRegionRe = new(
        @"^\s*Wounds to the (?<side>LEFT|RIGHT) (?<part>\w+):\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex RegionRe = new(
        @"^\s*Wounds to the (?<part>\w+):\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>An axis reading. The severity may arrive bare
    /// (<c>-- harmful</c>) or with the numeric suffix DR shows in some display
    /// modes (<c>-- harmful (5/13)</c>); both forms are accepted.</summary>
    private static readonly Regex AxisRe = new(
        @"^\s*(?<kind>Fresh|Scars)\s+(?<depth>External|Internal):.*?--\s*(?<severity>[a-z ]+?)\s*(?:\(\d+/\d+\))?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Vitality line — the percentage DR prints in parentheses.</summary>
    private static readonly Regex VitalityRe = new(
        @"\((?<pct>\d{1,3})%\)", RegexOptions.Compiled);

    /// <summary>Block terminators. "has/have … vitality" is the one the
    /// community script waits on (<c>waitforre ^You .+ vitality</c>).</summary>
    private static readonly Regex VitalityLineRe = new(
        @"\b(?:has|have)\b.*\bvitality\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>The canonical thirteen rungs, in order. Index + 1 is the
    /// severity, so the ladder is defined once and read both ways.</summary>
    private static readonly string[] Ladder =
    {
        "insignificant", "negligible", "minor", "more than minor",
        "harmful", "very harmful", "damaging", "very damaging",
        "severe", "very severe", "devastating", "very devastating", "useless",
    };

    /// <summary>
    /// Region ids, normalised to the SAME spellings the injuries dialog uses so
    /// a perceive reading joins onto <c>GameState.Injuries</c> directly.
    /// <c>skin</c> and <c>tail</c> have no dialog counterpart and keep their own
    /// lowercase id.
    /// </summary>
    private static readonly Dictionary<string, string> RegionIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["head"]    = "head",    ["neck"]    = "neck",
        ["chest"]   = "chest",   ["abdomen"] = "abdomen",
        ["back"]    = "back",    ["skin"]    = "skin",
        ["tail"]    = "tail",    ["nerves"]  = "nsys",
    };

    /// <summary>Paired regions, keyed by the bare part name. The side prefix
    /// picks the camelCase id.</summary>
    private static readonly Dictionary<string, (string Left, string Right)> SidedIds =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["eye"]  = ("leftEye",  "rightEye"),
            ["arm"]  = ("leftArm",  "rightArm"),
            ["hand"] = ("leftHand", "rightHand"),
            ["leg"]  = ("leftLeg",  "rightLeg"),
            ["foot"] = ("leftFoot", "rightFoot"),
        };

    // ── State ────────────────────────────────────────────────────────────────

    private readonly Func<DateTimeOffset> _now;

    private bool    _inBlock;
    private string  _patient       = "";
    private string? _pendingName;              // from "The presence of X."
    private string  _currentRegion = "";
    private int?    _rawVitality;
    private bool    _poisoned;
    private bool    _diseased;

    private readonly Dictionary<string, Dictionary<InjuryAxis, WoundSeverity>> _regions =
        new(StringComparer.OrdinalIgnoreCase);

    public PerceiveHealthParser(Func<DateTimeOffset>? now = null)
        => _now = now ?? (static () => DateTimeOffset.UtcNow);

    /// <summary>True while a block is open.</summary>
    public bool InBlock => _inBlock;

    /// <summary>The reading produced by the most recently CLOSED block, or null
    /// if none has completed. Cleared when the next block opens, or by
    /// <see cref="TakeCompleted"/>.</summary>
    public PatientHealth? Completed { get; private set; }

    /// <summary>
    /// Take the completed reading and clear it, so a caller polling after every
    /// line publishes each block exactly once.
    ///
    /// <para>This is the right way to consume it, because
    /// <see cref="Feed"/>'s return value cannot be used as the signal: a
    /// TERMINATOR both closes the block and returns false, since the terminator
    /// itself is not part of the block and still has to reach its own consumers
    /// (a swallowed Roundtime line would stall every RT-gated script). Gating
    /// on the return value therefore drops exactly the readings that ended the
    /// normal way.</para>
    /// </summary>
    public PatientHealth? TakeCompleted()
    {
        var done = Completed;
        Completed = null;
        return done;
    }

    /// <summary>Abandon any open block — a disconnect or room change means the
    /// half-read chart is no longer about anybody.</summary>
    public void Reset()
    {
        _inBlock = false;
        _pendingName = null;
        ResetBlock();
    }

    /// <summary>
    /// Feed one line. Returns true when the line belonged to a perceive block
    /// (so a caller may suppress or tag it); false for ordinary game text.
    ///
    /// <para>The return value is NOT a "did a block complete" signal: a
    /// terminator closes the block and returns false, because the terminator
    /// belongs to the surrounding text. Poll <see cref="TakeCompleted"/> after
    /// every line instead.</para>
    /// </summary>
    public bool Feed(string? line)
    {
        if (line is null) return false;

        // Patient identity arrives BEFORE the block opens, so it is tracked
        // outside it. Kept until the next open consumes it.
        var presence = PresenceRe.Match(line);
        if (presence.Success)
        {
            _pendingName = presence.Groups["name"].Value.Trim();
            return true;
        }

        // ── Open ─────────────────────────────────────────────────────────────
        if (SelfOpenRe.IsMatch(line))
        {
            Open(PatientHealth.SelfPatient);
            return true;
        }
        var patientOpen = PatientOpenRe.Match(line);
        if (patientOpen.Success)
        {
            Open(patientOpen.Groups["name"].Value.Trim());
            return true;
        }

        if (!_inBlock) return false;

        // ── Close ────────────────────────────────────────────────────────────
        // The vitality line is both a terminator AND the vitality reading, so
        // it is read before the block is closed rather than after.
        if (VitalityLineRe.IsMatch(line))
        {
            ReadVitality(line);
            Close();
            return true;
        }
        if (IsTerminator(line))
        {
            Close();
            // A terminator is not part of the block — report it as ordinary
            // text so a prompt or Roundtime line still reaches its consumers.
            return false;
        }

        // ── Body ─────────────────────────────────────────────────────────────
        var sided = SidedRegionRe.Match(line);
        if (sided.Success)
        {
            _currentRegion = SidedRegionId(sided.Groups["side"].Value, sided.Groups["part"].Value);
            return true;
        }

        var region = RegionRe.Match(line);
        if (region.Success)
        {
            _currentRegion = RegionId(region.Groups["part"].Value);
            return true;
        }

        var axis = AxisRe.Match(line);
        if (axis.Success && _currentRegion.Length > 0)
        {
            var severity = ParseSeverity(axis.Groups["severity"].Value);
            if (severity != WoundSeverity.None)
                Record(_currentRegion,
                       AxisOf(axis.Groups["kind"].Value, axis.Groups["depth"].Value),
                       severity);
            return true;
        }

        // Poison / disease markers appear as trailing lines inside the block.
        if (line.Contains("poison", StringComparison.OrdinalIgnoreCase)) { _poisoned = true; return true; }
        if (line.Contains("disease", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("infect",  StringComparison.OrdinalIgnoreCase)) { _diseased = true; return true; }

        // Vitality can also arrive on its own line inside the block.
        if (VitalityRe.IsMatch(line)) { ReadVitality(line); return true; }

        // Unrecognised line inside a block: skip it and keep reading. Aborting
        // here is the mistake #355 was — one unknown wording should cost one
        // line, not the rest of the chart.
        return true;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void Open(string patient)
    {
        ResetBlock();
        _inBlock = true;
        Completed = null;
        // "Name's injuries include" wins over a remembered presence line: it is
        // the block's own statement of who it is about.
        _patient = patient == PatientHealth.SelfPatient && _pendingName is { Length: > 0 }
            ? PatientHealth.SelfPatient          // "Your injuries" is always the player
            : patient;
        _pendingName = null;
    }

    private void Close()
    {
        var regions = new Dictionary<string, RegionInjuries>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, axes) in _regions)
            regions[id] = new RegionInjuries { Region = id, Axes = axes };

        Completed = new PatientHealth
        {
            Patient            = _patient,
            Regions            = regions,
            VitalityRawPercent = _rawVitality,
            VitalityPercent    = _rawVitality is { } raw ? 100 - raw : null,
            IsPoisoned         = _poisoned,
            IsDiseased         = _diseased,
            CapturedAt         = _now(),
        };

        _inBlock = false;
        ResetBlock();
    }

    private void ResetBlock()
    {
        _regions.Clear();
        _patient       = "";
        _currentRegion = "";
        _rawVitality   = null;
        _poisoned      = false;
        _diseased      = false;
    }

    private void Record(string region, InjuryAxis axis, WoundSeverity severity)
    {
        if (!_regions.TryGetValue(region, out var axes))
            _regions[region] = axes = new Dictionary<InjuryAxis, WoundSeverity>();
        axes[axis] = severity;
    }

    private void ReadVitality(string line)
    {
        var m = VitalityRe.Match(line);
        if (m.Success && int.TryParse(m.Groups["pct"].Value, out var pct) && pct is >= 0 and <= 100)
            _rawVitality = pct;
    }

    private static bool IsTerminator(string line) =>
        line.StartsWith("Roundtime", StringComparison.OrdinalIgnoreCase) ||
        line.StartsWith("You sense (", StringComparison.OrdinalIgnoreCase) ||
        line.TrimStart().StartsWith(">", StringComparison.Ordinal);

    private static InjuryAxis AxisOf(string kind, string depth)
    {
        var fresh    = kind.StartsWith("Fresh",    StringComparison.OrdinalIgnoreCase);
        var external = depth.StartsWith("External", StringComparison.OrdinalIgnoreCase);
        return fresh
            ? (external ? InjuryAxis.FreshExternal : InjuryAxis.FreshInternal)
            : (external ? InjuryAxis.ScarExternal  : InjuryAxis.ScarInternal);
    }

    /// <summary>
    /// Map a severity word to its rung. Matched against the LONGEST rungs first
    /// so "very harmful" cannot be read as "harmful" — every qualified rung
    /// contains its unqualified twin as a suffix, which is the trap in this
    /// ladder.
    /// </summary>
    public static WoundSeverity ParseSeverity(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return WoundSeverity.None;
        var t = text.Trim().ToLowerInvariant();

        // Exact first — the common case, and unambiguous.
        for (int i = 0; i < Ladder.Length; i++)
            if (t == Ladder[i]) return (WoundSeverity)(i + 1);

        // Then longest-wins containment, for a line that carried extra words.
        var best = WoundSeverity.None;
        var bestLen = 0;
        for (int i = 0; i < Ladder.Length; i++)
        {
            if (Ladder[i].Length <= bestLen) continue;
            if (t.Contains(Ladder[i], StringComparison.Ordinal))
            {
                best    = (WoundSeverity)(i + 1);
                bestLen = Ladder[i].Length;
            }
        }
        return best;
    }

    /// <summary>The display word for a rung — the inverse of
    /// <see cref="ParseSeverity"/>.</summary>
    public static string SeverityName(WoundSeverity severity) =>
        severity == WoundSeverity.None ? "none" : Ladder[(int)severity - 1];

    private static string RegionId(string part)
        => RegionIds.TryGetValue(part, out var id) ? id : part.ToLowerInvariant();

    private static string SidedRegionId(string side, string part)
    {
        if (!SidedIds.TryGetValue(part, out var pair))
            return $"{side.ToLowerInvariant()} {part.ToLowerInvariant()}";
        return side.Equals("LEFT", StringComparison.OrdinalIgnoreCase) ? pair.Left : pair.Right;
    }
}
