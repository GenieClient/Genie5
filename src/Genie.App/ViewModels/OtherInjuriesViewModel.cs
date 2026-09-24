using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using Genie.Core.Dialogs;
using Genie.Core.Events;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace Genie.App.ViewModels;

/// <summary>
/// Bespoke view for <c>injuries-&lt;charnum&gt;</c> — another character's wounds
/// (#156 Phase 2, and the injuries half of public #345). DR sends it when an
/// empath looks at a patient: the SAME fifteen body-part <c>&lt;image&gt;</c>s as
/// the player's own injuries dialog, each injured one carrying
/// <c>cmd="transfer &lt;name&gt; &lt;part&gt;"</c>.
///
/// <para>The generic renderer can only list those as sprite names ("head",
/// "neck", "Injury1"…). This shows them the way the Injuries panel does — the
/// same sprites and colour variants — as a grid where an injured part is a
/// button that sends its transfer. Readings reuse the parser's decoder
/// (<see cref="InjuryImageName"/>), so the two windows cannot disagree.</para>
///
/// <para>Deliberately NOT routed into the player's own Injuries panel: that is
/// the player's body, and this window describes someone else's (see the
/// exact-id note on <c>ServerDialogEngine</c>'s exclusions).</para>
/// </summary>
public sealed partial class OtherInjuriesViewModel : ReactiveObject, IServerDialogBespoke
{
    private static readonly Regex IdRe = IdRegex();
    [System.Text.RegularExpressions.GeneratedRegex(@"^injuries-\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex IdRegex();

    public static bool Handles(string dialogId) => IdRe.IsMatch(dialogId ?? "");

    /// <summary>One body region: the shared sprite cell plus the transfer
    /// command DR attached to it, if any.</summary>
    public sealed partial class Part : ReactiveObject
    {
        public Part(InjuriesViewModel.InjuryCell cell, string regionId)
        {
            Cell     = cell;
            RegionId = regionId;
        }

        public InjuriesViewModel.InjuryCell Cell { get; }
        public string RegionId { get; }

        /// <summary>DR attaches <c>cmd</c> only to parts that can be
        /// transferred right now; everything else is display-only.</summary>
        [Reactive] public bool   CanTransfer { get; internal set; }
        [Reactive] public string Tip         { get; internal set; } = "";
    }

    private readonly ServerDialogViewModel _host;
    private readonly InjuriesViewModel     _cells = new();
    private readonly Dictionary<string, Part> _parts = new(StringComparer.OrdinalIgnoreCase);

    public OtherInjuriesViewModel(ServerDialogViewModel host)
    {
        _host = host;
        // Same grid order as the Injuries panel.
        Parts = _cells.Cells.Select(c =>
        {
            var p = new Part(c, c.RegionId) { Tip = c.Tip };
            _parts[c.RegionId] = p;
            return p;
        }).ToList();
    }

    /// <summary>Grid cells, 4 columns, in the Injuries panel's order.</summary>
    public IReadOnlyList<Part> Parts { get; }

    /// <summary>"Renucci's Injuries" — whoever DR says this describes.</summary>
    [Reactive] public string Subject { get; private set; } = "";

    /// <summary>Injured regions in words, with a note on the ones that can be
    /// transferred.</summary>
    public ObservableCollection<string> Injured { get; } = new();

    [Reactive] public bool IsEmpty     { get; private set; } = true;
    [Reactive] public bool AnyTransfer { get; private set; }

    public void Apply(ServerDialogState state)
    {
        Subject = string.IsNullOrWhiteSpace(state.Title) ? "Injuries" : state.Title!;

        foreach (var c in state.Controls)
        {
            if (c.Type != DialogControlType.Image) continue;
            if (!_parts.TryGetValue(c.Id, out var part)) continue;   // unknown region

            c.Attributes.TryGetValue("name", out var name);
            var (kind, severity) = InjuryImageName.Decode(name);
            part.Cell.Set(kind, severity);

            part.CanTransfer = !string.IsNullOrWhiteSpace(c.Cmd);
            c.Attributes.TryGetValue("tooltip", out var tooltip);
            part.Tip = part.CanTransfer
                ? $"{part.Cell.Tip} — click to {(string.IsNullOrWhiteSpace(tooltip) ? c.Cmd : tooltip)}"
                : part.Cell.Tip;
        }

        Injured.Clear();
        foreach (var p in Parts.Where(p => p.Cell.Kind != InjuryKind.None))
            Injured.Add($"{p.Cell.FullName} — {InjuriesViewModel.InjuryCell.KindWord(p.Cell.Kind)} ({p.Cell.Severity})"
                        + (p.CanTransfer ? "  ·  transferable" : ""));
        IsEmpty     = Injured.Count == 0;
        AnyTransfer = Parts.Any(p => p.CanTransfer);
    }

    /// <summary>A part was clicked: send its transfer through the host, which
    /// resolves and escapes it like any other dialog command.</summary>
    public void Transfer(Part part)
    {
        if (part.CanTransfer) _host.Activate(part.RegionId);
    }
}
