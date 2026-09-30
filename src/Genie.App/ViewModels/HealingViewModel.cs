using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Avalonia.Media;
using Genie.Core;
using Genie.Core.Events;
using Genie.Core.Health;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace Genie.App.ViewModels;

/// <summary>
/// Backs the dockable Healing panel (public #263) — an Empath's healing console
/// in the spirit of Genie 4's Crutch, rebuilt on Genie 5's own
/// <c>perceive health</c> parser (public #277).
///
/// <para><b>What it shows.</b> The latest reading for each patient
/// (<see cref="Patients"/>, one row per patient, kept until forgotten), the
/// selected patient's body as a grid of regions coloured by severity on DR's
/// true 1–13 ladder, one wound axis at a time
/// (<see cref="SelectedAxis"/> — Fresh/Scar × External/Internal), plus
/// vitality, poison and disease. The words list under the grid spells out
/// every axis, so severity never rides on colour alone.</para>
///
/// <para><b>DR's injuries dialog.</b> When DR sends
/// <c>injuries-&lt;charnum&gt;</c> for the acting-on patient
/// (<see cref="DrDialog"/>), the header shows its health bar and its Transfer
/// Vit / Re-Link buttons, and every part DR marked transferable carries a ⇄
/// marker: left-click still takes, right-click sends DR's transfer. That
/// dialog's own window fills in hidden by default.</para>
///
/// <para><b>Send policy.</b> Every command this panel sends goes through
/// <see cref="Dispatch"/>, and <see cref="Dispatch"/> is reached only from the
/// public click entry points (<see cref="Perceive"/>, <see cref="Touch"/>,
/// <see cref="HealRegion"/>, <see cref="TransferRegion"/>, <see cref="TakeAll"/>, <see cref="TakeCondition"/>,
/// <see cref="CastSpell"/>, <see cref="CastPending"/>, <see cref="Stop"/>).
/// The two DR-dialog entry points (<see cref="TransferRegion"/> on a part DR
/// marked, <see cref="ActivateDialogControl"/>) send DR's own command through
/// that dialog's host instead, so it is resolved and separator-escaped like a
/// click in DR's window.
/// A reading arriving, a dialog arriving, a selection changing, or a setting
/// being edited never sends. There is no timer. Queued sequences (Take All, prepare-then-cast)
/// ride the player's own roundtime-gated <c>#send</c> queue — see
/// <see cref="HealingCommandBuilder"/>.</para>
///
/// Hidden by default; re-open via Window → Healing.
/// </summary>
public sealed class HealingViewModel : ReactiveObject
{
    // ── Nested rows ──────────────────────────────────────────────────────────

    /// <summary>One body region in the diagram.</summary>
    public sealed class RegionCell : ReactiveObject
    {
        public RegionCell(string regionId, string label, string fullName, int row, int column)
        {
            RegionId = regionId;
            Label    = label;
            FullName = fullName;
            Row      = row;
            Column   = column;
            _baseTip = $"{fullName} — healthy";
            Tip      = _baseTip;
        }

        private string  _baseTip;
        private string? _rightClick;

        /// <summary>Region id as the parser reports it (<c>leftArm</c>, <c>nsys</c>).</summary>
        public string RegionId { get; }
        public string Label    { get; }
        public string FullName { get; }
        public int    Row      { get; }
        public int    Column   { get; }

        /// <summary>Severity on the selected axis.</summary>
        [Reactive] public WoundSeverity Severity   { get; private set; }
        /// <summary>"7" for rung 7; empty when healthy.</summary>
        [Reactive] public string        Rung       { get; private set; } = "";
        [Reactive] public IBrush        Fill       { get; private set; } = HealthyFill;
        [Reactive] public IBrush        Foreground { get; private set; } = HealthyFg;
        [Reactive] public string        Tip        { get; private set; }
        /// <summary>True when the selected axis has a wound here — the cell is
        /// then a live heal button.</summary>
        [Reactive] public bool          IsWounded  { get; private set; }
        /// <summary>DR's own injuries dialog for this patient marked this part
        /// transferable (the ⇄ marker); a right-click sends DR's transfer.</summary>
        [Reactive] public bool          DrTransfer { get; private set; }

        internal void Set(RegionInjuries? region, InjuryAxis axis)
        {
            var s = region?[axis] ?? WoundSeverity.None;
            Severity   = s;
            IsWounded  = s > WoundSeverity.None;
            Rung       = IsWounded ? ((int)s).ToString() : "";
            Fill       = FillFor(s);
            Foreground = ForegroundFor(s);
            _baseTip   = region is null || region.Worst == WoundSeverity.None
                ? $"{FullName} — healthy"
                : FullName + "\n" + string.Join("\n",
                    AxisOptions.Select(o => $"{o.Label}: {Word(region[o.Axis])}"));
            ComposeTip();
        }

        /// <summary>What a right-click on this tile would send, if anything,
        /// and whether that is DR's own marked transfer.</summary>
        internal void SetTransfer(bool drMarked, string? rightClick)
        {
            DrTransfer  = drMarked;
            _rightClick = rightClick;
            ComposeTip();
        }

        private void ComposeTip()
            => Tip = _rightClick is null ? _baseTip : $"{_baseTip}\nright-click: {_rightClick}";
    }

    /// <summary>One patient's latest reading.</summary>
    public sealed class PatientEntry : ReactiveObject
    {
        internal PatientEntry(PatientHealth chart) => Update(chart);

        /// <summary>The parser's patient key (<c>self</c> or the name).</summary>
        public string Key => Chart.Patient;
        [Reactive] public PatientHealth Chart   { get; private set; } = null!;
        [Reactive] public string        Display { get; private set; } = "";

        internal void Update(PatientHealth chart)
        {
            Chart = chart;
            var name   = chart.IsSelf ? "Yourself" : chart.Patient;
            var count  = chart.Regions.Values.Count(r => r.Worst > WoundSeverity.None);
            var worst  = chart.Regions.Count == 0 ? WoundSeverity.None : chart.Regions.Values.Max(r => r.Worst);
            Display = count == 0
                ? $"{name} — no wounds ({chart.CapturedAt.ToLocalTime():HH:mm})"
                : $"{name} — {count} region{(count == 1 ? "" : "s")}, worst {(int)worst}/13 ({chart.CapturedAt.ToLocalTime():HH:mm})";
        }
    }

    /// <summary>One wound axis in the selector.</summary>
    public sealed record AxisOption(InjuryAxis Axis, string Label)
    {
        public override string ToString() => Label;
    }

    /// <summary>One spell's editable mana / wait, backed by <see cref="HealingSettings"/>.</summary>
    public sealed class SpellRow : ReactiveObject
    {
        private readonly HealingViewModel _owner;
        private decimal? _mana;
        private decimal? _delay;

        internal SpellRow(HealingViewModel owner, HealingSpellInfo info)
        {
            _owner = owner;
            Info   = info;
        }

        public HealingSpellInfo Info { get; }
        public string Abbreviation => Info.Abbreviation;
        public string Name         => Info.Name;
        public string Tip          => $"{Info.Name} — prep {Info.PrepName}. Script variables ${Info.ManaVariable} / ${Info.DelayVariable}.";

        /// <summary>Mana to prepare at; empty = the Genie 4 variable, else the minimum.</summary>
        public decimal? Mana
        {
            get => _mana;
            set
            {
                if (_mana == value) return;
                this.RaiseAndSetIfChanged(ref _mana, value);
                if (!_owner._refreshing)
                    _owner.EditSetting(s => s.SetMana(Info.Spell, value is { } v ? (int)v : null));
            }
        }

        /// <summary>Seconds between prep and cast; 0 = prepare only, Cast by click.</summary>
        public decimal? Delay
        {
            get => _delay;
            set
            {
                if (_delay == value) return;
                this.RaiseAndSetIfChanged(ref _delay, value);
                if (!_owner._refreshing)
                    _owner.EditSetting(s => s.SetDelay(Info.Spell, value is { } v ? (double)v : null));
            }
        }

        internal void Load(HealingSettings s)
        {
            var m = s.GetMana(Info.Spell);
            var d = s.GetDelay(Info.Spell);
            Mana  = m is { } mv ? (decimal?)mv : null;
            Delay = d is { } dv ? (decimal?)dv : null;
        }
    }

    // ── Static tables ────────────────────────────────────────────────────────

    public static IReadOnlyList<AxisOption> AxisOptions { get; } = new[]
    {
        new AxisOption(InjuryAxis.FreshExternal, "Fresh External"),
        new AxisOption(InjuryAxis.FreshInternal, "Fresh Internal"),
        new AxisOption(InjuryAxis.ScarExternal,  "Scar External"),
        new AxisOption(InjuryAxis.ScarInternal,  "Scar Internal"),
    };

    /// <summary>
    /// Severity fill per rung, index = rung (0 = healthy). A green → yellow →
    /// red ramp across all thirteen rungs, so the four worst severities —
    /// which Genie 4's Crutch drew in one shade of red — stay distinguishable.
    /// </summary>
    private static readonly IBrush[] Ramp = BuildRamp();
    private static readonly IBrush HealthyFill = new SolidColorBrush(Color.Parse("#26808080"));
    private static readonly IBrush HealthyFg   = new SolidColorBrush(Color.Parse("#8a8a8a"));
    private static readonly IBrush DarkFg      = new SolidColorBrush(Color.Parse("#141414"));
    private static readonly IBrush LightFg     = new SolidColorBrush(Color.Parse("#f4f4f4"));

    private static IBrush[] BuildRamp()
    {
        // Anchors: rung 1 soft green, rung 6 amber, rung 13 deep red.
        (int Rung, Color C)[] anchors =
        {
            (1,  Color.Parse("#7cc47c")),
            (6,  Color.Parse("#e8c14a")),
            (10, Color.Parse("#e0662e")),
            (13, Color.Parse("#8e1010")),
        };
        var ramp = new IBrush[14];
        ramp[0] = HealthyFill;
        for (int rung = 1; rung <= 13; rung++)
        {
            int i = 0;
            while (i < anchors.Length - 2 && rung > anchors[i + 1].Rung) i++;
            var (r0, c0) = anchors[i];
            var (r1, c1) = anchors[i + 1];
            var t = r1 == r0 ? 0 : (double)(rung - r0) / (r1 - r0);
            byte Lerp(byte a, byte b) => (byte)Math.Round(a + (b - a) * t);
            ramp[rung] = new SolidColorBrush(Color.FromRgb(Lerp(c0.R, c1.R), Lerp(c0.G, c1.G), Lerp(c0.B, c1.B)));
        }
        return ramp;
    }

    internal static IBrush FillFor(WoundSeverity s) => Ramp[Math.Clamp((int)s, 0, 13)];

    private static IBrush ForegroundFor(WoundSeverity s) => s switch
    {
        WoundSeverity.None                   => HealthyFg,
        <= WoundSeverity.VeryDamaging        => DarkFg,   // light ramp end
        _                                    => LightFg,
    };

    private static string Word(WoundSeverity s)
        => s == WoundSeverity.None ? "—" : $"{PerceiveHealthParser.SeverityName(s)} ({(int)s}/13)";

    // ── State ────────────────────────────────────────────────────────────────

    private HealingSettings _settings;
    private Action<string>? _send;
    private Func<char> _commandChar = static () => '#';
    private Func<string, string?>? _variable;
    private Action? _save;
    private bool _refreshing;
    private readonly Dictionary<string, RegionCell> _cellsById;

    public HealingViewModel(HealingSettings? settings = null)
    {
        _settings = settings ?? new HealingSettings();

        // Three body columns with the patient's RIGHT side on the viewer's
        // left (the doll convention the Injuries panel uses), plus a fourth
        // column for the regions that have no place on the silhouette.
        Cells = new[]
        {
            new RegionCell("rightEye",  "R Eye",   "Right Eye",      0, 0),
            new RegionCell("head",      "Head",    "Head",           0, 1),
            new RegionCell("leftEye",   "L Eye",   "Left Eye",       0, 2),
            new RegionCell("back",      "Back",    "Back",           0, 3),
            new RegionCell("neck",      "Neck",    "Neck",           1, 1),
            new RegionCell("skin",      "Skin",    "Skin",           1, 3),
            new RegionCell("rightArm",  "R Arm",   "Right Arm",      2, 0),
            new RegionCell("chest",     "Chest",   "Chest",          2, 1),
            new RegionCell("leftArm",   "L Arm",   "Left Arm",       2, 2),
            new RegionCell("nsys",      "Nerves",  "Nervous System", 2, 3),
            new RegionCell("rightHand", "R Hand",  "Right Hand",     3, 0),
            new RegionCell("abdomen",   "Abdomen", "Abdomen",        3, 1),
            new RegionCell("leftHand",  "L Hand",  "Left Hand",      3, 2),
            new RegionCell("tail",      "Tail",    "Tail",           3, 3),
            new RegionCell("rightLeg",  "R Leg",   "Right Leg",      4, 0),
            new RegionCell("leftLeg",   "L Leg",   "Left Leg",       4, 2),
            new RegionCell("rightFoot", "R Foot",  "Right Foot",     5, 0),
            new RegionCell("leftFoot",  "L Foot",  "Left Foot",      5, 2),
        };
        _cellsById = Cells.ToDictionary(c => c.RegionId, StringComparer.OrdinalIgnoreCase);

        Spells = HealingSpells.All.Select(i => new SpellRow(this, i)).ToList();
        _selectedAxis = AxisOptions[0];
        RefreshSettings();
        _settings.Changed += OnSettingsChanged;
    }

    /// <summary>The body diagram, in grid order.</summary>
    public IReadOnlyList<RegionCell> Cells { get; }

    /// <summary>The latest reading per patient, most recent first.</summary>
    public ObservableCollection<PatientEntry> Patients { get; } = new();

    /// <summary>Every wound on the selected patient, all four axes, in words.</summary>
    public ObservableCollection<string> Wounds { get; } = new();

    /// <summary>Per-spell mana and wait.</summary>
    public IReadOnlyList<SpellRow> Spells { get; }

    private PatientEntry? _selectedPatient;
    /// <summary>The patient whose chart the diagram shows. Choosing one also
    /// fills <see cref="PatientName"/>, which is who the buttons act on.</summary>
    public PatientEntry? SelectedPatient
    {
        get => _selectedPatient;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedPatient, value);
            if (value is not null)
                PatientName = value.Chart.IsSelf ? "" : value.Chart.Patient;
            Render();
        }
    }

    private string _patientName = "";
    /// <summary>Who Touch / Perceive / the heal buttons act on. Blank means
    /// yourself.</summary>
    public string PatientName
    {
        get => _patientName;
        set
        {
            this.RaiseAndSetIfChanged(ref _patientName, value ?? "");
            this.RaisePropertyChanged(nameof(IsSelf));
            this.RaisePropertyChanged(nameof(TargetLabel));
            RefreshDialog();
        }
    }

    /// <summary>True when the buttons act on the player's own body.</summary>
    public bool IsSelf => HealingCommandBuilder.IsSelf(PatientName);

    /// <summary>"Acting on: Naper" / "Acting on: yourself".</summary>
    public string TargetLabel => IsSelf
        ? "Acting on: yourself"
        : $"Acting on: {HealingCommandBuilder.CleanPatient(PatientName)}";

    private AxisOption _selectedAxis;
    /// <summary>Which of the four wound axes the diagram colours.</summary>
    public AxisOption SelectedAxis
    {
        get => _selectedAxis;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedAxis, value ?? AxisOptions[0]);
            Render();
        }
    }

    [Reactive] public bool   HasReading   { get; private set; }
    [Reactive] public string ReadingTitle { get; private set; } = "No reading yet — click Perceive or Touch.";
    [Reactive] public string VitalityText { get; private set; } = "";
    [Reactive] public bool   IsPoisoned   { get; private set; }
    [Reactive] public bool   IsDiseased   { get; private set; }
    [Reactive] public bool   HasWounds    { get; private set; }

    /// <summary>The cast line waiting for the player's Cast click, when a spell
    /// was prepared with no wait configured.</summary>
    [Reactive] public string? PendingCast    { get; private set; }
    [Reactive] public bool    HasPendingCast { get; private set; }

    private bool _quickTake;
    /// <summary>Append <c>quick</c> to takes. Persisted.</summary>
    public bool QuickTake
    {
        get => _quickTake;
        set
        {
            this.RaiseAndSetIfChanged(ref _quickTake, value);
            if (!_refreshing) EditSetting(s => s.QuickTake = value);
        }
    }

    // ── Wiring ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Connect to a live core: readings from <see cref="PatientHealthEvent"/>,
    /// settings from <see cref="GenieCore.Healing"/>, and sends through the
    /// ordinary user-command path (<c>Commands.ProcessInput</c>) — the same
    /// trust model as a typed line or a clicked link.
    /// </summary>
    public void Attach(GenieCore core)
    {
        ArgumentNullException.ThrowIfNull(core);
        UseSettings(core.Healing);
        _send        = line => core.Commands.ProcessInput(line);
        _commandChar = () => core.Config.CommandChar;
        _variable    = core.LookupVariable;
        _save        = () => core.SaveHealingSettings();

        core.GameEvents.OfType<PatientHealthEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(e => Apply(e.Health));

        // Seed from the snapshot — a reading may have landed before attach.
        foreach (var chart in core.State.PatientHealth.Values.OrderBy(c => c.CapturedAt))
            Apply(chart);
    }

    /// <summary>
    /// Test seam: route sends to <paramref name="send"/> and settings saves to
    /// <paramref name="save"/> without a core.
    /// </summary>
    public void AttachForTest(Action<string> send, char commandChar = '#',
                              Func<string, string?>? variable = null, Action? save = null)
    {
        _send        = send;
        _commandChar = () => commandChar;
        _variable    = variable;
        _save        = save;
    }

    private void UseSettings(HealingSettings settings)
    {
        if (ReferenceEquals(settings, _settings)) return;
        _settings.Changed -= OnSettingsChanged;
        _settings = settings;
        _settings.Changed += OnSettingsChanged;
        RefreshSettings();
    }

    private void OnSettingsChanged()
    {
        if (_refreshing) return;
        RxApp.MainThreadScheduler.Schedule(RefreshSettings);
    }

    private void RefreshSettings()
    {
        _refreshing = true;
        try
        {
            foreach (var row in Spells) row.Load(_settings);
            QuickTake = _settings.QuickTake;
        }
        finally { _refreshing = false; }
    }

    private void EditSetting(Action<HealingSettings> edit)
    {
        _refreshing = true;           // our own Changed must not reload the rows mid-edit
        try { edit(_settings); }
        finally { _refreshing = false; }
        _save?.Invoke();
    }

    // ── DR's own injuries dialog (never sends) ───────────────────────────────

    // Every other-character injuries dialog seen this session, most recently
    // updated last. The one for the acting-on patient is DrDialog.
    private readonly List<OtherInjuriesViewModel> _dialogs = new();

    /// <summary>
    /// DR's <c>injuries-&lt;charnum&gt;</c> dialog for the patient the buttons
    /// act on, or null (yourself, nobody, or DR hasn't sent one). Its
    /// <see cref="OtherInjuriesViewModel.Extras"/> are the health bar and DR's
    /// own buttons (Transfer Vit, Re-Link) the header shows.
    /// </summary>
    [Reactive] public OtherInjuriesViewModel? DrDialog    { get; private set; }
    [Reactive] public bool                    HasDrDialog { get; private set; }

    /// <summary>
    /// Follow one of DR's other-character injuries dialogs (public #263). The
    /// host calls this once per dialog; the header and the tiles then track
    /// whichever one names the acting-on patient.
    /// </summary>
    public void AttachDialog(OtherInjuriesViewModel dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        if (_dialogs.Contains(dialog)) return;
        _dialogs.Add(dialog);
        dialog.Rendered += OnDialogChanged;
        RefreshDialog();
    }

    /// <summary>A new connection: drop every dialog from the last one.</summary>
    public void ClearDialogs()
    {
        foreach (var d in _dialogs) d.Rendered -= OnDialogChanged;
        _dialogs.Clear();
        RefreshDialog();
    }

    private void OnDialogChanged(object? sender, EventArgs e)
    {
        if (sender is OtherInjuriesViewModel d && _dialogs.Remove(d)) _dialogs.Add(d);
        RefreshDialog();
    }

    private void RefreshDialog()
    {
        var name = IsSelf ? "" : HealingCommandBuilder.CleanPatient(PatientName);
        DrDialog = name.Length == 0 ? null : _dialogs.LastOrDefault(
            d => string.Equals(d.Patient, name, StringComparison.OrdinalIgnoreCase));
        HasDrDialog = DrDialog is not null;
        RenderTransfers();
    }

    /// <summary>
    /// What a right-click on each tile would send: DR's own transfer where its
    /// dialog marked the part, else (for another patient, on a wounded tile)
    /// the internal-axis <c>transfer</c> the builder has a confirmed wording
    /// for (public #375). Nothing on yourself.
    /// </summary>
    private void RenderTransfers()
    {
        var builder = Builder();
        foreach (var cell in Cells)
        {
            if (IsSelf) { cell.SetTransfer(false, null); continue; }
            if (DrDialog?.TransferHint(cell.RegionId) is { } hint)
            {
                cell.SetTransfer(true, hint);
                continue;
            }
            var fallback = cell.IsWounded
                ? builder.Transfer(PatientName, cell.RegionId, _selectedAxis.Axis)
                : HealingAction.None;
            cell.SetTransfer(false, fallback.Lines.Count > 0 ? fallback.Lines[0] : null);
        }
    }

    // ── Readings in (never sends) ────────────────────────────────────────────

    /// <summary>
    /// Take a completed reading: replace that patient's row (or add one) and
    /// show it. Only the latest reading per patient is kept.
    /// </summary>
    public void Apply(PatientHealth chart)
    {
        ArgumentNullException.ThrowIfNull(chart);
        var existing = Patients.FirstOrDefault(
            p => string.Equals(p.Key, chart.Patient, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            existing = new PatientEntry(chart);
            Patients.Insert(0, existing);
        }
        else
        {
            existing.Update(chart);
            var at = Patients.IndexOf(existing);
            if (at > 0) Patients.Move(at, 0);
        }

        // A fresh reading is what the player just asked for — show it.
        if (ReferenceEquals(_selectedPatient, existing)) Render();
        else SelectedPatient = existing;
    }

    /// <summary>Drop the selected patient's reading from the list.</summary>
    public void Forget()
    {
        if (_selectedPatient is not { } p) return;
        var at = Patients.IndexOf(p);
        Patients.Remove(p);
        SelectedPatient = Patients.Count == 0 ? null : Patients[Math.Min(at, Patients.Count - 1)];
    }

    private void Render()
    {
        var chart = _selectedPatient?.Chart;
        HasReading = chart is not null;

        foreach (var cell in Cells)
            cell.Set(chart is not null && chart.Regions.TryGetValue(cell.RegionId, out var r) ? r : null,
                     _selectedAxis.Axis);
        RenderTransfers();

        Wounds.Clear();
        if (chart is null)
        {
            ReadingTitle = "No reading yet — click Perceive or Touch.";
            VitalityText = "";
            IsPoisoned = IsDiseased = HasWounds = false;
            return;
        }

        var who = chart.IsSelf ? "Yourself" : chart.Patient;
        ReadingTitle = $"{who} — read {chart.CapturedAt.ToLocalTime():HH:mm:ss}";
        VitalityText = chart.VitalityPercent is { } v ? $"Vitality {v}%"
                     : chart.VitalityWord is { Length: > 0 } w ? $"Vitality: {w}"
                     : "Vitality —";
        IsPoisoned   = chart.IsPoisoned;
        IsDiseased   = chart.IsDiseased;

        foreach (var region in chart.Regions.Values.OrderByDescending(r => r.Worst))
        {
            var name = _cellsById.TryGetValue(region.Region, out var c) ? c.FullName : region.Region;
            foreach (var opt in AxisOptions)
                if (region[opt.Axis] > WoundSeverity.None)
                    Wounds.Add($"{name} — {opt.Label.ToLowerInvariant()} {Word(region[opt.Axis])}");
        }
        HasWounds = Wounds.Count > 0;
    }

    // ── Clicks out (the only send path) ──────────────────────────────────────

    private HealingCommandBuilder Builder() => new(_settings, _commandChar(), _variable);

    /// <summary>Perceive button.</summary>
    public void Perceive() => Dispatch(Builder().Perceive(PatientName));

    /// <summary>Touch button — the empath's diagnosis of a patient.</summary>
    public void Touch() => Dispatch(Builder().Touch(PatientName));

    /// <summary>A body region was clicked: heal it on the selected axis.</summary>
    public void HealRegion(RegionCell cell)
    {
        if (cell is null || !cell.IsWounded) return;
        Dispatch(Builder().HealRegion(PatientName, cell.RegionId, _selectedAxis.Axis));
    }

    /// <summary>
    /// A body region was right-clicked: transfer it. Where DR's dialog marked
    /// the part, DR's own command goes through the dialog host (resolved and
    /// separator-escaped like any dialog click). Otherwise a wounded tile on
    /// another patient sends the builder's <c>transfer</c> for the selected
    /// axis (internal axes only, public #375). Nothing on yourself.
    /// </summary>
    public void TransferRegion(RegionCell cell)
    {
        if (cell is null || IsSelf) return;
        if (DrDialog is { } dialog && dialog.TransferRegion(cell.RegionId)) return;
        if (!cell.IsWounded) return;
        Dispatch(Builder().Transfer(PatientName, cell.RegionId, _selectedAxis.Axis));
    }

    /// <summary>One of DR's own header buttons (Transfer Vit, Re-Link) was
    /// clicked: through the dialog host, like the button in DR's window.</summary>
    public void ActivateDialogControl(string controlId)
    {
        if (DrDialog is not { } dialog) return;
        if (!dialog.Extras.Any(c => (c is DialogButtonViewModel or DialogLinkViewModel) &&
                                    string.Equals(c.Id, controlId, StringComparison.OrdinalIgnoreCase)))
            return;
        dialog.ActivateExtra(controlId);
    }

    /// <summary>Take All: one click, one queued take per wound on the chart.</summary>
    public void TakeAll()
    {
        if (IsSelf) return;
        var name  = HealingCommandBuilder.CleanPatient(PatientName);
        var chart = Patients.FirstOrDefault(
            p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase))?.Chart;
        Dispatch(Builder().TakeAll(chart));
    }

    /// <summary>Vitality / Poison / Disease button.</summary>
    public void TakeCondition(HealingCondition condition)
        => Dispatch(Builder().TakeCondition(PatientName, condition));

    /// <summary>A spell button: prepare it (and cast after its wait, if one is set).</summary>
    public void CastSpell(HealingSpell spell) => Dispatch(Builder().Cast(spell, target: null));

    /// <summary>Cast button — sends the cast a no-wait prepare left pending.</summary>
    public void CastPending()
    {
        if (PendingCast is not { } cast) return;
        SetPendingCast(null);
        Send(cast);
    }

    /// <summary>Stop button: clear whatever is still queued.</summary>
    public void Stop()
    {
        SetPendingCast(null);
        Dispatch(Builder().StopQueue());
    }

    private void Dispatch(HealingAction action)
    {
        foreach (var line in action.Lines) Send(line);
        if (action.PendingCast is not null) SetPendingCast(action.PendingCast);
        else if (action.Lines.Count > 0 && action.Lines[0].StartsWith("prep ", StringComparison.Ordinal))
            SetPendingCast(null);   // a queued cast supersedes any older pending one
    }

    private void Send(string line) => _send?.Invoke(line);

    private void SetPendingCast(string? cast)
    {
        PendingCast    = cast;
        HasPendingCast = cast is not null;
    }
}
