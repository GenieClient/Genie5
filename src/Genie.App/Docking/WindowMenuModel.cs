using System.Windows.Input;
using ReactiveUI;

namespace Genie.App.Docking;

/// <summary>
/// A dockable that exposes a window right-click menu. Implemented by every
/// Tool / Document so <see cref="GenieDockFactory"/> can attach a
/// <see cref="WindowMenuModel"/> uniformly, and the shared ContextMenu (in the
/// ToolControl theme + the game-window template) can bind to it.
/// </summary>
public interface IWindowMenuHost
{
    WindowMenuModel? WindowMenu { get; set; }
}

/// <summary>
/// Backs the per-window right-click context menu — Genie 4's window menu:
/// <c>Copy All</c> / <c>Clear</c> / <c>Time Stamp</c> / <c>Name List Only</c> /
/// <c>Pause Scrolling</c> / <c>Float</c> / <c>Close Window</c>.
/// One instance per dockable, built by <see cref="GenieDockFactory"/> with only
/// the actions that apply to that window type; each menu item hides itself when
/// its command / capability is absent (so a Vitals window shows just "Float" +
/// "Close Window", while a stream window shows everything).
/// </summary>
public sealed class WindowMenuModel : ReactiveObject
{
    private bool _isTimestampOn;
    private bool _isNameListOnlyOn;
    private bool _isEchoToMainOn;
    private bool _isScrollPaused;
    private bool _isWordWrapOn;
    private bool _isConfigBarOn;
    private bool _isFlashOn;
    private bool _isFloating;
    private readonly Action<bool>? _onFlashToggled;
    private readonly Action<bool>? _onTimestampToggled;
    private readonly Action<bool>? _onNameListOnlyToggled;
    private readonly Action<bool>? _onEchoToMainToggled;
    private readonly Action<bool>? _onScrollPauseToggled;
    private readonly Action<bool>? _onWordWrapToggled;
    private readonly Action<bool>? _onConfigBarToggled;
    private readonly Func<bool>?   _floatStateProbe;
    private readonly Action?       _onToggleTitleBar;
    private readonly Func<bool>?   _titleBarHiddenProbe;
    private bool _isDockedTitleBarHidden;
    private bool _isAloneInFrame;
    private readonly Action<bool>? _onDockedTitleBarToggled;
    private readonly Func<bool>?   _aloneInFrameProbe;

    public WindowMenuModel(
        ICommand?     clear                 = null,
        ICommand?     close                 = null,
        bool          timestampOn           = false,
        Action<bool>? onTimestampToggled    = null,
        bool          nameListOnlyOn        = false,
        Action<bool>? onNameListOnlyToggled = null,
        ICommand?     copyAll               = null,
        bool          scrollPausedOn        = false,
        Action<bool>? onScrollPauseToggled  = null,
        Action?       onToggleFloat         = null,
        Func<bool>?   floatStateProbe       = null,
        ICommand?     saveAs                = null,
        ICommand?     find                  = null,
        bool          wordWrapOn            = true,
        Action<bool>? onWordWrapToggled     = null,
        bool          echoToMainOn          = true,
        Action<bool>? onEchoToMainToggled   = null,
        Action?       onToggleTitleBar      = null,
        Func<bool>?   titleBarHiddenProbe   = null,
        bool          configBarOn           = true,
        Action<bool>? onConfigBarToggled    = null,
        bool          flashOn               = true,
        Action<bool>? onFlashToggled        = null,
        bool          dockedTitleBarHidden    = false,
        Action<bool>? onDockedTitleBarToggled = null,
        Func<bool>?   aloneInFrameProbe       = null)
    {
        ClearCommand           = clear;
        CloseCommand           = close;
        CopyAllCommand         = copyAll;
        SaveAsCommand          = saveAs;
        FindCommand            = find;
        _isTimestampOn         = timestampOn;
        _onTimestampToggled    = onTimestampToggled;
        _isNameListOnlyOn      = nameListOnlyOn;
        _onNameListOnlyToggled = onNameListOnlyToggled;
        _isEchoToMainOn        = echoToMainOn;
        _onEchoToMainToggled   = onEchoToMainToggled;
        _isScrollPaused        = scrollPausedOn;
        _onScrollPauseToggled  = onScrollPauseToggled;
        _isWordWrapOn          = wordWrapOn;
        _onWordWrapToggled     = onWordWrapToggled;
        _isConfigBarOn         = configBarOn;
        _onConfigBarToggled    = onConfigBarToggled;
        _isFlashOn             = flashOn;
        _onFlashToggled        = onFlashToggled;
        _floatStateProbe       = floatStateProbe;
        _onToggleTitleBar      = onToggleTitleBar;
        _titleBarHiddenProbe   = titleBarHiddenProbe;
        _isDockedTitleBarHidden  = dockedTitleBarHidden;
        _onDockedTitleBarToggled = onDockedTitleBarToggled;
        _aloneInFrameProbe       = aloneInFrameProbe;

        if (onToggleFloat is not null)
            ToggleFloatCommand = ReactiveCommand.Create(() =>
            {
                onToggleFloat();
                RefreshFloatState();   // verb flips immediately after the action
            });

        // One item, two targets (public #299): a floating window collapses its
        // float's title bar (session-only, #181); a docked window alone in its
        // frame flips the persisted per-window setting instead.
        if (onToggleTitleBar is not null || onDockedTitleBarToggled is not null)
            ToggleTitleBarCommand = ReactiveCommand.Create(() =>
            {
                if (_isFloating && onToggleTitleBar is not null)
                    onToggleTitleBar();
                else if (onDockedTitleBarToggled is not null)
                    IsDockedTitleBarHidden = !IsDockedTitleBarHidden;
                RefreshFloatState();   // "Hide" ⇄ "Show" flips immediately
            });

        // Seed the float verb from the live tree (almost always "Float" — the
        // window starts docked); RefreshFloatState keeps it honest on each open.
        RefreshFloatState();
    }

    public ICommand? ClearCommand       { get; }
    public ICommand? CloseCommand       { get; }
    public ICommand? CopyAllCommand     { get; }
    /// <summary>"Save As…" — export the window buffer to a text file (#120).</summary>
    public ICommand? SaveAsCommand      { get; }
    /// <summary>"Find…" — open the window's in-window search bar (#120).</summary>
    public ICommand? FindCommand        { get; }
    public ICommand? ToggleFloatCommand { get; }
    /// <summary>Collapse / restore the title bar: the float's own bar while floating
    /// (#181), the frame header while docked alone in a frame (public #299). The
    /// item hides itself when neither applies, via <see cref="ShowHideTitleBar"/>.</summary>
    public ICommand? ToggleTitleBarCommand { get; }

    // Capability flags drive each MenuItem's IsVisible — a window only shows the
    // items it actually supports. Timestamp / Name List Only / Pause are
    // "supported" when their toggle handler was supplied; Float when a toggle
    // action was supplied.
    public bool ShowClear        => ClearCommand          is not null;
    public bool ShowClose        => CloseCommand          is not null;
    public bool ShowCopyAll      => CopyAllCommand        is not null;
    public bool ShowSaveAs       => SaveAsCommand         is not null;
    public bool ShowFind         => FindCommand           is not null;
    public bool ShowTimestamp    => _onTimestampToggled   is not null;
    public bool ShowNameListOnly => _onNameListOnlyToggled is not null;
    public bool ShowEchoToMain   => _onEchoToMainToggled  is not null;
    public bool ShowPauseScroll  => _onScrollPauseToggled is not null;
    public bool ShowWordWrap     => _onWordWrapToggled    is not null;
    /// <summary>"Show Config Bar" — panels with a settings strip across the top
    /// (the Experience window's Density / Track gain / G4 layout row, and the
    /// Objects window's header + Creatures checkbox).</summary>
    public bool ShowConfigBar    => _onConfigBarToggled   is not null;
    /// <summary>"Flash on Activity" — windows wired for the unread-tab flash
    /// (ActivityTool derivatives with settings).</summary>
    public bool ShowFlash        => _onFlashToggled       is not null;
    public bool ShowFloat        => ToggleFloatCommand    is not null;

    /// <summary>"Hide/Show Title Bar" shows in two cases. <b>Floating</b> (#181):
    /// the float's own title bar. <b>Docked and alone in its frame</b> (public
    /// #299): the frame's header — a tool frame's title bar, or the Game group's
    /// tab strip — which only repeats the one window's name. A docked window that
    /// shares its frame keeps the header (it is how you switch tabs), so the item
    /// hides there, unless the setting is already on and still needs turning off.
    /// Kept live by <see cref="RefreshFloatState"/> on each menu-open.</summary>
    public bool ShowHideTitleBar =>
        _isFloating
            ? _onToggleTitleBar is not null
            : _onDockedTitleBarToggled is not null && (_isAloneInFrame || _isDockedTitleBarHidden);

    /// <summary>"Hide Title Bar" when the bar is shown, "Show Title Bar" when it's
    /// collapsed. Floating reads the live float window (#181); docked reads the
    /// persisted per-window setting (public #299).</summary>
    public string TitleBarHeader =>
        (_isFloating ? (_titleBarHiddenProbe?.Invoke() ?? false) : _isDockedTitleBarHidden)
            ? "Show Title Bar" : "Hide Title Bar";

    /// <summary>Public #299: collapse this window's frame header while it is docked
    /// alone in that frame. Mirrors <c>WindowSettings.HideTitleBarWhenAlone</c>;
    /// setting it runs the toggle handler, which persists. The header itself is
    /// collapsed by <see cref="DockedTitleBar"/>, which watches this property.</summary>
    public bool IsDockedTitleBarHidden
    {
        get => _isDockedTitleBarHidden;
        set
        {
            if (_isDockedTitleBarHidden == value) return;
            this.RaiseAndSetIfChanged(ref _isDockedTitleBarHidden, value);
            this.RaisePropertyChanged(nameof(TitleBarHeader));
            _onDockedTitleBarToggled?.Invoke(value);
        }
    }

    /// <summary>Render the separator above "Close Window" only when Close
    /// coexists with at least one item above it (so a Close-only menu has no
    /// dangling leading separator).</summary>
    public bool ShowCloseSeparator =>
        ShowClose && (ShowCopyAll || ShowClear || ShowSaveAs || ShowFind
                      || ShowTimestamp || ShowNameListOnly || ShowEchoToMain
                      || ShowPauseScroll || ShowWordWrap || ShowConfigBar
                      || ShowFlash || ShowFloat || ShowHideTitleBar);

    /// <summary>Time Stamp checkbox state. Set by the TwoWay menu binding —
    /// flipping it runs the toggle handler (which updates the window's
    /// <c>WindowSettings.Timestamp</c> + persists).</summary>
    public bool IsTimestampOn
    {
        get => _isTimestampOn;
        set
        {
            if (_isTimestampOn == value) return;
            this.RaiseAndSetIfChanged(ref _isTimestampOn, value);
            _onTimestampToggled?.Invoke(value);
        }
    }

    /// <summary>Name List Only checkbox state — see <see cref="IsTimestampOn"/>.</summary>
    public bool IsNameListOnlyOn
    {
        get => _isNameListOnlyOn;
        set
        {
            if (_isNameListOnlyOn == value) return;
            this.RaiseAndSetIfChanged(ref _isNameListOnlyOn, value);
            _onNameListOnlyToggled?.Invoke(value);
        }
    }

    /// <summary>"Show in Main Window" checkbox state — mirrors
    /// <c>WindowSettings.EchoToMain</c>. On by default: the stream also echoes
    /// into the main game window. Flipping it off opts this stream out of Main
    /// (it still shows in its own panel). See <see cref="IsTimestampOn"/>.</summary>
    public bool IsEchoToMainOn
    {
        get => _isEchoToMainOn;
        set
        {
            if (_isEchoToMainOn == value) return;
            this.RaiseAndSetIfChanged(ref _isEchoToMainOn, value);
            _onEchoToMainToggled?.Invoke(value);
        }
    }

    /// <summary>Pause Scrolling checkbox state. When on, the window stops
    /// auto-following new lines (freezes the scroll position); turning it off
    /// snaps back to the newest line. Transient — not persisted, matching
    /// Genie 4 (a fresh session always starts following).</summary>
    public bool IsScrollPaused
    {
        get => _isScrollPaused;
        set
        {
            if (_isScrollPaused == value) return;
            this.RaiseAndSetIfChanged(ref _isScrollPaused, value);
            _onScrollPauseToggled?.Invoke(value);
        }
    }

    /// <summary>Word Wrap checkbox state (#120). Flipping it runs the toggle
    /// handler, which updates <c>WindowSettings.WordWrap</c> + persists; the
    /// window relayouts live via the tool's ToolTextWrapping binding.</summary>
    public bool IsWordWrapOn
    {
        get => _isWordWrapOn;
        set
        {
            if (_isWordWrapOn == value) return;
            this.RaiseAndSetIfChanged(ref _isWordWrapOn, value);
            _onWordWrapToggled?.Invoke(value);
        }
    }

    /// <summary>"Show Config Bar" checkbox state — mirrors the panel's config-bar
    /// visibility (Experience: <c>experienceconfigbar</c>; Objects:
    /// <c>objectsconfigbar</c>). Flipping it runs the
    /// toggle handler, which updates the view-model + persists.</summary>
    public bool IsConfigBarOn
    {
        get => _isConfigBarOn;
        set
        {
            if (_isConfigBarOn == value) return;
            this.RaiseAndSetIfChanged(ref _isConfigBarOn, value);
            _onConfigBarToggled?.Invoke(value);
        }
    }

    /// <summary>"Flash on Activity" checkbox state — mirrors
    /// <c>WindowSettings.FlashOnActivity</c>. On by default: the tab title
    /// pulses when data lands while the tab is backgrounded. Flipping it runs
    /// the toggle handler (which updates the setting + persists); turning it
    /// off also stops an in-progress flash (ActivityTool clears on the
    /// settings change). See <see cref="IsTimestampOn"/>.</summary>
    public bool IsFlashOn
    {
        get => _isFlashOn;
        set
        {
            if (_isFlashOn == value) return;
            this.RaiseAndSetIfChanged(ref _isFlashOn, value);
            _onFlashToggled?.Invoke(value);
        }
    }

    /// <summary>"Float" when the window is docked, "Re-dock" when it's already
    /// floating in its own top-level window. Refreshed by
    /// <see cref="RefreshFloatState"/> when the menu opens (the user can drag a
    /// window out / back without the menu's knowledge).</summary>
    public string FloatHeader => _isFloating ? "Re-dock" : "Float";

    /// <summary>Re-read the live float state from the dock tree and update
    /// <see cref="FloatHeader"/>. Called on menu-open (via
    /// <see cref="WindowMenuBehavior"/>) and right after a Float / Re-dock.</summary>
    public void RefreshFloatState()
    {
        // #181: the title-bar item's visibility and its Hide/Show verb both depend on
        // live float-window state, so re-raise them on every open even when the float
        // flag itself didn't move (the user may have hidden/shown the bar since).
        // Public #299: "alone in its frame" also moves without the menu knowing
        // (a tab dragged in or out), so re-probe it on the same schedule.
        if (_aloneInFrameProbe is not null)
            _isAloneInFrame = _aloneInFrameProbe();
        this.RaisePropertyChanged(nameof(ShowHideTitleBar));
        this.RaisePropertyChanged(nameof(TitleBarHeader));

        if (_floatStateProbe is null) return;
        var floating = _floatStateProbe();
        if (_isFloating == floating) return;
        _isFloating = floating;
        this.RaisePropertyChanged(nameof(FloatHeader));
        this.RaisePropertyChanged(nameof(ShowHideTitleBar));
        this.RaisePropertyChanged(nameof(TitleBarHeader));
    }

    /// <summary>Mirror an external Time Stamp change (e.g. the Layout tab) into
    /// the checkmark without re-invoking the toggle handler.</summary>
    public void SyncTimestamp(bool value) =>
        this.RaiseAndSetIfChanged(ref _isTimestampOn, value, nameof(IsTimestampOn));

    /// <summary>Mirror an external Name List Only change into the checkmark.</summary>
    public void SyncNameListOnly(bool value) =>
        this.RaiseAndSetIfChanged(ref _isNameListOnlyOn, value, nameof(IsNameListOnlyOn));

    /// <summary>Mirror an external "Show in Main Window" change (e.g. the Layout
    /// tab) into the checkmark without re-invoking the toggle handler.</summary>
    public void SyncEchoToMain(bool value) =>
        this.RaiseAndSetIfChanged(ref _isEchoToMainOn, value, nameof(IsEchoToMainOn));

    /// <summary>Mirror an external Word Wrap change into the checkmark.</summary>
    public void SyncWordWrap(bool value) =>
        this.RaiseAndSetIfChanged(ref _isWordWrapOn, value, nameof(IsWordWrapOn));

    /// <summary>Mirror an external config-bar visibility change (e.g. the value
    /// seeded from settings.cfg on connect) into the checkmark without
    /// re-invoking the toggle handler.</summary>
    public void SyncConfigBar(bool value) =>
        this.RaiseAndSetIfChanged(ref _isConfigBarOn, value, nameof(IsConfigBarOn));

    /// <summary>Mirror an external "Hide Title Bar" change (e.g. the Layout tab)
    /// into the model without re-invoking the toggle handler.</summary>
    public void SyncDockedTitleBar(bool value)
    {
        this.RaiseAndSetIfChanged(ref _isDockedTitleBarHidden, value, nameof(IsDockedTitleBarHidden));
        this.RaisePropertyChanged(nameof(TitleBarHeader));
    }

    /// <summary>Mirror an external "Flash on Activity" change (e.g. the Layout
    /// tab) into the checkmark without re-invoking the toggle handler.</summary>
    public void SyncFlash(bool value) =>
        this.RaiseAndSetIfChanged(ref _isFlashOn, value, nameof(IsFlashOn));
}
