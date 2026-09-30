using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using Genie.Core.Dialogs;
using Genie.Core.Events;
using Genie.Core.Health;
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
///
/// <para>It IS carried into the Healing window (public #263): that window
/// subscribes to <see cref="Rendered"/> and shows this patient's bar, buttons
/// and DR-marked transfers, every click still coming back through
/// <see cref="ActivateExtra"/> / <see cref="TransferRegion"/> and so through the
/// host. By default this window itself fills in hidden
/// (<c>ServerDialogMappings.OpensQuietlyByDefault</c>).</para>
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
        /// <summary>Shown from the empath's own touch/perceive reading, not
        /// from DR's dialog (which filters by display mode).</summary>
        [Reactive] public bool   FromReading { get; internal set; }
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

    /// <summary>
    /// The dialog's own non-sprite controls: the vitality bar, Transfer Vit,
    /// Re-Link and any label or link DR adds. The generic grid used to show
    /// these (drawn underneath this view by mistake, public #374); with the grid
    /// hidden they have to be carried here, or the empath loses the buttons.
    /// The same VM instances as the host's, so values update in place.
    /// </summary>
    public ObservableCollection<DialogControlViewModel> Extras { get; } = new();

    [Reactive] public bool IsEmpty     { get; private set; } = true;
    [Reactive] public bool AnyTransfer { get; private set; }

    // What DR's dialog last said about each part, kept apart from the display
    // so a touch/perceive reading can be merged without losing it.
    private readonly Dictionary<string, (InjuryKind Kind, int Severity, string? Cmd, string? Tooltip)> _dialog =
        new(StringComparer.OrdinalIgnoreCase);

    private Func<string, PatientHealth?>? _readingFor;
    private PatientHealth? _reading;
    private IDisposable?   _readingSub;

    /// <summary>"Renucci" from DR's "Renucci's Injuries" title: the patient a
    /// touch/perceive reading must name to be merged here.</summary>
    public string Patient { get; private set; } = "";

    /// <summary>
    /// Feed this window the empath's own touch/perceive readings (public #263).
    /// DR draws <c>injuries-&lt;charnum&gt;</c> through the VIEWER's injury
    /// display mode (the E/I Wound/Scar/Both radios), so a patient with only
    /// external wounds reads as "No injuries." under an internal mode, directly
    /// under a touch that lists them (2026-09-28 walk). A reading for the same
    /// patient fills in the parts DR left blank; DR's own markings still win,
    /// and only DR-marked parts carry a transfer command.
    /// </summary>
    public void AttachReadings(Func<string, PatientHealth?> readingFor, IObservable<PatientHealth> readings)
    {
        _readingFor = readingFor;
        _readingSub?.Dispose();
        _readingSub = readings.Subscribe(ApplyReading);
    }

    /// <summary>A touch/perceive reading landed; merge it if it is this patient's.</summary>
    public void ApplyReading(PatientHealth chart)
    {
        if (Patient.Length == 0 || !string.Equals(chart.Patient, Patient, StringComparison.OrdinalIgnoreCase)) return;
        _reading = chart;
        Render();
    }

    public void Apply(ServerDialogState state)
    {
        Subject = string.IsNullOrWhiteSpace(state.Title) ? "Injuries" : state.Title!;
        Patient = PatientFrom(Subject);

        foreach (var c in state.Controls)
        {
            if (c.Type != DialogControlType.Image) continue;
            if (!_parts.ContainsKey(c.Id)) continue;   // unknown region

            c.Attributes.TryGetValue("name", out var name);
            c.Attributes.TryGetValue("tooltip", out var tooltip);
            var (kind, severity) = InjuryImageName.Decode(name);
            _dialog[c.Id] = (kind, severity, string.IsNullOrWhiteSpace(c.Cmd) ? null : c.Cmd, tooltip);
        }

        Extras.Clear();
        foreach (var c in _host.Controls.Concat(_host.BottomControls))
            if (c is not DialogImageViewModel) Extras.Add(c);

        if (_reading is null && _readingFor is not null && Patient.Length > 0)
            _reading = _readingFor(Patient);
        Render();
    }

    private void Render()
    {
        foreach (var part in Parts)
        {
            _dialog.TryGetValue(part.RegionId, out var d);
            var fromReading = false;
            var (kind, severity) = (d.Kind, d.Severity);
            if (kind == InjuryKind.None && FromReading(part.RegionId) is { } r)
            {
                (kind, severity) = r;
                fromReading = true;
            }
            part.Cell.Set(kind, severity);

            part.CanTransfer = d.Cmd is not null;
            part.FromReading = fromReading;
            part.Tip = part.CanTransfer
                ? $"{part.Cell.Tip} — click to {(string.IsNullOrWhiteSpace(d.Tooltip) ? d.Cmd : d.Tooltip)}"
                : fromReading
                    ? $"{part.Cell.Tip} — from your last touch; not transferable in this view"
                    : part.Cell.Tip;
        }

        Injured.Clear();
        foreach (var p in Parts.Where(p => p.Cell.Kind != InjuryKind.None))
            Injured.Add($"{p.Cell.FullName} — {InjuriesViewModel.InjuryCell.KindWord(p.Cell.Kind)} ({p.Cell.Severity})"
                        + (p.CanTransfer ? "  ·  transferable" : p.FromReading ? "  ·  from your touch" : ""));
        IsEmpty     = Injured.Count == 0;
        AnyTransfer = Parts.Any(p => p.CanTransfer);
        Rendered?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised after every render (a dialog delta or a merged
    /// reading), so the Healing window can follow this patient's dialog
    /// without the window being open (public #263).</summary>
    public event EventHandler? Rendered;

    /// <summary>DR's own words for the transfer it attached to
    /// <paramref name="regionId"/> (its tooltip, else the cmd), or null when DR
    /// marked nothing there.</summary>
    public string? TransferHint(string regionId)
        => _dialog.TryGetValue(regionId, out var d) && d.Cmd is not null
            ? (string.IsNullOrWhiteSpace(d.Tooltip) ? d.Cmd : d.Tooltip)
            : null;

    /// <summary>Send DR's transfer for <paramref name="regionId"/> through the
    /// host. False (and nothing sent) when DR marked nothing there.</summary>
    public bool TransferRegion(string regionId)
    {
        if (TransferHint(regionId) is null) return false;
        _host.Activate(regionId);
        return true;
    }

    /// <summary>The reading's worst wound for a region, on the dialog's sprite
    /// scale: kind from the axes present (fresh beats scar; nerves are always
    /// "damage"), severity folded from DR's 13 rungs onto the sprite's three.</summary>
    private (InjuryKind Kind, int Severity)? FromReading(string regionId)
    {
        if (_reading is null || !_reading.Regions.TryGetValue(regionId, out var region)) return null;
        var fresh = Worst(region, InjuryAxis.FreshExternal, InjuryAxis.FreshInternal);
        var scar  = Worst(region, InjuryAxis.ScarExternal,  InjuryAxis.ScarInternal);
        var worst = (WoundSeverity)Math.Max((int)fresh, (int)scar);
        if (worst == WoundSeverity.None) return null;
        var kind = regionId.Equals("nsys", StringComparison.OrdinalIgnoreCase) ? InjuryKind.Damage
                 : fresh != WoundSeverity.None ? InjuryKind.Wound : InjuryKind.Scar;
        return (kind, SpriteLevel(worst));
    }

    private static WoundSeverity Worst(RegionInjuries r, InjuryAxis a, InjuryAxis b)
    {
        r.Axes.TryGetValue(a, out var x);
        r.Axes.TryGetValue(b, out var y);
        return (WoundSeverity)Math.Max((int)x, (int)y);
    }

    /// <summary>1-4 -> 1, 5-8 -> 2, 9-13 -> 3: the 13-rung ladder onto the
    /// dialog's three sprite levels.</summary>
    internal static int SpriteLevel(WoundSeverity s) => (int)s switch
    {
        <= 0 => 0,
        <= 4 => 1,
        <= 8 => 2,
        _    => 3,
    };

    private static string PatientFrom(string subject)
    {
        const string suffix = "'s Injuries";
        return subject.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? subject[..^suffix.Length].Trim()
            : "";
    }

    /// <summary>A part was clicked: send its transfer through the host, which
    /// resolves and escapes it like any other dialog command.</summary>
    public void Transfer(Part part)
    {
        if (part.CanTransfer) _host.Activate(part.RegionId);
    }

    /// <summary>One of <see cref="Extras"/> was clicked (Transfer Vit, Re-Link):
    /// the same host path every generic dialog control uses.</summary>
    public void ActivateExtra(string controlId) => _host.Activate(controlId);
}
