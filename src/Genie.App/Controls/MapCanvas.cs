using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Genie.App.Docking;
using Genie.Core.Mapper;

namespace Genie.App.Controls;

/// <summary>
/// Lightweight custom-drawn map renderer for an <see cref="MapZone"/>. Paints
/// rooms as filled rectangles at their grid coordinates and draws lines between
/// rooms whose <see cref="MapExit.DestinationId"/> resolves. The room matching
/// <see cref="CurrentNode"/> is outlined in a hot colour so the player can see
/// where they are at a glance.
///
/// Geometry is Genie 4's, via <see cref="MapProjection"/>: rooms are 9px
/// boxes (scaled) centred on their RAW map pixels — not on a 20px grid, since
/// the community maps are authored on Genie 4's 10px snap grid — and the
/// zone's <c>&lt;label&gt;</c> text is anchored at its raw pixel in the same
/// space. Pan, zoom, hit-testing, drag-to-move and the legend all read the
/// same projection so they can't disagree with the paint.
///
/// Re-renders when any of:
///  - <see cref="Zone"/> reference changes
///  - <see cref="CurrentNode"/> reference changes
///  - <see cref="Level"/> changes (Z-level filter)
///  - <see cref="RenderTick"/> bumps — used to force a redraw when the zone's
///    Nodes dictionary mutates but the zone reference itself hasn't.
/// </summary>
public class MapCanvas : Control
{
    // ── Visual constants ───────────────────────────────────────────────────
    // All geometry (room pitch, box size, label font, padding) comes from
    // MapProjection — Genie 4's pixel-space rules scaled by Zoom. See that
    // class for why rooms are drawn at their raw pixels, not on a 20px grid.
    private const double EdgeWidth    = 1.0;
    private const double MinZoom      = 0.4;
    private const double MaxZoom      = 4.0;
    private const double ZoomStep     = 1.20;   // multiplicative step per wheel notch

    // Projection cache: building one measures every visible label, and the
    // hover hit-test asks for it on every pointer move. Keyed on everything
    // that changes the geometry; RenderTick covers in-place zone edits.
    private MapProjection? _proj;
    private (MapZone? zone, int level, double zoom, int tick, bool labels) _projKey;

    /// <summary>The current floor's projection (cached until the zone, level,
    /// zoom, render tick or label toggle changes).</summary>
    private MapProjection GetProjection()
    {
        var key = (Zone, Level, Zoom, RenderTick, FullLabels);
        if (_proj is null || key != _projKey)
        {
            _proj    = MapProjection.Create(Zone ?? new MapZone(), Level, Zoom, MeasureLabel, FullLabels);
            _projKey = key;
        }
        return _proj;
    }

    /// <summary>Label text width in canvas px at the projection's font size —
    /// the same FormattedText the label pass draws, so bounds and paint agree.</summary>
    private static double MeasureLabel(string text, double fontSize) =>
        new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, Typeface.Default, fontSize, RoomLabelBrush).Width;

    private static Rect  R(MapRect r)           => new(r.X, r.Y, r.Width, r.Height);
    private static Point P((double X, double Y) p) => new(p.X, p.Y);

    // Palette mirrors the Genie 4 AutoMapper defaults (Globals.SetDefaultPresets):
    //   panel bg = PaleGoldenrod, node = White, node border / line = Black.
    private static readonly IBrush  BackgroundBrush = new SolidColorBrush(Color.FromRgb(0xee, 0xe8, 0xaa)); // PaleGoldenrod (designer fallback)
    private static readonly IBrush  DefaultNodeFill = new SolidColorBrush(Colors.White);
    private static readonly IBrush  NodeStroke      = new SolidColorBrush(Colors.Black);
    private static readonly IBrush  CurrentStroke   = new SolidColorBrush(Color.FromRgb(0xff, 0x40, 0x40));
    private static readonly Pen     NodePen         = new(NodeStroke, 1.0);
    private static readonly Pen     CurrentPen      = new(CurrentStroke, 2.5);
    // Cross-zone connector rooms (note references another .xml map) get a 2px
    // blue border — Genie 4's "Other map" boxes that show how maps connect.
    private static readonly Pen     CrossZonePen    = new(new SolidColorBrush(Colors.Blue), 2.0);
    // Selection outline (edit mode) — bright yellow dashed so it's distinct
    // from the red "you are here" current-room outline.
    private static readonly Pen     SelectedPen     = new(new SolidColorBrush(Color.FromRgb(0xff, 0xe0, 0x40)), 2.0)
                                                      { DashStyle = new DashStyle(new double[] { 2, 2 }, 0) };
    private static readonly IBrush  EmptyMessageBrush = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));

    // Edge pens — Genie 4 AutoMapper line palette (Globals.SetDefaultPresets):
    //   Cardinal (N/NE/E/SE/S/SW/W/NW)  → black  (automapper.line)
    //   Go / Up / Down / Out            → blue   (automapper.linego)
    //   Climb                           → green  (automapper.lineclimb)
    // Genie 5 lumps every non-compass arc into Direction.None, so climb-vs-go is
    // disambiguated from the move verb (see EdgePenFor).
    private static readonly Pen     EdgePenCardinal = new(new SolidColorBrush(Colors.Black), EdgeWidth);
    private static readonly Pen     EdgePenGo       = new(new SolidColorBrush(Colors.Blue),  EdgeWidth);
    private static readonly Pen     EdgePenClimb    = new(new SolidColorBrush(Color.FromRgb(0x00, 0x80, 0x00)), EdgeWidth);
    // Directional exit stub (#157) — a short cyan tick out of a room's edge for a
    // cardinal exit whose neighbour isn't drawn, so you can still see which ways
    // leave the room (Genie 4 mapper behaviour).
    private static readonly IBrush  StubBrush       = new SolidColorBrush(Color.FromRgb(0x00, 0xC8, 0xE8)); // cyan
    private static readonly Pen     StubPen         = new(StubBrush, EdgeWidth);

    // On-map label text — Genie 4 paints <label> elements in the panel's
    // foreground colour (black on the PaleGoldenrod canvas).
    private static readonly IBrush  RoomLabelBrush  = new SolidColorBrush(Colors.Black);
    // A selected label (edit mode) — Genie 4 paints it white-on-blue with a
    // black border (MapForm.cs, m_SelectedLabels).
    private static readonly IBrush  SelectedLabelFillBrush = new SolidColorBrush(Colors.Blue);
    private static readonly IBrush  SelectedLabelTextBrush = new SolidColorBrush(Colors.White);

    // Hover badge — translucent panel + bright text drawn near the cursor.
    private static readonly IBrush  HoverBackgroundBrush = new SolidColorBrush(Color.FromArgb(0xee, 0x22, 0x22, 0x22));
    private static readonly Pen     HoverBorderPen       = new(new SolidColorBrush(Color.FromRgb(0x66, 0x88, 0xaa)), 1.0);
    private static readonly IBrush  HoverTitleBrush      = new SolidColorBrush(Color.FromRgb(0xff, 0xff, 0xff));
    private static readonly IBrush  HoverSubBrush        = new SolidColorBrush(Color.FromRgb(0x9b, 0xb8, 0xcc));

    // ── Styled properties ─────────────────────────────────────────────────

    public static readonly StyledProperty<MapZone?> ZoneProperty =
        AvaloniaProperty.Register<MapCanvas, MapZone?>(nameof(Zone));

    public static readonly StyledProperty<MapNode?> CurrentNodeProperty =
        AvaloniaProperty.Register<MapCanvas, MapNode?>(nameof(CurrentNode));

    public static readonly StyledProperty<int> LevelProperty =
        AvaloniaProperty.Register<MapCanvas, int>(nameof(Level));

    /// <summary>
    /// Monotonic counter the view-model increments to signal "the zone's Nodes
    /// changed in place, please repaint". Required because StyledProperty
    /// equality is reference-based — re-assigning the same zone reference
    /// would not fire <c>OnPropertyChanged</c>.
    /// </summary>
    /// <summary>Draw spoiler arcs (search / objsearch / quick-send — public #254).
    /// Off hides them so a new player's map doesn't pre-reveal secrets.</summary>
    public static readonly StyledProperty<bool> ShowSpoilersProperty =
        AvaloniaProperty.Register<MapCanvas, bool>(nameof(ShowSpoilers), defaultValue: true);

    public static readonly StyledProperty<int> RenderTickProperty =
        AvaloniaProperty.Register<MapCanvas, int>(nameof(RenderTick));

    /// <summary>
    /// Fired with the clicked <see cref="MapNode"/> as parameter when the user
    /// clicks a room rectangle. The Mapper VM's GotoNodeCommand handles
    /// pathfinding + walking.
    /// </summary>
    public static readonly StyledProperty<ICommand?> NodeClickedCommandProperty =
        AvaloniaProperty.Register<MapCanvas, ICommand?>(nameof(NodeClickedCommand));

    /// <summary>
    /// Fired with the clicked <see cref="MapNode"/> when a plain left-click (navigate
    /// mode) lands on a cross-zone room — the blue-bordered connector whose note
    /// names the adjacent map file. The Mapper VM switches to that map (task #2).
    /// Null (unbound) leaves the click as an ordinary pan start.
    /// </summary>
    public static readonly StyledProperty<ICommand?> CrossZoneClickedCommandProperty =
        AvaloniaProperty.Register<MapCanvas, ICommand?>(nameof(CrossZoneClickedCommand));

    /// <summary>
    /// Scale factor for the whole map render. 1.0 = native size, clamped to
    /// [0.4, 4.0]. Mouse wheel and toolbar buttons drive this. AffectsMeasure
    /// so the ScrollViewer's scrollbars resize with the content.
    /// </summary>
    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<MapCanvas, double>(nameof(Zoom), defaultValue: 1.0,
            coerce: (_, v) => Math.Clamp(v, MinZoom, MaxZoom));

    /// <summary>
    /// Optional override for the "you are here" outline colour around the
    /// current room. Null = default red. Bound from the Mapper VM so users
    /// can pick their own colour via View → Highlight Color.
    /// </summary>
    public static readonly StyledProperty<IBrush?> CurrentRoomBrushProperty =
        AvaloniaProperty.Register<MapCanvas, IBrush?>(nameof(CurrentRoomBrush));

    /// <summary>
    /// Command invoked with a (node, exit) tuple when the user picks
    /// "Edit Exit ▶ {verb}" from the right-click context menu. Bound
    /// from MapperViewModel; on null, the Edit Exit submenu still
    /// appears but the items are disabled.
    /// </summary>
    public static readonly StyledProperty<ICommand?> EditExitCommandProperty =
        AvaloniaProperty.Register<MapCanvas, ICommand?>(nameof(EditExitCommand));

    /// <summary>
    /// User-chosen canvas background brush. Bound to
    /// <see cref="ViewModels.MapperViewModel.MapBackgroundBrush"/>. Null falls
    /// back to the default dark fill, so layout tests / designer previews
    /// without a DataContext still render sensibly.
    /// </summary>
    public static readonly StyledProperty<IBrush?> MapBackgroundBrushProperty =
        AvaloniaProperty.Register<MapCanvas, IBrush?>(nameof(MapBackgroundBrush));

    /// <summary>
    /// User-chosen colour for on-map <c>&lt;label&gt;</c> text. Bound to
    /// <see cref="ViewModels.MapperViewModel.MapTextBrush"/>. Null falls back to
    /// the default black so designer previews without a DataContext still render.
    /// </summary>
    public static readonly StyledProperty<IBrush?> LabelTextBrushProperty =
        AvaloniaProperty.Register<MapCanvas, IBrush?>(nameof(LabelTextBrush));

    /// <summary>
    /// Opacity (0–255) of the "ghost" rooms and arcs drawn for the floors directly
    /// above and below the current level — Genie 4's <c>AutoMapperAlpha</c>. Bound
    /// from <see cref="ViewModels.MapperViewModel.AutoMapperAlpha"/> ←
    /// <c>GenieConfig.AutoMapperAlpha</c>. 0 = off-level rooms hidden (pure
    /// single-level view); 255 = Genie 4's solid white off-floor look.
    /// </summary>
    public static readonly StyledProperty<int> AutoMapperAlphaProperty =
        AvaloniaProperty.Register<MapCanvas, int>(nameof(AutoMapperAlpha), defaultValue: 255,
            coerce: (_, v) => Math.Clamp(v, 0, 255));

    /// <summary>Draw the on-map colour legend (#157) — a small screen-space key
    /// for the node/edge/stub colours. Bound to the mapper VM (config
    /// <c>mapperlegend</c>); default on.</summary>
    public static readonly StyledProperty<bool> ShowLegendProperty =
        AvaloniaProperty.Register<MapCanvas, bool>(nameof(ShowLegend), defaultValue: true);

    // ── Editor styled properties (Genie 4 AutoMapper edit toolbar) ─────────

    /// <summary>When true, left-click selects a node and drag moves it
    /// (edit mode). When false the canvas is a read-only navigator and
    /// left-click does nothing (Go Here lives on the right-click menu).</summary>
    public static readonly StyledProperty<bool> EditModeProperty =
        AvaloniaProperty.Register<MapCanvas, bool>(nameof(EditMode));

    /// <summary>Snap dragged nodes to the integer grid (always on in practice —
    /// the Genie 4 map format stores positions as 20px multiples, so off-grid
    /// placement can't round-trip; the toggle biases rounding vs floor).</summary>
    public static readonly StyledProperty<bool> SnapToGridProperty =
        AvaloniaProperty.Register<MapCanvas, bool>(nameof(SnapToGrid), defaultValue: true);

    /// <summary>When true, nodes can be selected but not dragged (Genie 4
    /// "Lock Positions") — guards against accidentally nudging a clean map.</summary>
    public static readonly StyledProperty<bool> LockPositionsProperty =
        AvaloniaProperty.Register<MapCanvas, bool>(nameof(LockPositions));

    /// <summary>When true (default), the zone's <c>&lt;label&gt;</c> text
    /// (landmark names) is painted on the map. When false the labels are hidden
    /// for a cleaner view. Driven by the toolbar "Labels" toggle.</summary>
    public static readonly StyledProperty<bool> FullLabelsProperty =
        AvaloniaProperty.Register<MapCanvas, bool>(nameof(FullLabels), defaultValue: true);

    /// <summary>The map label selected in edit mode (two-way with the Mapper
    /// VM). Genie 4 paints a selected label as white text on a blue box.
    /// Selecting a label clears <see cref="SelectedNode"/> and vice versa.</summary>
    public static readonly StyledProperty<MapLabel?> SelectedLabelProperty =
        AvaloniaProperty.Register<MapCanvas, MapLabel?>(nameof(SelectedLabel),
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    /// <summary>Fired with a <see cref="MapLabel"/> positioned at the right-click
    /// ("Add Label Here", edit mode). The VM adds it to the zone.</summary>
    public static readonly StyledProperty<ICommand?> AddLabelCommandProperty =
        AvaloniaProperty.Register<MapCanvas, ICommand?>(nameof(AddLabelCommand));

    /// <summary>Fired with the label to delete (Remove Label context item /
    /// Delete key with a label selected).</summary>
    public static readonly StyledProperty<ICommand?> RemoveLabelCommandProperty =
        AvaloniaProperty.Register<MapCanvas, ICommand?>(nameof(RemoveLabelCommand));

    /// <summary>Fired with the label after a drag-to-move completes.</summary>
    public static readonly StyledProperty<ICommand?> LabelMovedCommandProperty =
        AvaloniaProperty.Register<MapCanvas, ICommand?>(nameof(LabelMovedCommand));

    public MapLabel? SelectedLabel      { get => GetValue(SelectedLabelProperty);      set => SetValue(SelectedLabelProperty, value); }
    public ICommand? AddLabelCommand    { get => GetValue(AddLabelCommandProperty);    set => SetValue(AddLabelCommandProperty, value); }
    public ICommand? RemoveLabelCommand { get => GetValue(RemoveLabelCommandProperty); set => SetValue(RemoveLabelCommandProperty, value); }
    public ICommand? LabelMovedCommand  { get => GetValue(LabelMovedCommandProperty);  set => SetValue(LabelMovedCommandProperty, value); }

    /// <summary>The currently selected node (edit mode). Outlined in yellow.</summary>
    public static readonly StyledProperty<MapNode?> SelectedNodeProperty =
        AvaloniaProperty.Register<MapCanvas, MapNode?>(nameof(SelectedNode),
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    /// <summary>Invoked with the moved <see cref="MapNode"/> after a drag
    /// completes, so the VM can mark the zone dirty + repaint.</summary>
    public static readonly StyledProperty<ICommand?> NodeMovedCommandProperty =
        AvaloniaProperty.Register<MapCanvas, ICommand?>(nameof(NodeMovedCommand));

    /// <summary>Invoked with the selected <see cref="MapNode"/> (or null) when
    /// the selection changes in edit mode.</summary>
    public static readonly StyledProperty<ICommand?> SelectNodeCommandProperty =
        AvaloniaProperty.Register<MapCanvas, ICommand?>(nameof(SelectNodeCommand));

    /// <summary>Invoked with a <see cref="MapNode"/> to delete it (Remove Room
    /// context-menu item / Delete key in edit mode).</summary>
    public static readonly StyledProperty<ICommand?> RemoveNodeCommandProperty =
        AvaloniaProperty.Register<MapCanvas, ICommand?>(nameof(RemoveNodeCommand));

    public bool      EditMode          { get => GetValue(EditModeProperty);          set => SetValue(EditModeProperty, value); }
    public bool      SnapToGrid        { get => GetValue(SnapToGridProperty);        set => SetValue(SnapToGridProperty, value); }
    public bool      LockPositions     { get => GetValue(LockPositionsProperty);     set => SetValue(LockPositionsProperty, value); }
    public bool      FullLabels        { get => GetValue(FullLabelsProperty);        set => SetValue(FullLabelsProperty, value); }
    public MapNode?  SelectedNode      { get => GetValue(SelectedNodeProperty);      set => SetValue(SelectedNodeProperty, value); }
    public ICommand? NodeMovedCommand  { get => GetValue(NodeMovedCommandProperty);  set => SetValue(NodeMovedCommandProperty, value); }
    public ICommand? SelectNodeCommand { get => GetValue(SelectNodeCommandProperty); set => SetValue(SelectNodeCommandProperty, value); }
    public ICommand? RemoveNodeCommand { get => GetValue(RemoveNodeCommandProperty); set => SetValue(RemoveNodeCommandProperty, value); }

    public MapZone?  Zone               { get => GetValue(ZoneProperty);               set => SetValue(ZoneProperty, value); }
    public MapNode?  CurrentNode        { get => GetValue(CurrentNodeProperty);        set => SetValue(CurrentNodeProperty, value); }
    public int       Level              { get => GetValue(LevelProperty);              set => SetValue(LevelProperty, value); }
    public int       RenderTick         { get => GetValue(RenderTickProperty);         set => SetValue(RenderTickProperty, value); }
    public bool      ShowSpoilers       { get => GetValue(ShowSpoilersProperty);       set => SetValue(ShowSpoilersProperty, value); }
    public ICommand? NodeClickedCommand { get => GetValue(NodeClickedCommandProperty); set => SetValue(NodeClickedCommandProperty, value); }
    public ICommand? CrossZoneClickedCommand { get => GetValue(CrossZoneClickedCommandProperty); set => SetValue(CrossZoneClickedCommandProperty, value); }
    public double    Zoom               { get => GetValue(ZoomProperty);               set => SetValue(ZoomProperty, value); }
    public IBrush?   CurrentRoomBrush   { get => GetValue(CurrentRoomBrushProperty);   set => SetValue(CurrentRoomBrushProperty, value); }
    public IBrush?   MapBackgroundBrush { get => GetValue(MapBackgroundBrushProperty); set => SetValue(MapBackgroundBrushProperty, value); }
    public IBrush?   LabelTextBrush     { get => GetValue(LabelTextBrushProperty);     set => SetValue(LabelTextBrushProperty, value); }
    public int       AutoMapperAlpha    { get => GetValue(AutoMapperAlphaProperty);    set => SetValue(AutoMapperAlphaProperty, value); }
    public bool      ShowLegend         { get => GetValue(ShowLegendProperty);         set => SetValue(ShowLegendProperty, value); }
    public ICommand? EditExitCommand    { get => GetValue(EditExitCommandProperty);    set => SetValue(EditExitCommandProperty, value); }

    // ── Hover state (internal, drives the tooltip paint) ──────────────────
    private MapNode? _hoveredNode;
    private Point    _cursor;

    // ── Drag state (edit mode) ────────────────────────────────────────────
    private bool           _dragging;
    private MapProjection? _dragProj;   // projection cached at drag start (stable while dragging)
    private int            _dragOrigX;  // selected node's map-pixel position at drag start —
    private int            _dragOrigY;  // used to skip the move/dirty when it didn't change
    private Point          _pressPoint; // where the button went down — a release within
                                        // DragThreshold of it is a click, not a move
    private const double   DragThreshold = 3.0;

    // Label drag (edit mode). The grab offset keeps the label under the same
    // spot of the pointer instead of snapping its corner to the cursor.
    private bool           _draggingLabel;
    private Point          _labelGrab;   // (Avalonia Point − Point is a Point)
    private double         _labelOrigX;
    private double         _labelOrigY;

    // ── Pan state (grab-scroll the view) ──────────────────────────────────
    private bool          _panning;
    private Point         _panLast;    // last pointer pos in ScrollViewer coords
    private ScrollViewer? _scroller;   // cached parent scroll viewer

    // The context menu we opened last, so a fresh right-click can dismiss it
    // before opening a new one. We build a new ContextMenu per click and open it
    // imperatively; because the right-click is Handled, Avalonia's light-dismiss
    // doesn't always close the previous menu, leaving two stacked. Track + close.
    private ContextMenu? _openMenu;

    public MapCanvas()
    {
        // Focusable so the Delete key reaches OnKeyDown when the canvas has
        // focus (we Focus() on pointer-press in edit mode).
        Focusable = true;

        // The map surface owns a single unified right-click menu (built in
        // OnPointerPressed, folding in the window-level Float/Close actions).
        // Swallow ContextRequested so the dock chrome's separate per-window menu
        // never opens alongside ours. ContextRequested is a routed event, not a
        // virtual override, so we subscribe rather than override.
        ContextRequested += OnContextRequested;
    }

    static MapCanvas()
    {
        // Any of these changing means we need to repaint AND recompute size.
        AffectsRender<MapCanvas>(ShowSpoilersProperty);
        AffectsRender<MapCanvas>(ZoneProperty, CurrentNodeProperty, LevelProperty, RenderTickProperty,
                                 ZoomProperty, CurrentRoomBrushProperty, MapBackgroundBrushProperty,
                                 LabelTextBrushProperty, AutoMapperAlphaProperty, ShowLegendProperty,
                                 EditModeProperty, SelectedNodeProperty, FullLabelsProperty,
                                 SelectedLabelProperty);
        AffectsMeasure<MapCanvas>(ZoneProperty, LevelProperty, RenderTickProperty, ZoomProperty);

        // Auto-center on the active room whenever it changes. Walking into a
        // new room shouldn't require the player to hunt for themselves in a
        // large zone — fire CenterOnCurrent so the surrounding ScrollViewer
        // pans to put the active node in the middle of the viewport.
        //
        // Manual user panning between room changes is preserved because we
        // only fire on CurrentNode-changes, not on every render. Zoom + zone
        // changes also re-center (different node coordinates → different
        // viewport offsets needed).
        CurrentNodeProperty.Changed.AddClassHandler<MapCanvas>((c, _) => c.CenterOnCurrent());
        ZoneProperty.Changed.AddClassHandler<MapCanvas>((c, _) => c.CenterOnCurrent());
        ZoomProperty.Changed.AddClassHandler<MapCanvas>((c, _) => c.CenterOnCurrent());
    }

    /// <summary>
    /// Scroll the surrounding <see cref="ScrollViewer"/> so the current
    /// room sits at the viewport center. Dispatched on background priority
    /// so the canvas has a chance to remeasure first — if the zone just
    /// loaded, <see cref="Bounds"/> may still be at the previous size when
    /// the CurrentNode property change fires.
    /// </summary>
    public void CenterOnCurrent()
    {
        if (CurrentNode is null || Zone is null) return;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (CurrentNode is null || Zone is null) return;

            var proj = GetProjection();
            if (!proj.Any) return;

            var center = P(proj.NodeCenter(CurrentNode));

            // Walk up the visual tree to find the ScrollViewer we live in
            // and scroll its Offset so the node center lands at the
            // viewport center. Clamp to [0, scrollable-extent] so we don't
            // try to set a negative offset (Avalonia clamps anyway, but
            // doing it explicitly keeps the math honest).
            var sv = this.FindAncestorOfType<ScrollViewer>();
            if (sv is null) return;

            var targetX = center.X - sv.Viewport.Width  / 2;
            var targetY = center.Y - sv.Viewport.Height / 2;
            var maxX    = Math.Max(0, Bounds.Width  - sv.Viewport.Width);
            var maxY    = Math.Max(0, Bounds.Height - sv.Viewport.Height);
            sv.Offset   = new Avalonia.Vector(
                Math.Clamp(targetX, 0, maxX),
                Math.Clamp(targetY, 0, maxY));
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    // ── Layout ────────────────────────────────────────────────────────────

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Zone is null || Zone.Nodes.Count == 0)
            return new Size(200, 200);

        // Extents of the rooms AND labels on the active floor (MapProjection
        // folds the measured label rectangles in, so no label text clips).
        var proj = GetProjection();
        if (!proj.Any) return new Size(200, 200);

        return new Size(Math.Max(200, proj.CanvasWidth), Math.Max(200, proj.CanvasHeight));
    }

    // ── Render ────────────────────────────────────────────────────────────

    public override void Render(DrawingContext context)
    {
        // Background — paint the whole control so empty areas don't show
        // whatever was behind the canvas. Honours the user-chosen brush from
        // the Mapper VM (ColorPickerButton in Details); falls back to the
        // dark default if no brush bound (designer / no-DataContext).
        var bg = MapBackgroundBrush ?? BackgroundBrush;
        context.FillRectangle(bg, new Rect(Bounds.Size));

        if (Zone is null || Zone.Nodes.Count == 0)
        {
            DrawCenteredMessage(context, "No zone loaded.\nConnect to DragonRealms or run File → Update Maps.");
            return;
        }

        // The floor's projection (matches MeasureOverride and HitTest).
        var proj = GetProjection();
        if (!proj.Any)
        {
            DrawCenteredMessage(context, $"No rooms on level {Level}.");
            return;
        }
        var nodeSide = proj.NodeSide;

        // ── Pass 0: off-level "ghost" floors (directly above/below) ────────────
        // Drawn first (under everything current) so a multi-floor zone shows
        // where the adjacent levels extend. Genie 4 parity (MapForm.cs, "Mark
        // all other levels gray"): an off-floor room AND its arcs are painted in
        // the automapper presets' BACKGROUND colour — white by default
        // ("automapper.node" / "automapper.line" = "…, White") — so the floor
        // below stays a readable map under the current floor. The earlier cut
        // painted opaque grey boxes and skipped the arcs; on a map whose new
        // rooms sit on a one-floor inset (Crossing's Tatting Street / Riverlace
        // Lane, z=1), that turned all 1,148 floor-0 rooms into a line-less grid
        // that read as a broken mapper. AutoMapperAlpha fades the ghosts; 0 =
        // hidden (pure single level). Rooms share the X/Y grid across Z, so
        // ghosts align under the current floor. Labels stay current-floor only,
        // as in Genie 4.
        if (AutoMapperAlpha > 0)
        {
            var a          = (byte)AutoMapperAlpha;
            var ghostBrush = new SolidColorBrush(Color.FromArgb(a, 0xff, 0xff, 0xff));
            var ghostPen   = new Pen(ghostBrush, EdgeWidth);

            // Ghost arcs first, under the ghost boxes, exactly like Pass 1 → 2.
            ForEachGhostEdge(Zone, Level, ShowSpoilers,
                (from, to)  => context.DrawLine(ghostPen, P(proj.NodeCenter(from)), P(proj.NodeCenter(to))),
                (from, dir) => DrawExitStub(context, P(proj.NodeCenter(from)), dir, nodeSide, ghostPen));

            foreach (var node in Zone.Nodes.Values)
            {
                if (!IsGhostFloor(node.Z, Level)) continue;
                var rect = R(proj.NodeRect(node));
                context.FillRectangle(ghostBrush, rect);
                context.DrawRectangle(ghostPen, rect);
            }
        }

        // ── Pass 1: edges (under the nodes so they don't paint over the squares) ──
        foreach (var node in Zone.Nodes.Values)
        {
            if (node.Z != Level) continue;
            var fromCenter = P(proj.NodeCenter(node));

            foreach (var exit in node.Exits)
            {
                // #254: with spoilers off, a hidden exit leaves no trace — no
                // edge and no stub pointing at it.
                if (!ShowSpoilers && Genie.Core.Mapper.MoveVerb.IsSpoilerMove(exit.MoveCommand)) continue;
                if (exit.DestinationId is int destId
                    && Zone.Nodes.TryGetValue(destId, out var dest)
                    && dest.Z == Level)
                {
                    // Neighbour is drawn on this level → full edge (once, from the
                    // lower id side). Pen by exit type, matching Genie 4's palette.
                    if (node.Id > dest.Id) continue;
                    context.DrawLine(EdgePenFor(exit), fromCenter, P(proj.NodeCenter(dest)));
                }
                else
                {
                    // Neighbour isn't drawn (no dest, off-level, or unrecorded) →
                    // a short cyan stub in the exit's cardinal direction (#157).
                    DrawExitStub(context, fromCenter, exit.Direction, nodeSide);
                }
            }
        }

        // ── Pass 2: nodes ──────────────────────────────────────────────────
        // Rooms are drawn at their raw map pixels, Genie 4 style: a 9px box
        // (scaled) centred on the position. On the community's 10px-grid maps
        // neighbouring boxes sit ~1px apart, exactly as in Genie 4 — so the
        // current-room ring and the selection outline are painted AFTER every
        // box (below) rather than inline, or a neighbour would cover them.
        Rect? currentRect = null, selectedRect = null;
        foreach (var node in Zone.Nodes.Values)
        {
            if (node.Z != Level) continue;

            var isSelected = SelectedNode is not null && node.Id == SelectedNode.Id;

            // While dragging the selected node, draw it under the cursor (a
            // visual-only offset) — its stored position doesn't change until
            // release, so the bounds origin stays stable and nothing else jumps.
            var rect = (_dragging && isSelected)
                ? new Rect(_cursor.X - nodeSide / 2, _cursor.Y - nodeSide / 2, nodeSide, nodeSide)
                : R(proj.NodeRect(node));
            var fill = ParseColor(node.Color) ?? DefaultNodeFill;

            context.FillRectangle(fill, rect);
            // Cross-zone connector rooms get a 2px blue border (Genie 4 "Other
            // map" boxes); ordinary rooms get the thin black border.
            context.DrawRectangle(node.IsCrossZone ? CrossZonePen : NodePen, rect);

            if (CurrentNode is not null && node.Id == CurrentNode.Id) currentRect  = rect;
            if (isSelected)                                           selectedRect = rect;
        }

        if (currentRect is { } cur)
        {
            // Slight outset so the highlight stroke doesn't overlap the fill.
            // User-chosen "here I am" colour wins over the default red.
            var pen = CurrentRoomBrush is null ? CurrentPen : new Pen(CurrentRoomBrush, 2.5);
            context.DrawRectangle(pen, cur.Inflate(1.5));
        }

        // Edit-mode selection outline (drawn outset further than the
        // current-room ring so both are visible on the active room).
        if (selectedRect is { } sel)
            context.DrawRectangle(SelectedPen, sel.Inflate(3.5));

        // ── Pass 3: map labels (<label> elements) ──────────────────────────
        // Genie 4 paints the zone's free-floating <label> text (landmark names
        // like "East Gate", "Guard House", "Driftwood Designs") in black at the
        // positions the map author placed them — anchored top-left, no collision
        // avoidance (the placements are hand-tuned). The label goes through the
        // SAME projection as the rooms, so it lands where it sits in Genie 4
        // (the old cell-corner anchor put every label half a cell up-left).
        // Node notes are NOT drawn here: they are #goto aliases, surfaced in the
        // hover badge instead. This is why a cross-zone room no longer shows its
        // raw "Map31_…xml|…" note.
        var labelSize  = proj.LabelFontSize;                 // Genie 4: default font × scale
        var labelBrush = LabelTextBrush ?? RoomLabelBrush;   // user colour, default black

        if (FullLabels)
        foreach (var label in Zone.Labels)
        {
            if (label.Z != Level || string.IsNullOrEmpty(label.Text)) continue;

            var isSel = EditMode && ReferenceEquals(label, SelectedLabel);
            var ft = new FormattedText(
                label.Text, System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, Typeface.Default, labelSize,
                isSel ? SelectedLabelTextBrush : labelBrush);

            // Genie 4: a selected label is white text on a blue box with a black
            // border. While it is being dragged it follows the cursor (visual
            // only — its stored position changes on release).
            var origin = (_draggingLabel && isSel)
                ? _cursor - _labelGrab
                : P(proj.LabelOrigin(label));
            if (isSel)
            {
                var box = new Rect(origin.X, origin.Y, ft.Width + 2, proj.LabelRowHeight);
                context.FillRectangle(SelectedLabelFillBrush, box);
                context.DrawRectangle(NodePen, box);
            }
            context.DrawText(ft, new Point(origin.X + 1, origin.Y + 1));
        }

        // ── Pass 4: hover badge ───────────────────────────────────────────
        if (_hoveredNode is { } hovered)
            DrawHoverBadge(context, hovered);

        // ── Pass 5: colour legend (#157) ──────────────────────────────────
        if (ShowLegend)
            DrawLegend(context, proj);
    }

    // ── Legend (#157) — lists ONLY what the current level actually draws ──
    // A fixed seven-row key was both incomplete (coloured rooms, ghost floors
    // and the edit selection had no row) and noisy (a zone with no climb arc
    // still showed "Climb"). BuildLegend walks the same nodes/arcs Pass 0–2
    // paint and emits one row per thing that is on screen, so the panel is as
    // small as the map allows — one room on a flat zone gives a one-row key.

    internal enum LegendStyle
    {
        /// <summary>A filled room box (the colour is the FILL, thin black border).</summary>
        Room,
        /// <summary>An outlined box: the colour is a 2px ring on a white box (cross-zone
        /// border, current-room ring, edit selection).</summary>
        Ring,
        /// <summary>A coloured line (arcs + the exit stub).</summary>
        Line,
        /// <summary>The off-level ghost box: alpha-white fill and border over the map
        /// background, exactly as Pass 0 paints it.</summary>
        Ghost,
    }

    internal readonly record struct LegendEntry(LegendStyle Style, Color Color, string Label, bool Dashed = false);

    /// <summary>
    /// The community Maps-repo colour key (github.com/GenieClient/Maps README,
    /// "Color order indicates priority"), in README order. A room whose fill
    /// matches an entry gets that meaning in the legend. Any other fill is
    /// listed by its hex: the map maintainers audited the off-key colours in
    /// #automapper (May 2026) and consider them legacy mistakes with no agreed
    /// meaning, so inventing a label for them would be wrong. The May 2026
    /// re-org proposal (drop Navy/Sand, add GoldenRod, pink Nexus) never
    /// reached the README or the map data, so it is deliberately NOT here.
    /// </summary>
    internal static readonly (Color color, string label)[] RoomColourKey =
    {
        (Color.FromRgb(0xFF, 0x00, 0xFF), "Portal / transport"),
        (Color.FromRgb(0x00, 0xFF, 0x00), "Bank / services"),
        (Color.FromRgb(0xFF, 0x80, 0x00), "Guildleader"),
        (Color.FromRgb(0x00, 0xBF, 0x80), "Auto-healer"),
        (Color.FromRgb(0xFF, 0x00, 0x00), "Shop"),
        (Color.FromRgb(0xFF, 0xFF, 0x00), "Stat training"),
        (Color.FromRgb(0x00, 0x00, 0xFF), "Water (swim)"),
        (Color.FromRgb(0x00, 0x00, 0x80), "Underwater (drowning)"),
        (Color.FromRgb(0xFF, 0xBF, 0x00), "Roundtime / maze"),
        (Color.FromRgb(0x99, 0x33, 0x00), "Mining"),
        (Color.FromRgb(0x00, 0x80, 0x00), "Lumber"),
        (Color.FromRgb(0xC2, 0xB2, 0x80), "Ranger trailhead"),
        (Color.FromRgb(0x00, 0xFF, 0xFF), "PC housing"),
        (Color.FromRgb(0xA6, 0xA3, 0xD9), "Pilgrim shrine"),
        (Color.FromRgb(0x40, 0x00, 0x40), "Depart room"),
        (Color.FromRgb(0x80, 0x00, 0x80), "Favor altar"),
    };

    private static readonly Color SelectedColor = Color.FromRgb(0xff, 0xe0, 0x40);
    private static readonly Color StubColor     = Color.FromRgb(0x00, 0xC8, 0xE8);
    private static readonly Color ClimbColor    = Color.FromRgb(0x00, 0x80, 0x00);

    /// <summary>
    /// Build the legend rows for <paramref name="level"/> of <paramref name="zone"/>:
    /// only the things the render passes will actually paint there. Mirrors
    /// Pass 0 (ghost floors, when <paramref name="ghostsVisible"/>), Pass 1
    /// (arcs classified exactly as <see cref="EdgeKindFor"/> draws them, once
    /// per pair from the lower id; stubs only for the eight 2-D cardinals;
    /// spoiler arcs obey <paramref name="showSpoilers"/>) and Pass 2 (fills,
    /// cross-zone border, current ring, edit selection). Row order: rooms
    /// (plain, then colours in README priority, then off-key fills by hex),
    /// rings, ghost, then lines. Static + pure so the tests can pin it without
    /// a render surface.
    /// </summary>
    internal static List<LegendEntry> BuildLegend(MapZone zone, int level, MapNode? current, MapNode? selected,
                                                  bool showSpoilers, bool ghostsVisible, Color currentRing)
    {
        bool plain = false, crossZone = false, ghost = false;
        bool cardinal = false, go = false, climb = false, stub = false;
        var fills = new HashSet<Color>();

        foreach (var node in zone.Nodes.Values)
        {
            if (ghostsVisible && IsGhostFloor(node.Z, level)) ghost = true;
            if (node.Z != level) continue;

            if (TryParseFill(node.Color, out var fill)) fills.Add(fill);
            else plain = true;
            if (node.IsCrossZone) crossZone = true;

            foreach (var exit in node.Exits)
            {
                if (!showSpoilers && Genie.Core.Mapper.MoveVerb.IsSpoilerMove(exit.MoveCommand)) continue;
                if (exit.DestinationId is int destId
                    && zone.Nodes.TryGetValue(destId, out var dest)
                    && dest.Z == level)
                {
                    if (node.Id > dest.Id) continue;
                    switch (EdgeKindFor(exit))
                    {
                        case EdgeKind.Cardinal: cardinal = true; break;
                        case EdgeKind.Climb:    climb    = true; break;
                        default:                go       = true; break;
                    }
                }
                else if (IsStubDirection(exit.Direction, out _))
                {
                    stub = true;
                }
            }
        }

        var rows = new List<LegendEntry>();
        if (plain) rows.Add(new(LegendStyle.Room, Colors.White, "Room"));

        // Known colours in README priority order, then anything off-key by hex.
        foreach (var (color, label) in RoomColourKey)
            if (fills.Remove(color)) rows.Add(new(LegendStyle.Room, color, label));
        foreach (var other in fills.OrderBy(c => c.ToUInt32()))
            rows.Add(new(LegendStyle.Room, other, $"Other (#{other.R:X2}{other.G:X2}{other.B:X2})"));

        if (crossZone)                                   rows.Add(new(LegendStyle.Ring, Colors.Blue,   "Cross-zone room"));
        if (current  is not null && current.Z  == level) rows.Add(new(LegendStyle.Ring, currentRing,   "Current room *"));
        if (selected is not null && selected.Z == level) rows.Add(new(LegendStyle.Ring, SelectedColor, "Selected room", Dashed: true));
        if (ghost)                                       rows.Add(new(LegendStyle.Ghost, Colors.White, "Floor above / below"));

        if (cardinal) rows.Add(new(LegendStyle.Line, Colors.Black, "Path"));
        if (go)       rows.Add(new(LegendStyle.Line, Colors.Blue,  "Go / up / down"));
        if (climb)    rows.Add(new(LegendStyle.Line, ClimbColor,   "Climb"));
        if (stub)     rows.Add(new(LegendStyle.Line, StubColor,    "Exit (neighbour not mapped)"));
        return rows;
    }

    /// <summary>A node's fill as the canvas paints it, if it is anything other
    /// than the default white box. Named colours ("Red", "Blue") parse like hex;
    /// alpha is dropped so "#FFFF0000" and "#FF0000" are the same key.</summary>
    internal static bool TryParseFill(string? hex, out Color fill)
    {
        fill = default;
        if (string.IsNullOrWhiteSpace(hex) || !Color.TryParse(hex, out var c)) return false;
        fill = Color.FromRgb(c.R, c.G, c.B);
        return fill != Colors.White;
    }

    /// <summary>Draw the legend for the current level (see <see cref="BuildLegend"/>).
    /// Pinned to a clear corner of the visible viewport (parent ScrollViewer
    /// offset + a scroll-repaint hook) so it never covers a room and doesn't
    /// slide as the map pans. A trailing "*" marks a user-configurable colour
    /// (AutoMapper Settings). Nothing on the level → no panel at all.</summary>
    private void DrawLegend(DrawingContext context, MapProjection proj)
    {
        var currentRing = CurrentRoomBrush is ISolidColorBrush scb ? scb.Color : Color.FromRgb(0xff, 0x40, 0x40);
        var rows = BuildLegend(Zone!, Level, CurrentNode, SelectedNode, ShowSpoilers, AutoMapperAlpha > 0, currentRing);
        if (rows.Count == 0) return;

        const double pad = 8, row = 16, sw = 12, gap = 8;
        var typeface = new Typeface("Segoe UI, Consolas, monospace");
        const double fontSize = 11;

        // Measure the widest label to size the panel.
        double maxText = 0;
        var texts = new FormattedText[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            texts[i] = new FormattedText(rows[i].Label, System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, typeface, fontSize, RoomLabelBrush);
            if (texts[i].Width > maxText) maxText = texts[i].Width;
        }

        double w = pad + sw + gap + maxText + pad;
        double h = pad + rows.Count * row + pad;

        // Place the key in whichever VIEWPORT corner is clear of the drawn rooms,
        // so it never covers a room (#157 follow-up). Anchored in canvas coords to
        // the visible region (parent ScrollViewer offset) + repainted on scroll,
        // so it stays in a clear corner as the map pans. Each corner is tested
        // against the ACTUAL room boxes on the current level — not the zone's
        // whole bounding box, which on a dense map (Throne City) covers every
        // corner and made the test always fall through even when a corner had
        // no rooms in it. Falls back to bottom-left when every corner really
        // does have rooms — the panel is semi-transparent for that case.
        var viewport = this.FindAncestorOfType<ScrollViewer>() is { } sv
            ? new Rect(sv.Offset.X, sv.Offset.Y, sv.Viewport.Width, sv.Viewport.Height)
            : new Rect(Bounds.Size);

        bool CornerClear(Rect c)
        {
            foreach (var n in Zone!.Nodes.Values)
                if (n.Z == Level && R(proj.NodeRect(n)).Intersects(c))
                    return false;
            return true;
        }

        const double m = 8;
        var bottomLeft = new Rect(viewport.Left + m,      viewport.Bottom - h - m, w, h);
        var candidates = new[]
        {
            new Rect(viewport.Right - w - m, viewport.Bottom - h - m, w, h),  // bottom-right
            new Rect(viewport.Right - w - m, viewport.Top + m,        w, h),  // top-right
            bottomLeft,                                                       // bottom-left
            new Rect(viewport.Left + m,      viewport.Top + m,        w, h),  // top-left
        };
        var panel = bottomLeft;
        foreach (var c in candidates)
            if (CornerClear(c)) { panel = c; break; }

        context.FillRectangle(new SolidColorBrush(Color.FromArgb(0xCC, 0xff, 0xff, 0xff)), panel, 4);
        context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)), 1.0), panel, 4, 4);

        var bg = MapBackgroundBrush ?? BackgroundBrush;
        for (int i = 0; i < rows.Count; i++)
        {
            var (style, color, _, dashed) = rows[i];
            var brush = new SolidColorBrush(color);
            double cy = panel.Y + pad + i * row + row / 2;
            double x0 = panel.X + pad;
            var swatch = new Rect(x0, cy - sw / 2, sw, sw);
            switch (style)
            {
                case LegendStyle.Line:
                    context.DrawLine(new Pen(brush, 2.0), new Point(x0, cy), new Point(x0 + sw, cy));
                    break;
                case LegendStyle.Room:
                    // The colour IS the fill, with the map's thin black node border.
                    context.FillRectangle(brush, swatch);
                    context.DrawRectangle(NodePen, swatch);
                    break;
                case LegendStyle.Ring:
                    // White box, colour as a 2px border (cross-zone / current / selected).
                    context.FillRectangle(DefaultNodeFill, swatch);
                    var ring = new Pen(brush, 2.0);
                    if (dashed) ring.DashStyle = new DashStyle(new double[] { 2, 2 }, 0);
                    context.DrawRectangle(ring, swatch);
                    break;
                case LegendStyle.Ghost:
                    // Alpha-white over the map background, exactly as Pass 0 paints a ghost.
                    var ghostBrush = new SolidColorBrush(Color.FromArgb((byte)AutoMapperAlpha, 0xff, 0xff, 0xff));
                    context.FillRectangle(bg, swatch);
                    context.FillRectangle(ghostBrush, swatch);
                    context.DrawRectangle(new Pen(ghostBrush, EdgeWidth), swatch);
                    break;
            }
            var ft = texts[i];
            context.DrawText(ft, new Point(panel.X + pad + sw + gap, cy - ft.Height / 2));
        }
    }

    // ── Input ─────────────────────────────────────────────────────────────

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var props = e.GetCurrentPoint(this).Properties;

        // Right-click on a node = open a context menu with Go Here / Copy
        // Room ID / Show Details. We previously walked-immediately on right
        // click which made it too easy to start a long auto-walk by mis-
        // clicking; a menu adds one confirmation step. Genie 4's mapper
        // works the same way.
        //
        // Building the menu per-click rather than via a static ContextMenu
        // property because the items depend on which node was hit — the
        // menu needs the MapNode reference captured at click time.
        if (props.IsRightButtonPressed)
        {
            // Always open the unified menu. Node actions (Go Here / Copy Room ID
            // / Edit Exit) grey out when the click missed a room; the window
            // actions (Float / Close) are always live. The dock chrome's own
            // menu is suppressed in OnContextRequested so only this one shows.
            var at = e.GetPosition(this);
            ShowContextMenu(HitTest(at), at);
            e.Handled = true;
            return;
        }

        // Middle-button drag pans the view in any mode (universal grab-scroll).
        if (props.IsMiddleButtonPressed)
        {
            BeginPan(e);
            e.Handled = true;
            return;
        }

        // Ctrl+Left-Click on a room = Go Here (walk to it) — the same action as
        // the right-click "Go Here" menu item (NodeClickedCommand). A held
        // modifier can't mis-fire the way a plain left-click would, so this
        // restores the Genie 4 click-to-walk muscle memory without the
        // accidental-walk problem that pushed Go Here onto the context menu.
        // Works in both navigate and edit modes and takes priority over
        // select / drag / pan.
        if (props.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            var node = HitTest(e.GetPosition(this));
            if (node is not null && NodeClickedCommand?.CanExecute(node) == true)
                NodeClickedCommand.Execute(node);
            e.Handled = true;
            return;
        }

        if (props.IsLeftButtonPressed && EditMode)
        {
            // Edit mode: left-click selects the hit node (or clears selection on
            // empty space) and, unless locked, begins a drag-to-move. Pressing
            // on empty space instead pans the view.
            Focus();   // so the Delete key reaches OnKeyDown
            _cursor     = e.GetPosition(this);
            _pressPoint = _cursor;
            var node    = HitTest(_cursor);
            // Rooms win over labels (a label's box can run under a room);
            // otherwise the LAST label under the pointer wins, as in Genie 4.
            var label   = node is null ? HitTestLabel(_cursor) : null;

            SelectedNode  = node;
            SelectedLabel = label;
            if (SelectNodeCommand?.CanExecute(node) == true)
                SelectNodeCommand.Execute(node);

            if (node is not null && !LockPositions)
            {
                // Cache the projection now so the pixel math stays stable for
                // the whole drag even as the visual node tracks the cursor.
                _dragProj  = GetProjection();
                _dragOrigX = node.PixelX;
                _dragOrigY = node.PixelY;
                _dragging = true;
                e.Pointer.Capture(this);
            }
            else if (label is not null && !LockPositions)
            {
                // Genie 4 "Move Nodes/Labels": labels drag like rooms.
                _dragProj      = GetProjection();
                _labelGrab     = _cursor - P(_dragProj.LabelOrigin(label));
                _labelOrigX    = label.X;
                _labelOrigY    = label.Y;
                _draggingLabel = true;
                e.Pointer.Capture(this);
            }
            else
            {
                // Empty space (or positions locked) → pan instead of move.
                BeginPan(e);
            }

            InvalidateVisual();
            e.Handled = true;
        }
        else if (props.IsLeftButtonPressed)
        {
            // Navigate mode: a plain left-click on a CROSS-ZONE room (blue
            // border) opens the connecting map — the room's note names the
            // adjacent zone file, and Genie 4's mapper followed it on click.
            // Ordinary rooms and empty space keep the grab-scroll pan (Go Here
            // stays on the right-click menu / Ctrl+Click, so a plain left-drag
            // is free to pan; a press ON a connector trades that pan start for
            // the switch, which is what the blue border advertises).
            var node = HitTest(e.GetPosition(this));
            if (node is { IsCrossZone: true } &&
                CrossZoneClickedCommand?.CanExecute(node) == true)
            {
                CrossZoneClickedCommand.Execute(node);
                e.Handled = true;
                return;
            }
            BeginPan(e);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Suppress the dock chrome's per-window context menu (Float / Close Window)
    /// when a right-click lands on a room node — otherwise BOTH menus open on the
    /// same click. Our node menu is built imperatively in
    /// <see cref="OnPointerPressed"/> and opened with <c>menu.Open(this)</c>;
    /// that path does NOT consume Avalonia's separate <c>ContextRequested</c>
    /// routed event, so without this override the event bubbles to the wrapping
    /// ContentControl (ToolControlCachedSkin.axaml) and its ContextMenu opens too.
    /// Marking the event handled here stops that second menu. When the click
    /// misses every node we leave it unhandled, so right-clicking empty map space
    /// still surfaces the chrome's Float / Close Window menu.
    /// </summary>
    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        // The map builds one combined menu in OnPointerPressed (room actions +
        // the window Float/Close actions). Unconditionally swallow this event so
        // the dock chrome's separate per-window menu never opens over the map —
        // one menu, not two.
        e.Handled = true;
    }

    private ScrollViewer? _legendScroller;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // The legend (#157) is drawn pinned to the viewport's bottom-left, but the
        // canvas doesn't otherwise repaint on scroll — so it would appear to slide
        // with the map. Repaint on scroll so it stays put.
        _legendScroller = this.FindAncestorOfType<ScrollViewer>();
        if (_legendScroller is not null)
            _legendScroller.ScrollChanged += OnScrollerScrollChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_legendScroller is not null)
            _legendScroller.ScrollChanged -= OnScrollerScrollChanged;
        _legendScroller = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnScrollerScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (ShowLegend) InvalidateVisual();
    }

    /// <summary>Start a grab-scroll pan: cache the parent ScrollViewer and the
    /// pointer's position within it, capture the pointer, show the move cursor.</summary>
    private void BeginPan(PointerPressedEventArgs e)
    {
        _scroller = this.FindAncestorOfType<ScrollViewer>();
        if (_scroller is null) return;   // nothing to scroll (e.g. designer)
        _panLast  = e.GetPosition(_scroller);
        _panning  = true;
        e.Pointer.Capture(this);
        Cursor = new Cursor(StandardCursorType.SizeAll);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (_panning)
        {
            _panning = false;
            e.Pointer.Capture(null);
            Cursor = Cursor.Default;
            e.Handled = true;
            return;
        }

        if (_draggingLabel)
        {
            _draggingLabel = false;
            e.Pointer.Capture(null);
            if (SelectedLabel is { } label && DragMoved())
            {
                // Drop the label's top-left (cursor minus the grab offset) back
                // into map pixels, snapped like a room — Genie 4 moves labels
                // and rooms with the same `% 10` snap.
                var proj   = _dragProj ?? GetProjection();
                var origin = _cursor - _labelGrab;
                var mx = MapProjection.SnapMapPx(proj.ToMapX(origin.X), SnapToGrid);
                var my = MapProjection.SnapMapPx(proj.ToMapY(origin.Y), SnapToGrid);
                label.X = mx / MapProjection.G4GridPx;
                label.Y = my / MapProjection.G4GridPx;
                if ((label.X != _labelOrigX || label.Y != _labelOrigY)
                    && LabelMovedCommand?.CanExecute(label) == true)
                    LabelMovedCommand.Execute(label);
            }
            _dragProj = null;
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (!_dragging) return;

        _dragging = false;
        e.Pointer.Capture(null);

        // A press-and-release that never travelled is a click (select), not a
        // move: without the threshold, clicking a room that sits off the snap
        // grid (Crossing has 265 at odd pixels) would re-snap it and dirty the
        // zone.
        if (SelectedNode is { } node && DragMoved())
        {
            // Convert the release point back to map pixels through the
            // projection cached at drag start. SnapToGrid rounds to Genie 4's
            // 10px grid (MapForm's `% 10`); off, to the nearest whole pixel.
            // The room's raw PixelX/PixelY are written — NOT the 20px grid
            // cell — so a room on a 10px-spaced street stays on that street
            // instead of being re-quantised onto its neighbour.
            var proj = _dragProj ?? GetProjection();
            var newX = MapProjection.SnapMapPx(proj.ToMapX(_cursor.X), SnapToGrid);
            var newY = MapProjection.SnapMapPx(proj.ToMapY(_cursor.Y), SnapToGrid);

            // Only commit + mark dirty when the room actually moved. A plain
            // click (press+release with no drag) lands back on the same pixel
            // (or the same snap cell) and must not dirty the zone.
            if (newX != _dragOrigX || newY != _dragOrigY)
            {
                node.PixelX = newX;
                node.PixelY = newY;
                if (NodeMovedCommand?.CanExecute(node) == true)
                    NodeMovedCommand.Execute(node);
            }
        }
        _dragProj = null;

        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        // Delete removes the selected room in edit mode (Genie 4 "Remove
        // Selected Nodes/Labels"). Gated on EditMode so the key is inert in
        // the read-only navigator.
        if (EditMode && e.Key == Key.Delete && SelectedNode is { } node)
        {
            if (RemoveNodeCommand?.CanExecute(node) == true)
            {
                RemoveNodeCommand.Execute(node);
                SelectedNode = null;
                e.Handled = true;
            }
        }
        else if (EditMode && e.Key == Key.Delete && SelectedLabel is { } label)
        {
            if (RemoveLabelCommand?.CanExecute(label) == true)
            {
                RemoveLabelCommand.Execute(label);
                SelectedLabel = null;
                e.Handled = true;
            }
        }
    }

    /// <summary>True when the pointer travelled far enough since the press to
    /// count as a drag rather than a click.</summary>
    private bool DragMoved()
    {
        var dx = _cursor.X - _pressPoint.X;
        var dy = _cursor.Y - _pressPoint.Y;
        return Math.Sqrt(dx * dx + dy * dy) >= DragThreshold;
    }

    /// <summary>
    /// Builds and opens the map's single unified right-click <see cref="ContextMenu"/>.
    /// The menu shape is constant so it's predictable: a header, the room actions
    /// (Go Here / Copy Room ID / Edit Exit ▶), and the window actions
    /// (Float&#8239;/&#8239;Re-dock and Close Window, folded in from the hosting
    /// dockable's <see cref="WindowMenuModel"/> — the same model the dock chrome
    /// uses). Room actions <b>grey out</b> when <paramref name="node"/> is null
    /// (the click missed a room) rather than disappearing, so there's never a
    /// second menu and the user always sees every option.
    /// </summary>
    private void ShowContextMenu(MapNode? node, Point at)
    {
        var items = new List<Control>();

        // A label under the click (only when no room is) gets its own Remove
        // item in edit mode and names the header.
        var hitLabel = node is null && EditMode ? HitTestLabel(at) : null;

        // Header: the room this menu acts on, or a placeholder when the click
        // missed every room. IsHitTestVisible=false makes it a non-selectable
        // label; FontWeight=Bold marks it as a header.
        var headerText = node is null
            ? (hitLabel is null ? "(no room here)" : $"Label \"{hitLabel.Text}\"")
            : (string.IsNullOrWhiteSpace(node.Title) ? "(unnamed room)" : node.Title)
              + (!string.IsNullOrEmpty(node.ServerRoomId) ? $"  #{node.ServerRoomId}" : "");
        items.Add(new MenuItem { Header = headerText, IsHitTestVisible = false, FontWeight = FontWeight.Bold });
        items.Add(new Separator());

        // --- Room actions — enabled only when a room was actually hit. ---
        var goHere = new MenuItem
        {
            Header    = "Go Here",
            IsEnabled = node is not null && NodeClickedCommand?.CanExecute(node) == true
        };
        if (node is not null)
            goHere.Click += (_, _) =>
            {
                if (NodeClickedCommand?.CanExecute(node) == true)
                    NodeClickedCommand.Execute(node);
            };
        items.Add(goHere);

        var copyId = new MenuItem { Header = "Copy Room ID", IsEnabled = node is not null };
        if (node is not null)
            copyId.Click += async (_, _) =>
            {
                var idText = !string.IsNullOrEmpty(node.ServerRoomId)
                    ? $"#{node.ServerRoomId}"
                    : node.Id.ToString();
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard is not null)
                    await clipboard.SetTextAsync(idText);
            };
        items.Add(copyId);

        // "Set Waypoint" / "Show Path" are deferred — they need pathfinding
        // surface area on MapperViewModel that isn't shipped yet.
        // var setWaypoint = new MenuItem { Header = "Set as Waypoint" };
        // var showPath    = new MenuItem { Header = "Show Path"      };

        // Edit Exit ▶ {verb} submenu — one item per exit on the node. Off-node
        // (or no exits / no command wired) it shows as a greyed stub so the menu
        // shape stays constant.
        items.Add((node is not null ? BuildEditExitSubmenu(node) : null)
                  ?? new MenuItem { Header = "Edit Exit", IsEnabled = false });

        // Edit-mode-only: Remove Room. Greyed off-node. Mirrors the Delete key +
        // the toolbar Remove button; the menu item makes it discoverable.
        if (EditMode && RemoveNodeCommand is not null)
        {
            var remove = new MenuItem
            {
                Header     = "Remove Room",
                IsEnabled  = node is not null,
                Foreground = new SolidColorBrush(Color.FromRgb(0xff, 0x99, 0x99))
            };
            if (node is not null)
                remove.Click += (_, _) =>
                {
                    if (RemoveNodeCommand?.CanExecute(node) == true)
                    {
                        RemoveNodeCommand.Execute(node);
                        if (SelectedNode?.Id == node.Id) SelectedNode = null;
                    }
                };
            items.Add(remove);
        }

        // Edit-mode-only label actions (Genie 4 LabelDetails "New" / "Remove"):
        // add a label at the click point, or remove the label under it.
        if (EditMode)
        {
            var proj = GetProjection();
            if (AddLabelCommand is not null && proj.Any)
            {
                var add = new MenuItem { Header = "Add Label Here" };
                add.Click += (_, _) =>
                {
                    var l = new MapLabel
                    {
                        Text = "New Label",
                        X    = MapProjection.SnapMapPx(proj.ToMapX(at.X), SnapToGrid) / MapProjection.G4GridPx,
                        Y    = MapProjection.SnapMapPx(proj.ToMapY(at.Y), SnapToGrid) / MapProjection.G4GridPx,
                        Z    = Level,
                    };
                    if (AddLabelCommand?.CanExecute(l) == true) AddLabelCommand.Execute(l);
                };
                items.Add(add);
            }
            if (RemoveLabelCommand is not null)
            {
                var removeLabel = new MenuItem
                {
                    Header     = hitLabel is null ? "Remove Label" : $"Remove Label \"{hitLabel.Text}\"",
                    IsEnabled  = hitLabel is not null,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xff, 0x99, 0x99))
                };
                if (hitLabel is not null)
                    removeLabel.Click += (_, _) =>
                    {
                        if (RemoveLabelCommand?.CanExecute(hitLabel) == true)
                        {
                            RemoveLabelCommand.Execute(hitLabel);
                            if (ReferenceEquals(SelectedLabel, hitLabel)) SelectedLabel = null;
                        }
                    };
                items.Add(removeLabel);
            }
        }

        // --- Window actions — Float/Re-dock + Close Window, pulled from the same
        // WindowMenuModel the dock chrome binds to, so behaviour is identical;
        // they just live in this one menu now instead of a second one. ---
        var wm = FindWindowMenu();
        if (wm is not null)
        {
            var windowItems = new List<Control>();
            if (wm.ShowFloat && wm.ToggleFloatCommand is not null)
            {
                wm.RefreshFloatState();   // pick the correct "Float" vs "Re-dock" verb
                windowItems.Add(new MenuItem { Header = wm.FloatHeader, Command = wm.ToggleFloatCommand });
            }
            if (wm.ShowClose && wm.CloseCommand is not null)
                windowItems.Add(new MenuItem { Header = "Close Window", Command = wm.CloseCommand });

            if (windowItems.Count > 0)
            {
                items.Add(new Separator());
                items.AddRange(windowItems);
            }
        }

        // Dismiss any menu still open from a previous right-click before showing
        // the new one — otherwise two (or more) stack up, since our Handled
        // right-click suppresses the light-dismiss that would normally close it.
        _openMenu?.Close();

        var menu = new ContextMenu { ItemsSource = items };
        _openMenu = menu;
        menu.Closed += (_, _) => { if (ReferenceEquals(_openMenu, menu)) _openMenu = null; };

        // Show the menu programmatically. Avalonia's ContextMenu.Open()
        // opens it next to the placement target's cursor position by
        // default; passing `this` anchors it to the canvas.
        menu.Open(this);
    }

    /// <summary>
    /// Walk the visual tree to the hosting dockable's <see cref="WindowMenuModel"/>
    /// — the same model the dock chrome binds Float / Close to. Lets the map fold
    /// those window-level actions into its own single context menu. Returns null
    /// when the canvas isn't hosted in a dockable (e.g. designer preview).
    /// </summary>
    private WindowMenuModel? FindWindowMenu()
    {
        foreach (var ancestor in this.GetVisualAncestors())
            if (ancestor is Control { DataContext: IWindowMenuHost { WindowMenu: { } wm } })
                return wm;
        return null;
    }

    /// <summary>
    /// Build the "Edit Exit ▶" submenu for a node — one item per exit.
    /// Returns null when there are no exits or no <see cref="EditExitCommand"/>
    /// is wired (designer preview / standalone canvas).
    /// </summary>
    private MenuItem? BuildEditExitSubmenu(MapNode node)
    {
        if (EditExitCommand is null) return null;
        if (node.Exits.Count == 0) return null;

        var submenu = new MenuItem { Header = "Edit Exit" };
        var children = new List<MenuItem>();
        foreach (var exit in node.Exits)
        {
            var verb = !string.IsNullOrEmpty(exit.MoveCommand)
                ? exit.MoveCommand
                : exit.Direction.ToString().ToLowerInvariant();

            // Annotate the menu text if the exit already has requirements
            // or wait times set — quick at-a-glance signal of "this arc
            // already has community data."
            var hasMeta = !string.IsNullOrEmpty(exit.Requires)
                       || exit.RtCost.HasValue
                       || exit.WaitMin.HasValue
                       || !string.IsNullOrEmpty(exit.Notes);
            var label = hasMeta ? $"{verb}  ●" : verb;

            var item = new MenuItem { Header = label };
            // Capture node + exit by value so each click invokes against
            // the right pair. (NodeClickedCommand uses Avalonia's `Tag`
            // pattern; for two-arg commands we wrap as a tuple and let
            // the consuming MapperViewModel destructure.)
            var localNode = node;
            var localExit = exit;
            item.Click += (_, _) =>
            {
                if (EditExitCommand?.CanExecute((localNode, localExit)) == true)
                    EditExitCommand.Execute((localNode, localExit));
            };
            children.Add(item);
        }
        submenu.ItemsSource = children;
        return submenu;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _cursor = e.GetPosition(this);

        // Grab-scroll panning: shift the ScrollViewer offset by the pointer
        // delta (measured in the viewer's own coords so changing the offset
        // doesn't feed back into the next reading).
        if (_panning && _scroller is not null)
        {
            var p     = e.GetPosition(_scroller);
            var delta = p - _panLast;
            _scroller.Offset -= delta;
            _panLast = p;
            return;
        }

        // While dragging a node, just track the cursor and repaint — the node
        // is drawn under the cursor and committed to a grid cell on release.
        if (_dragging || _draggingLabel)
        {
            _hoveredNode = null;   // suppress the hover badge mid-drag
            InvalidateVisual();
            return;
        }

        var hit = HitTest(_cursor);
        if (!ReferenceEquals(hit, _hoveredNode))
        {
            _hoveredNode = hit;
            InvalidateVisual();
        }
        else if (_hoveredNode is not null)
        {
            // Same node but cursor moved within it — repaint so the badge
            // tracks the cursor position.
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hoveredNode is not null)
        {
            _hoveredNode = null;
            InvalidateVisual();
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        // Vertical wheel: +1 = up = zoom in, -1 = down = zoom out.
        // Multiplicative step keeps the perceived speed constant across zoom
        // levels (small steps near 1x, big steps near 4x).
        var factor = e.Delta.Y > 0 ? ZoomStep : 1.0 / ZoomStep;
        Zoom = Zoom * factor;
        // Mark handled so the surrounding ScrollViewer doesn't also consume
        // the wheel event and try to scroll.
        e.Handled = true;
    }

    /// <summary>
    /// Hit-test against the visible (current-level) nodes. Returns the first
    /// node whose rectangle contains <paramref name="point"/>, or null.
    /// </summary>
    private MapNode? HitTest(Point point)
    {
        if (Zone is null || Zone.Nodes.Count == 0) return null;

        // The same projection Render draws with, so a click lands on the box
        // the user sees.
        var proj = GetProjection();
        if (!proj.Any) return null;

        foreach (var node in Zone.Nodes.Values)
        {
            if (node.Z != Level) continue;
            if (proj.NodeRect(node).Contains(point.X, point.Y))
                return node;
        }
        return null;
    }

    /// <summary>The label under <paramref name="point"/> on the current floor,
    /// or null — also null while labels are hidden (nothing to grab).</summary>
    private MapLabel? HitTestLabel(Point point)
    {
        if (Zone is null || !FullLabels) return null;
        var proj = GetProjection();
        if (!proj.Any) return null;
        return FindLabelAt(Zone, Level, proj, MeasureLabel, point.X, point.Y);
    }

    /// <summary>
    /// Label hit-test against the rectangles the label pass paints: the LAST
    /// label in file order under the point wins when two overlap (Genie 4
    /// <c>FindLabel</c>: "so the highest label gets selected"). Static and
    /// measure-injected so the test project can pin it without a render surface.
    /// </summary>
    internal static MapLabel? FindLabelAt(MapZone zone, int level, MapProjection proj,
                                          LabelMeasure measure, double x, double y)
    {
        MapLabel? hit = null;
        foreach (var label in zone.Labels)
        {
            if (label.Z != level || string.IsNullOrEmpty(label.Text)) continue;
            var r = proj.LabelRect(label, measure(label.Text, proj.LabelFontSize));
            if (r.Contains(x, y)) hit = label;
        }
        return hit;
    }

    private void DrawHoverBadge(DrawingContext context, MapNode node)
    {
        var title = string.IsNullOrEmpty(node.Title) ? "(no title)" : node.Title;
        var line2Parts = new List<string> { $"id {node.Id}" };
        if (!string.IsNullOrEmpty(node.ServerRoomId)) line2Parts.Add($"server {node.ServerRoomId}");
        if (!string.IsNullOrEmpty(node.Notes))        line2Parts.Add(node.Notes);
        if (node.Tags.Count > 0)                      line2Parts.Add(string.Join(" ", node.Tags.Select(t => "@" + t)));
        var subText = string.Join("  ·  ", line2Parts);

        var titleText = new FormattedText(title,   System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, Typeface.Default, 12, HoverTitleBrush);
        var subTextFt = new FormattedText(subText, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, Typeface.Default, 10, HoverSubBrush);

        const double pad = 6.0;
        var w = Math.Max(titleText.Width, subTextFt.Width) + pad * 2;
        var h = titleText.Height + subTextFt.Height + pad * 2 + 2;

        // Offset from cursor so the badge doesn't sit under the mouse pointer.
        var x = _cursor.X + 12;
        var y = _cursor.Y + 12;
        // Keep the badge inside the control bounds so it doesn't get clipped.
        if (x + w > Bounds.Width)  x = _cursor.X - w - 12;
        if (y + h > Bounds.Height) y = _cursor.Y - h - 12;
        if (x < 0) x = 0;
        if (y < 0) y = 0;

        var rect = new Rect(x, y, w, h);
        context.FillRectangle(HoverBackgroundBrush, rect, 4);
        context.DrawRectangle(HoverBorderPen, rect, 4);

        context.DrawText(titleText, new Point(x + pad, y + pad));
        context.DrawText(subTextFt, new Point(x + pad, y + pad + titleText.Height + 2));
    }

    // ── Geometry helpers (instance so they pick up live Zoom) ─────────────

    /// <summary>Draw a short cyan tick out of a room's edge in <paramref name="dir"/>'s
    /// direction (#157). Only the eight 2-D cardinals get a stub — up/down/out/in
    /// have no on-map direction. The grid Δ maps straight to screen space (grid Y
    /// and screen Y both grow downward), normalised so diagonals aren't longer.</summary>
    private void DrawExitStub(DrawingContext ctx, Point center, Direction dir, double nodeSide, Pen? pen = null)
    {
        if (!IsStubDirection(dir, out var d)) return;

        var len  = Math.Sqrt(d.dx * (double)d.dx + d.dy * (double)d.dy);
        var ux   = d.dx / len;
        var uy   = d.dy / len;
        var half = nodeSide / 2;
        var stub = nodeSide * 0.7;
        var start = new Point(center.X + ux * half,          center.Y + uy * half);
        var end   = new Point(center.X + ux * (half + stub), center.Y + uy * (half + stub));
        ctx.DrawLine(pen ?? StubPen, start, end);
    }

    /// <summary>The eight 2-D cardinals get an exit stub; up/down/out/in/none have
    /// no on-map direction. Returns the grid Δ for the caller's geometry.</summary>
    internal static bool IsStubDirection(Direction dir, out (int dx, int dy, int dz) d)
    {
        if (!DirectionHelper.Delta.TryGetValue(dir, out d)) return false;
        return d.dz == 0 && (d.dx != 0 || d.dy != 0);
    }

    /// <summary>A floor drawn as ghosts under <paramref name="level"/>: the one
    /// directly above or below it.</summary>
    internal static bool IsGhostFloor(int z, int level) => Math.Abs(z - level) == 1;

    /// <summary>
    /// Enumerate the arcs Pass 0 paints for the ghost floors of
    /// <paramref name="level"/>, in the same shape Pass 1 uses for the current
    /// floor: <paramref name="edge"/> once per arc joining two rooms on the SAME
    /// ghost floor (from the lower id, so a two-way arc draws once), and
    /// <paramref name="stub"/> for a cardinal arc whose neighbour isn't on that
    /// floor (no destination, another floor, or unrecorded). Non-cardinal stubs
    /// are filtered here, and spoiler arcs obey <paramref name="showSpoilers"/>
    /// exactly as on the current floor (#254). Static and callback-shaped so the
    /// test project can count the geometry without a render surface.
    /// </summary>
    internal static void ForEachGhostEdge(MapZone zone, int level, bool showSpoilers,
                                          Action<MapNode, MapNode> edge,
                                          Action<MapNode, Direction> stub)
    {
        foreach (var node in zone.Nodes.Values)
        {
            if (!IsGhostFloor(node.Z, level)) continue;
            foreach (var exit in node.Exits)
            {
                if (!showSpoilers && MoveVerb.IsSpoilerMove(exit.MoveCommand)) continue;
                if (exit.DestinationId is int destId
                    && zone.Nodes.TryGetValue(destId, out var dest)
                    && dest.Z == node.Z)
                {
                    if (node.Id > dest.Id) continue;
                    edge(node, dest);
                }
                else if (IsStubDirection(exit.Direction, out _))
                {
                    stub(node, exit.Direction);
                }
            }
        }
    }

    private static IBrush? ParseColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        if (Color.TryParse(hex, out var c)) return new SolidColorBrush(c);
        return null;
    }

    internal enum EdgeKind { Cardinal, Go, Climb }

    /// <summary>
    /// Classify an exit the way the Genie 4 line palette draws it: cardinal
    /// arcs black, climb arcs green, and everything else (go-doors, up/down/out,
    /// swim, etc.) blue. Genie 5 collapses all non-compass arcs into
    /// <see cref="Direction.None"/>, so climb is recognised from the move verb.
    /// Shared by the renderer (<see cref="EdgePenFor"/>) and the legend, so the
    /// key can never disagree with the map.
    /// </summary>
    internal static EdgeKind EdgeKindFor(MapExit exit)
    {
        switch (exit.Direction)
        {
            case Direction.North: case Direction.NorthEast:
            case Direction.East:  case Direction.SouthEast:
            case Direction.South: case Direction.SouthWest:
            case Direction.West:  case Direction.NorthWest:
                return EdgeKind.Cardinal;
            case Direction.Up: case Direction.Down: case Direction.Out:
                return EdgeKind.Go;
            default: // None / In — disambiguate climb from go via the verb
                var mc = exit.MoveCommand;
                return !string.IsNullOrEmpty(mc)
                       && mc.TrimStart().StartsWith("climb", StringComparison.OrdinalIgnoreCase)
                    ? EdgeKind.Climb
                    : EdgeKind.Go;
        }
    }

    private static Pen EdgePenFor(MapExit exit) => EdgeKindFor(exit) switch
    {
        EdgeKind.Cardinal => EdgePenCardinal,   // black
        EdgeKind.Climb    => EdgePenClimb,      // green
        _                 => EdgePenGo,         // blue
    };

    private void DrawCenteredMessage(DrawingContext context, string text)
    {
        var ft = new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            12,
            EmptyMessageBrush);
        ft.TextAlignment = TextAlignment.Center;
        var origin = new Point(
            (Bounds.Width  - ft.Width)  / 2,
            (Bounds.Height - ft.Height) / 2);
        context.DrawText(ft, origin);
    }
}
