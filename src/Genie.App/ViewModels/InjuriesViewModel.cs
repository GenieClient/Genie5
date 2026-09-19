using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Genie.Core;
using Genie.Core.Events;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace Genie.App.ViewModels;

/// <summary>
/// Backs the dockable Injuries panel (public issue #18) — a body-part sprite
/// grid fed by the server's injuries dialog
/// (<c>&lt;dialogData id="injuries"&gt;&lt;image id="rightLeg" name="Injury1"/&gt;…</c>).
///
/// Each of DR's 16 hit-test regions gets a sprite cell whose colour variant
/// encodes the last reading (natural ivory = healthy, yellow→orange→red =
/// wound 1–3, steel blue = scar, purple = nerve damage — nsys can't
/// distinguish wound from scar); the list below the grid spells the same
/// readings out in words, so severity never rides on colour alone. The dialog
/// reflects the player's selected display mode (E/I Wound/Scar/Both — the
/// <c>_injury N</c> radios), and DR re-pushes the full dialog at every login,
/// so a stale panel self-corrects on reconnect.
///
/// Hidden by default; re-open via Window → Injuries.
/// </summary>
public sealed class InjuriesViewModel : ReactiveObject
{
    /// <summary>One body-region cell in the sprite grid / figure.</summary>
    public sealed class InjuryCell : ReactiveObject
    {
        private readonly string _regionId;
        private readonly double _figureCrop;

        /// <summary>Short grid label ("R Arm").</summary>
        public string Label    { get; }
        /// <summary>Full region name for tooltips + the summary list ("Right Arm").</summary>
        public string FullName { get; }

        /// <summary>Body-part sprite in the colour variant for the current
        /// reading (see <see cref="Controls.InjurySprites"/>). Null when the
        /// asset pipeline isn't available — the label still renders.</summary>
        [Reactive] public IImage? Sprite        { get; private set; }
        /// <summary>Same variant for the assembled-figure layout: the arm/leg
        /// sprites carry their own built-in hands/feet, cropped here at the
        /// wrist/ankle so the dedicated hand/foot parts take over. Identical
        /// to <see cref="Sprite"/> for every other region.</summary>
        [Reactive] public IImage? FigureSprite  { get; private set; }
        /// <summary>Healthy parts render ghosted so injuries pop at a glance.</summary>
        [Reactive] public double  SpriteOpacity { get; private set; } = HealthyOpacity;
        [Reactive] public IBrush  Foreground    { get; private set; } = HealthyFg;
        [Reactive] public string  Tip           { get; private set; }

        public InjuryCell(string regionId, string label, string fullName, double figureCrop = 1.0)
        {
            _regionId   = regionId;
            _figureCrop = figureCrop;
            Label       = label;
            FullName    = fullName;
            Tip         = $"{fullName} — healthy";
            UpdateSprites(Controls.InjurySprites.Get(regionId, InjuryKind.None, 0));
        }

        internal InjuryKind Kind     { get; private set; } = InjuryKind.None;
        internal int        Severity { get; private set; }

        internal void Set(InjuryKind kind, int severity)
        {
            Kind          = kind;
            Severity      = severity;
            UpdateSprites(Controls.InjurySprites.Get(_regionId, kind, severity));
            SpriteOpacity = kind == InjuryKind.None ? HealthyOpacity : 1.0;
            Foreground    = kind == InjuryKind.None ? HealthyFg : InjuredFg;
            Tip = kind == InjuryKind.None
                ? $"{FullName} — healthy"
                : $"{FullName} — {KindWord(kind)} ({severity})";
        }

        private void UpdateSprites(Bitmap? bmp)
        {
            Sprite = bmp;
            FigureSprite = bmp is null || _figureCrop >= 1.0
                ? bmp
                : new CroppedBitmap(bmp, new PixelRect(
                    0, 0, bmp.PixelSize.Width, (int)(bmp.PixelSize.Height * _figureCrop)));
        }

        /// <summary>Display word per kind. Nerve damage is "damage" — the
        /// dialog can't say wound vs scar for nsys (see <see cref="InjuryKind.Damage"/>).</summary>
        internal static string KindWord(InjuryKind kind) => kind switch
        {
            InjuryKind.Wound  => "wound",
            InjuryKind.Scar   => "scar",
            InjuryKind.Damage => "damage",
            _                 => "healthy",
        };

        private const double HealthyOpacity = 0.35;
        private static readonly IBrush HealthyFg = new SolidColorBrush(Color.Parse("#808080"));
        private static readonly IBrush InjuredFg = new SolidColorBrush(Color.Parse("#f0f0f0"));
    }

    // The 16 regions the injuries dialog reports (ids verbatim from the XML;
    // each id doubles as the sprite asset name under Assets/Injuries/). The
    // arm/leg crop fractions are where the wrist/ankle sits in those sprites
    // (they carry built-in hands/feet) — only the figure layout uses them.
    public InjuryCell Head      { get; } = new("head",      "Head",   "Head");
    public InjuryCell Neck      { get; } = new("neck",      "Neck",   "Neck");
    public InjuryCell RightEye  { get; } = new("rightEye",  "R Eye",  "Right Eye");
    public InjuryCell LeftEye   { get; } = new("leftEye",   "L Eye",  "Left Eye");
    public InjuryCell RightArm  { get; } = new("rightArm",  "R Arm",  "Right Arm", 0.84);
    public InjuryCell LeftArm   { get; } = new("leftArm",   "L Arm",  "Left Arm",  0.84);
    public InjuryCell RightHand { get; } = new("rightHand", "R Hand", "Right Hand");
    public InjuryCell LeftHand  { get; } = new("leftHand",  "L Hand", "Left Hand");
    public InjuryCell Chest     { get; } = new("chest",     "Chest",  "Chest");
    public InjuryCell Abdomen   { get; } = new("abdomen",   "Abdomen","Abdomen");
    public InjuryCell Back      { get; } = new("back",      "Back",   "Back");
    public InjuryCell RightLeg  { get; } = new("rightLeg",  "R Leg",  "Right Leg", 0.83);
    public InjuryCell LeftLeg   { get; } = new("leftLeg",   "L Leg",  "Left Leg",  0.83);
    public InjuryCell RightFoot { get; } = new("rightFoot", "R Foot", "Right Foot");
    public InjuryCell LeftFoot  { get; } = new("leftFoot",  "L Foot", "Left Foot");
    public InjuryCell Nsys      { get; } = new("nsys",      "Nerves", "Nervous System");

    /// <summary>Grid display order (4 columns): torso row first, then the
    /// paired limbs with the character's RIGHT side leading — the doll
    /// convention (character faces the viewer) carried into a grid.</summary>
    public IReadOnlyList<InjuryCell> Cells { get; }

    private readonly Dictionary<string, InjuryCell> _cells;

    /// <summary>Injured regions in words ("Right Leg — wound (1)"), silhouette
    /// order. Rebuilt on every reading.</summary>
    public ObservableCollection<string> Injured { get; } = new();

    /// <summary>Number of regions currently reading wound or scar — drives the
    /// panel header.</summary>
    [Reactive] public int  Count   { get; private set; }

    /// <summary>True when every region reads healthy — drives the empty-state
    /// placeholder.</summary>
    [Reactive] public bool IsEmpty { get; private set; } = true;

    // ── Auto-refresh picker ─────────────────────────────────────────────────
    // The nsys image can't say wound vs scar; only the `health` verb's text
    // can. This cadence drives Core's opt-in silent poll (#config
    // injuriespoll). Persisted quietly — straight to config, not through
    // #config — so changing it doesn't spam the Game window (same pattern as
    // the Experience density slider).

    private static readonly int[] RefreshSeconds = { 0, 30, 60, 120, 300 };

    /// <summary>Cadence stops shown by the picker, index-aligned with
    /// <see cref="RefreshSeconds"/>.</summary>
    public IReadOnlyList<string> RefreshOptions { get; } =
        new[] { "Off", "30 s", "1 min", "2 min", "5 min" };

    private GenieCore? _core;
    private int _refreshIndex;
    private int _appliedSeconds = -1;
    private bool _figureLayout;

    /// <summary>Panel layout toggle: true = assembled body figure, false =
    /// the 4×4 part grid (default). Persisted quietly to config
    /// (<c>injurieslayout</c>) — same pattern as <see cref="RefreshIndex"/>;
    /// seeding from config in <see cref="Attach"/> is a no-op write.</summary>
    public bool FigureLayout
    {
        get => _figureLayout;
        set
        {
            this.RaiseAndSetIfChanged(ref _figureLayout, value);
            if (_core is not null && value != _core.Config.InjuriesFigureLayout)
            {
                _core.Config.SetSetting("injurieslayout", value ? "figure" : "grid", showException: false);
                _core.Config.Save();
            }
        }
    }

    /// <summary>Selected picker index. Setting it applies + saves the config
    /// value; seeding from config in <see cref="Attach"/> is a no-op write.</summary>
    public int RefreshIndex
    {
        get => _refreshIndex;
        set
        {
            var index = Math.Clamp(value, 0, RefreshSeconds.Length - 1);
            this.RaiseAndSetIfChanged(ref _refreshIndex, index);
            var seconds = RefreshSeconds[index];
            if (_core is not null && seconds != _appliedSeconds)
            {
                _appliedSeconds = seconds;
                _core.Config.SetSetting("injuriespoll", seconds.ToString(), showException: false);
                _core.Config.Save();
            }
        }
    }

    public InjuriesViewModel()
    {
        Cells = new[]
        {
            Head,     Neck,    Chest,     Back,
            Abdomen,  Nsys,    RightEye,  LeftEye,
            RightArm, LeftArm, RightHand, LeftHand,
            RightLeg, LeftLeg, RightFoot, LeftFoot,
        };
        _cells = new Dictionary<string, InjuryCell>(StringComparer.OrdinalIgnoreCase)
        {
            ["head"] = Head,           ["neck"] = Neck,
            ["rightEye"] = RightEye,   ["leftEye"] = LeftEye,
            ["rightArm"] = RightArm,   ["leftArm"] = LeftArm,
            ["rightHand"] = RightHand, ["leftHand"] = LeftHand,
            ["chest"] = Chest,         ["abdomen"] = Abdomen,
            ["back"] = Back,           ["rightLeg"] = RightLeg,
            ["leftLeg"] = LeftLeg,     ["rightFoot"] = RightFoot,
            ["leftFoot"] = LeftFoot,   ["nsys"] = Nsys,
        };
    }

    public void Attach(GenieCore core)
    {
        _core = core;

        // Seed the picker from config: snap a hand-edited value to the nearest
        // stop for display, but record the REAL value as applied so merely
        // attaching never rewrites the user's setting.
        _appliedSeconds = core.Config.InjuriesPollSeconds;
        var nearest = 0;
        for (var i = 0; i < RefreshSeconds.Length; i++)
            if (Math.Abs(RefreshSeconds[i] - _appliedSeconds) < Math.Abs(RefreshSeconds[nearest] - _appliedSeconds))
                nearest = i;
        _refreshIndex = nearest;
        this.RaisePropertyChanged(nameof(RefreshIndex));

        _figureLayout = core.Config.InjuriesFigureLayout;
        this.RaisePropertyChanged(nameof(FigureLayout));

        core.GameEvents.OfType<InjuryEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(e => Apply(e.Area, e.Kind, e.Severity));

        // Seed from the persistent snapshot — the panel may attach after the
        // login burst already delivered the dialog (GameStateEngine subscribes
        // in GenieCore's ctor, so State.Injuries is ahead of this handler).
        foreach (var kv in core.State.Injuries)
            Apply(kv.Key, kv.Value.Kind, kv.Value.Severity);
    }

    private void Apply(string area, InjuryKind kind, int severity)
    {
        if (!_cells.TryGetValue(area, out var cell)) return;   // unknown region
        if (cell.Kind == kind && cell.Severity == severity) return;   // no change
        cell.Set(kind, severity);
        RebuildSummary();
    }

    private void RebuildSummary()
    {
        Injured.Clear();
        foreach (var cell in _cells.Values)
        {
            if (cell.Kind == InjuryKind.None) continue;
            Injured.Add($"{cell.FullName} — {InjuryCell.KindWord(cell.Kind)} ({cell.Severity})");
        }
        Count   = Injured.Count;
        IsEmpty = Injured.Count == 0;
    }
}
