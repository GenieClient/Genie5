using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using Genie.App.Controls;
using Genie.Core.Health;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace Genie.App.ViewModels;

/// <summary>
/// The Healing panel's body figures: the Injuries panel's own art, either as
/// four figures (one per wound axis, labelled) or as one figure with all four
/// axes overlaid — outside wounds on a part's edge, inside wounds on its
/// centre, fresh solid, scars striped. The player picks the layout; it is
/// persisted per character in <c>healing.json</c>.
/// </summary>
public sealed partial class HealingViewModel
{
    /// <summary>Width and height of a figure's drawing space — the same canvas
    /// the Injuries panel's figure layout uses, so the coordinates are shared.</summary>
    public const double FigureWidth = 920, FigureHeight = 1125;

    /// <summary>One body part placed on a figure.</summary>
    public sealed class FigurePart : ReactiveObject
    {
        internal FigurePart(string regionId, string fullName, double left, double top,
                            double width, double height, double crop = 1.0,
                            bool isBadge = false, bool neverReported = false)
        {
            RegionId = regionId; FullName = fullName;
            Left = left; Top = top; Width = width; Height = height;
            Crop = crop; IsBadge = isBadge; NeverReported = neverReported;
            Tip = $"{fullName} — healthy";
            Opacity = neverReported ? 0.16 : HealingSprites.HealthyOpacity;
        }

        public string RegionId { get; }
        public string FullName { get; }
        public double Left   { get; }
        public double Top    { get; }
        public double Width  { get; }
        public double Height { get; }
        /// <summary>Arm/leg sprites carry a built-in hand/foot; the figure crops
        /// them at the wrist/ankle, as the Injuries panel does.</summary>
        public double Crop   { get; }
        /// <summary>Skin has no sprite: it is a labelled tile on the figure.</summary>
        public bool   IsBadge { get; }
        public bool   IsSprite => !IsBadge;
        /// <summary>perceive health never names this part (feet, nerves): it is
        /// drawn fainter so it does not read as "checked and healthy".</summary>
        public bool   NeverReported { get; }

        [Reactive] public IImage? Sprite     { get; internal set; }
        [Reactive] public double  Opacity    { get; internal set; }
        [Reactive] public string  Tip        { get; internal set; }
        [Reactive] public IBrush  BadgeFill  { get; internal set; } = HealthyFill;
        [Reactive] public IBrush  BadgeText  { get; internal set; } = HealthyFg;
        [Reactive] public string  BadgeLabel { get; internal set; } = "Skin";
        [Reactive] public bool    IsSelected { get; internal set; }
        /// <summary>Wounds on this part, axis → rung, for the figure's axis
        /// (four-figure layout) or all axes (overlay).</summary>
        internal IReadOnlyDictionary<InjuryAxis, int> Wounds { get; set; } = new Dictionary<InjuryAxis, int>();
        public bool IsWounded => Wounds.Count > 0;
    }

    /// <summary>One figure: its parts and, in the four-figure layout, the axis
    /// it shows. The overlay figure has no axis.</summary>
    public sealed class BodyFigure
    {
        internal BodyFigure(string title, InjuryAxis? axis, IReadOnlyList<FigurePart> parts)
        { Title = title; Axis = axis; Parts = parts; }

        public string Title { get; }
        public InjuryAxis? Axis { get; }
        public IReadOnlyList<FigurePart> Parts { get; }
    }

    /// <summary>One line of the selected part's card in the overlay layout.</summary>
    public sealed record PartWound(InjuryAxis Axis, string Label, string Rung, IBrush Swatch, bool Striped,
                                   bool CanTransfer);

    // ── Layout table ─────────────────────────────────────────────────────────

    // The Injuries panel's figure assembly (App.axaml), in stacking order:
    // legs → feet → abdomen → chest → arms → hands → gorget → head → eyes,
    // then back / nerves / tail beside it. Skin, which has no art, is a tile.
    private static IReadOnlyList<FigurePart> NewParts() => new[]
    {
        new FigurePart("rightLeg",  "Right Leg",      194, 611, 127, 365, crop: 0.83),
        new FigurePart("leftLeg",   "Left Leg",       318, 611, 127, 365, crop: 0.83),
        new FigurePart("rightFoot", "Right Foot",     141, 938, 134, 145, neverReported: true),
        new FigurePart("leftFoot",  "Left Foot",      365, 938, 131, 145, neverReported: true),
        new FigurePart("abdomen",   "Abdomen",        226, 455, 189, 220),
        new FigurePart("chest",     "Chest",          191, 225, 258, 280),
        new FigurePart("rightArm",  "Right Arm",      130, 235, 116, 260, crop: 0.84),
        new FigurePart("leftArm",   "Left Arm",       395, 235, 114, 260, crop: 0.84),
        new FigurePart("rightHand", "Right Hand",     132, 477,  89, 150),
        new FigurePart("leftHand",  "Left Hand",      414, 477,  88, 150),
        new FigurePart("neck",      "Neck",           229, 185, 182, 170),
        new FigurePart("head",      "Head",           242,  48, 155, 240),
        new FigurePart("rightEye",  "Right Eye",      270, 131,  49,  34),
        new FigurePart("leftEye",   "Left Eye",       322, 131,  48,  34),
        new FigurePart("back",      "Back",           548, 120, 260, 280),
        new FigurePart("nsys",      "Nervous System", 583, 470, 190, 300, neverReported: true),
        new FigurePart("tail",      "Tail",           610, 840, 130, 195),
        new FigurePart("skin",      "Skin",            20,  20, 170,  80, isBadge: true),
    };

    private IReadOnlyList<BodyFigure> BuildPanelFigures() => AxisOptions
        .Select(o => BuildFigure(o.Label, o.Axis))
        .ToList();

    private static BodyFigure BuildFigure(string title, InjuryAxis? axis) => new(title, axis, NewParts());

    // ── State ────────────────────────────────────────────────────────────────

    /// <summary>The four-figure layout: fresh outside, fresh inside, scar
    /// outside, scar inside.</summary>
    public IReadOnlyList<BodyFigure> PanelFigures { get; private set; } = Array.Empty<BodyFigure>();

    /// <summary>The overlay layout's single figure.</summary>
    public BodyFigure OverlayFigure { get; private set; } = null!;

    private bool _overlayLayout;
    /// <summary>True: one figure with every axis overlaid. False: four figures.
    /// Persisted.</summary>
    public bool OverlayLayout
    {
        get => _overlayLayout;
        set
        {
            this.RaiseAndSetIfChanged(ref _overlayLayout, value);
            this.RaisePropertyChanged(nameof(PanelLayout));
            if (!_refreshing) EditSetting(s => s.OverlayFigure = value);
            if (!value) SelectPart(null);
        }
    }

    /// <summary>Inverse of <see cref="OverlayLayout"/>, for the view's radio pair.</summary>
    public bool PanelLayout
    {
        get => !_overlayLayout;
        set => OverlayLayout = !value;
    }

    private FigurePart? _selectedPart;
    /// <summary>The part whose wounds the overlay card lists.</summary>
    public FigurePart? SelectedPart
    {
        get => _selectedPart;
        private set
        {
            if (_selectedPart is not null) _selectedPart.IsSelected = false;
            this.RaiseAndSetIfChanged(ref _selectedPart, value);
            if (value is not null) value.IsSelected = true;
            this.RaisePropertyChanged(nameof(HasSelectedPart));
        }
    }

    public bool HasSelectedPart => _selectedPart is not null;

    /// <summary>The selected part's wounds, in draw order.</summary>
    public ObservableCollection<PartWound> SelectedPartWounds { get; } = new();

    // ── Rendering ────────────────────────────────────────────────────────────

    private void RenderFigures(PatientHealth? chart)
    {
        foreach (var fig in PanelFigures) RenderFigure(fig, chart);
        if (OverlayFigure is not null) RenderFigure(OverlayFigure, chart);
        // Keep the card in step with the new reading (or close it).
        if (_selectedPart is { } sel) SelectPart(sel);
    }

    private void RenderFigure(BodyFigure fig, PatientHealth? chart)
    {
        foreach (var part in fig.Parts)
        {
            RegionInjuries? region = null;
            chart?.Regions.TryGetValue(part.RegionId, out region);
            var wounds = new Dictionary<InjuryAxis, int>();
            foreach (var o in AxisOptions)
            {
                if (fig.Axis is { } only && o.Axis != only) continue;
                var s = region?[o.Axis] ?? WoundSeverity.None;
                if (s > WoundSeverity.None) wounds[o.Axis] = (int)s;
            }
            part.Wounds = wounds;
            part.Tip = TipFor(part.FullName, region, fig.Axis);
            // DR's own injuries dialog marked this part transferable.
            if (!IsSelf && DrDialog?.TransferHint(part.RegionId) is { } hint)
                part.Tip += $"\nright-click: {hint}";

            if (part.IsBadge)
            {
                var worst = wounds.OrderByDescending(w => w.Value).FirstOrDefault();
                part.BadgeFill  = worst.Value > 0 ? new SolidColorBrush(HealingSprites.ColourFor(worst.Key, worst.Value)) : HealthyFill;
                part.BadgeText  = worst.Value > 0 ? DarkFg : HealthyFg;
                part.BadgeLabel = worst.Value > 0 ? $"Skin {worst.Value}" : "Skin";
                continue;
            }

            if (wounds.Count == 0)
            {
                part.Sprite  = HealingSprites.Plain(part.RegionId, part.Crop);
                part.Opacity = part.NeverReported ? 0.16 : HealingSprites.HealthyOpacity;
            }
            else
            {
                // Without readable pixels (headless) the plain art at full
                // strength still marks the part as wounded.
                part.Sprite  = HealingSprites.Bake(part.RegionId, part.Crop, wounds, overlay: fig.Axis is null)
                               ?? HealingSprites.Plain(part.RegionId, part.Crop);
                part.Opacity = 1.0;
            }
        }
    }

    private static string TipFor(string name, RegionInjuries? region, InjuryAxis? only)
    {
        if (region is null || region.Worst == WoundSeverity.None) return $"{name} — healthy";
        var lines = AxisOptions
            .Where(o => only is null || o.Axis == only)
            .Where(o => region[o.Axis] > WoundSeverity.None)
            .Select(o => $"{o.Label}: {Word(region[o.Axis])}")
            .ToList();
        return lines.Count == 0 ? $"{name} — healthy on this view" : name + "\n" + string.Join("\n", lines);
    }

    // ── Hit testing ──────────────────────────────────────────────────────────

    /// <summary>
    /// The part under <paramref name="point"/> (figure coordinates, 920×1125),
    /// topmost first. A sprite only counts where its art is solid — the boxes
    /// overlap (the arms' boxes cover the chest's shoulders), so a box test
    /// would hand a click on the chest to an arm.
    /// </summary>
    public static FigurePart? PartAt(BodyFigure figure, Point point)
    {
        for (int i = figure.Parts.Count - 1; i >= 0; i--)
        {
            var p = figure.Parts[i];
            if (point.X < p.Left || point.Y < p.Top || point.X >= p.Left + p.Width || point.Y >= p.Top + p.Height)
                continue;
            if (p.IsBadge) return p;

            // Image Stretch=Uniform, centred in the part's box.
            double dw = p.Width, dh = p.Height;
            if (HealingSprites.SizeOf(p.RegionId, p.Crop) is { } px && px.Width > 0 && px.Height > 0)
            {
                double scale = Math.Min(p.Width / px.Width, p.Height / px.Height);
                dw = px.Width * scale; dh = px.Height * scale;
            }
            double ox = p.Left + (p.Width - dw) / 2, oy = p.Top + (p.Height - dh) / 2;
            double u = (point.X - ox) / dw, v = (point.Y - oy) / dh;
            if (HealingSprites.IsSolidAt(p.RegionId, p.Crop, u, v)) return p;
        }
        return null;
    }

    // ── Clicks ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Left-click on a figure. Four-figure layout: heal that part on the
    /// figure's axis. Overlay: open the part's card (a part can hold up to four
    /// wounds, so the click alone cannot say which to take).
    /// </summary>
    public void FigureClicked(BodyFigure figure, Point point)
    {
        var part = PartAt(figure, point);
        if (figure.Axis is { } axis)
        {
            if (part is { IsWounded: true }) HealPart(part.RegionId, axis);
            return;
        }
        SelectPart(part is { IsWounded: true } ? part : null);
    }

    /// <summary>
    /// Right-click on a figure: transfer the part. DR's own marked transfer
    /// wins; otherwise the figure's axis, or in the overlay the worst inside
    /// wound (only inside wounds have a transfer wording).
    /// </summary>
    public void FigureRightClicked(BodyFigure figure, Point point)
    {
        if (PartAt(figure, point) is not { } part) return;
        var axis = figure.Axis
                   ?? part.Wounds.Where(w => HealingCommandBuilder.CanTransfer(w.Key))
                                 .OrderByDescending(w => w.Value)
                                 .Select(w => (InjuryAxis?)w.Key)
                                 .FirstOrDefault();
        if (axis is null)
        {
            // Nothing transferable by the builder; DR's own may still apply.
            if (!IsSelf && DrDialog is { } dialog) dialog.TransferRegion(part.RegionId);
            return;
        }
        TransferPart(part.RegionId, axis.Value);
    }

    /// <summary>Open (or with null, close) the overlay card for a part.</summary>
    public void SelectPart(FigurePart? part)
    {
        SelectedPartWounds.Clear();
        if (part is null || !part.IsWounded) { SelectedPart = null; return; }
        SelectedPart = part;
        foreach (var o in AxisOptions)
        {
            if (!part.Wounds.TryGetValue(o.Axis, out var rung)) continue;
            SelectedPartWounds.Add(new PartWound(
                o.Axis, o.Label, $"{PerceiveHealthParser.SeverityName((WoundSeverity)rung)} ({rung}/13)",
                new SolidColorBrush(HealingSprites.ColourFor(o.Axis, rung)),
                Striped: HealingSprites.IsScar(o.Axis),
                CanTransfer: !IsSelf && HealingCommandBuilder.CanTransfer(o.Axis)));
        }
    }

    /// <summary>A Take button on the overlay card.</summary>
    public void TakeSelected(PartWound wound)
    {
        if (_selectedPart is { } part && wound is not null) HealPart(part.RegionId, wound.Axis);
    }

    /// <summary>A Transfer button on the overlay card.</summary>
    public void TransferSelected(PartWound wound)
    {
        if (_selectedPart is { } part && wound is not null) TransferPart(part.RegionId, wound.Axis);
    }
}
