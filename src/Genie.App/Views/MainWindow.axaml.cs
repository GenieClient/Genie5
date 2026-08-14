using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.ReactiveUI;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Genie.App.Controls;
using Genie.App.ViewModels;
using ReactiveUI;
using System.Windows.Input;

namespace Genie.App.Views;

public partial class MainWindow : ReactiveWindow<MainWindowViewModel>
{
    public MainWindow()
    {
        InitializeComponent();

        // Main-window size/position is no longer auto-restored on startup — the
        // app always opens at the XAML default size. Geometry now rides on a
        // saved layout profile (captured/applied via the VM's
        // CaptureWindowGeometry / ApplyWindowGeometry bridge, wired below).

        // Ctrl+Right-Click on selected game text → append selection to the
        // command bar. Window-level handler so the gesture works regardless of
        // which docked panel currently shows the SelectableTextBlock (main game
        // window, side stream tabs, room/backpack panels — they all use the
        // same control type). Registered with HandledEventsToo so we still see
        // the press even if SelectableTextBlock's own handler marks it handled.
        AddHandler(InputElement.PointerPressedEvent, OnGlobalPointerPressed,
                   RoutingStrategies.Bubble, handledEventsToo: true);

        // The default right-click context menu ("Copy / Ctrl+C") on
        // SelectableTextBlock opens via a separate ContextRequested event —
        // marking PointerPressed handled doesn't suppress it. Intercept here
        // so Ctrl+Right doesn't pop the menu while we're pasting.
        AddHandler(Control.ContextRequestedEvent, OnGlobalContextRequested,
                   RoutingStrategies.Tunnel, handledEventsToo: true);

        // PageUp/PageDown page the selected game window; Ctrl+PageUp/PageDown
        // jump to its top/bottom (#136, Genie 3/4 parity — focus stays in the
        // command bar the whole time). Tunnel phase so the keystroke wins
        // before the focused control can consume it, same as Genie 4 where
        // these keys always scroll the active output window.
        AddHandler(InputElement.KeyDownEvent, OnPageScrollKeyDown,
                   RoutingStrategies.Tunnel);

        // Suppress BARE-Alt menu arming (smoke 2026-08-04/06 finding #11). A
        // lone Alt tap arms the menu bar, which makes menu ACCESS KEYS live:
        // the next typed 'f' opens File and a following 'r' fires Record
        // Session, so ordinary game commands ("forage …") silently toggled
        // the raw-XML recorder five times across one evening (recording
        // coverage went random). Two dead ends first: swallowing the Alt
        // key-up in a tunnel handler loses the ordering race (Avalonia's
        // AccessKeyHandler registers its own tunnel hooks at window
        // construction, before any ctor AddHandler here), and the
        // IInputRoot.AccessKeyHandler seam that would let us detach the menu
        // is internal in 11.3. Public-API counter instead: keyboard arming
        // manifests as the main Menu OPENING with the pointer somewhere else
        // entirely — close it the instant that happens. Real mouse use always
        // has the pointer over the menu bar when it opens, so click
        // navigation is untouched. The cost is Alt/Alt+letter menu
        // accelerators, which a text-first game client gladly trades away (a
        // typed line must never detour into the menu bar); Genie 4's WinForms
        // menu never armed on bare Alt either. Wired on Opened so the Menu
        // exists in the visual tree.
        Opened += (_, _) =>
        {
            var menu = this.GetVisualDescendants().OfType<Menu>().FirstOrDefault();
            if (menu is null) return;
            menu.Opened += (_, _) =>
            {
                if (!menu.IsPointerOver) menu.Close();
            };
        };

        // Keyboard macros — F-keys + modifier+letter/digit. Bubble phase
        // (the default) so any inner control that wants to capture the
        // keystroke first (e.g. the MacrosPanel KeyBox capture mode) can
        // mark the event handled and skip the macro fire entirely.
        AddHandler(InputElement.KeyDownEvent, OnGlobalKeyDown,
                   RoutingStrategies.Bubble, handledEventsToo: false);

        // Type-anywhere → command bar (public #141, Genie 3/4 parity): plain
        // typing that lands on a non-editable control (game text, a panel, a
        // just-clicked button) is redirected into the command bar, so the
        // player never has to click back into the input box. Bubble phase
        // without handledEventsToo: any real text control (command bar,
        // mapper notes, mob-pattern box, …) handles its own TextInput first
        // and we never see it. Bound macros swallow their KeyDown before the
        // platform raises TextInput, so macro keys don't leak here either.
        AddHandler(InputElement.TextInputEvent, OnGlobalTextInput,
                   RoutingStrategies.Bubble, handledEventsToo: false);

        // AutoWalk attended-detection: when the window loses focus for >60s
        // we pause any in-flight walk per the compliance review's
        // attended-mode requirement. Activated cancels the pending timer
        // so brief alt-tabs don't pause unnecessarily. The service handles
        // the timer + state itself; we just feed the events in.
        Activated   += (_, _) => ViewModel?.Mapper?.AutoWalk?.OnWindowActivated();
        Deactivated += (_, _) => ViewModel?.Mapper?.AutoWalk?.OnWindowDeactivated();

        this.WhenActivated(d =>
        {
            // Bridge the main-window geometry to the VM so a saved layout can
            // capture/restore it (size, position, maximized). The VM owns no
            // Window reference, so it calls back through these hooks.
            ViewModel!.CaptureWindowGeometry = () =>
            {
                var maximized = WindowState == WindowState.Maximized;
                return (Bounds.Width, Bounds.Height, Position.X, Position.Y, maximized);
            };
            ViewModel!.ApplyWindowGeometry = (w, h, x, y, maximized) =>
            {
                if (maximized)
                {
                    WindowState = WindowState.Maximized;
                    return;
                }
                WindowState = WindowState.Normal;
                if (w >= 400) Width  = w;
                if (h >= 300) Height = h;
                // Only restore position if it looks sane (guards against a
                // window stranded off-screen after a monitor change).
                if (x > -50 && y > -50 && x < 20000 && y < 20000)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual;
                    Position = new PixelPoint(x, y);
                }
            };

            // Magic Panels (Genie 4 SetMagicPanels): collapse the status bar's
            // mana column so the other four vitals stretch equally — G4 flips
            // TableLayoutPanelBars.ColumnCount 5 ↔ 4. Done here because
            // ColumnDefinition.Width can't be data-bound from XAML; the mana
            // cell's own IsVisible binding hides the content.
            d(ViewModel!.Display.WhenAnyValue(x => x.ShowMagicPanels)
                .Subscribe(show => StatusBarGrid.ColumnDefinitions[1].Width =
                    show ? new GridLength(1, GridUnitType.Star) : new GridLength(0)));

            // Align Input to Game Window (Genie 4 SizeInputToGame — horizontal
            // only): pad the command bar's side margins so it spans the Game
            // window's horizontal extent. Recomputed on every dock layout pass
            // (re-dock, resize, splitter drag all funnel through LayoutUpdated);
            // the 0.5px equality guard stops margin-write → layout → recompute
            // loops. Game window floated out or missing → full-width fallback.
            void UpdateCommandBarAlignment()
            {
                var target = new Thickness(4, 2, 4, 2);
                if (ViewModel?.AlignInputToGame == true)
                {
                    // The game document's BODY presenter — largest visual
                    // presenting the GameTextDocument (the tab-strip header
                    // presenter shows the same content but is tiny).
                    var game = MainDock.GetVisualDescendants()
                        .OfType<Avalonia.Controls.Presenters.ContentPresenter>()
                        .Where(p => p.Content is Docking.GameTextDocument && p.Bounds.Width > 0)
                        .OrderByDescending(p => p.Bounds.Width * p.Bounds.Height)
                        .FirstOrDefault();
                    if (game is not null &&
                        game.TranslatePoint(new Point(0, 0), this) is { } tl)
                    {
                        var left  = Math.Max(4, tl.X);
                        var right = Math.Max(4, Bounds.Width - (tl.X + game.Bounds.Width));
                        if (Bounds.Width - left - right > 120)   // never squeeze the input away
                            target = new Thickness(left, 2, right, 2);
                    }
                }
                var cur = CommandBarGrid.Margin;
                if (Math.Abs(cur.Left - target.Left) > 0.5 || Math.Abs(cur.Right - target.Right) > 0.5)
                    CommandBarGrid.Margin = target;
            }

            EventHandler alignInputHandler = (_, _) => UpdateCommandBarAlignment();
            MainDock.LayoutUpdated += alignInputHandler;
            d(System.Reactive.Disposables.Disposable.Create(
                () => MainDock.LayoutUpdated -= alignInputHandler));
            d(ViewModel!.WhenAnyValue(x => x.AlignInputToGame)
                .Subscribe(_ => Dispatcher.UIThread.Post(UpdateCommandBarAlignment)));

            d(ViewModel!.ShowConnectDialog.RegisterHandler(async ctx =>
            {
                // Pass the previous session's actual config so the dialog
                // shows what the user actually used. The dialog itself
                // decides whether a saved profile matches the config (and
                // pre-selects it) or whether the credentials are bare (and
                // shows them with the profile dropdown empty).
                var dlg = new ConnectDialog
                {
                    DataContext = new ConnectDialogViewModel(
                        ViewModel.Profiles,
                        ViewModel.SaveProfiles,
                        ViewModel.LastConnectionConfig,
                        ViewModel.ReportConnectionFailure,
                        ViewModel.ListLayoutNamesFor)
                };
                var result = await dlg.ShowDialog<ConnectResult?>(this);
                ctx.SetOutput(result);
            }));

            d(ViewModel!.ShowDisplaySettingsDialog.RegisterHandler(async ctx =>
            {
                var dlg = new DisplaySettingsDialog
                {
                    // ThemeService powers the Theme tab (#20): dropdown +
                    // import/export/duplicate/delete over Config/Themes.
                    DataContext = new DisplaySettingsViewModel(ctx.Input, ViewModel!.Themes)
                };
                var ok = await dlg.ShowDialog<bool>(this);
                ctx.SetOutput(ok);
            }));

            d(ViewModel!.ShowConfigurationDialog.RegisterHandler(async ctx =>
            {
                try
                {
                    var dlg = new ConfigurationDialog { DataContext = ctx.Input };
                    await dlg.ShowDialog(this);
                }
                catch (Exception ex)
                {
                    Genie.App.Diagnostics.ErrorLog.Log("ConfigurationDialog.Show", ex);
                }
                ctx.SetOutput(System.Reactive.Unit.Default);
            }));

            d(ViewModel!.ShowGenie4ImportDialog.RegisterHandler(async ctx =>
            {
                try
                {
                    var dlg = new Genie4ImportDialog { DataContext = ctx.Input };
                    await dlg.ShowDialog(this);
                }
                catch (Exception ex)
                {
                    Genie.App.Diagnostics.ErrorLog.Log("Genie4ImportDialog.Show", ex);
                }
                ctx.SetOutput(System.Reactive.Unit.Default);
            }));

            d(ViewModel!.ShowEditExitDialog.RegisterHandler(async ctx =>
            {
                try
                {
                    var dlg = new EditExitDialog { DataContext = ctx.Input };
                    var ok = await dlg.ShowDialog<bool>(this);
                    ctx.SetOutput(ok);
                }
                catch (Exception ex)
                {
                    Genie.App.Diagnostics.ErrorLog.Log("EditExitDialog.Show", ex);
                    ctx.SetOutput(false);
                }
            }));

            d(ViewModel!.ShowZoneConnectionsDialog.RegisterHandler(async ctx =>
            {
                try
                {
                    var dlg = new ZoneConnectionsDialog { DataContext = ctx.Input };
                    await dlg.ShowDialog(this);
                }
                catch (Exception ex)
                {
                    Genie.App.Diagnostics.ErrorLog.Log("ZoneConnectionsDialog.Show", ex);
                }
                ctx.SetOutput(System.Reactive.Unit.Default);
            }));

            d(ViewModel!.ShowManageLayoutsDialog.RegisterHandler(async ctx =>
            {
                var dlg = new ManageLayoutsDialog { DataContext = ctx.Input };
                await dlg.ShowDialog(this);
                ctx.SetOutput(System.Reactive.Unit.Default);
            }));

            d(ViewModel!.ShowUpdatesDialog.RegisterHandler(async ctx =>
            {
                var dlg = new UpdatesDialog { DataContext = ctx.Input, ViewModel = ctx.Input };
                await dlg.ShowDialog(this);
                ctx.SetOutput(System.Reactive.Unit.Default);
            }));

            d(ViewModel!.ShowAboutDialog.RegisterHandler(async ctx =>
            {
                await new AboutDialog().ShowDialog(this);
                ctx.SetOutput(System.Reactive.Unit.Default);
            }));

            d(ViewModel!.ShowChangelogDialog.RegisterHandler(async ctx =>
            {
                await new ChangelogDialog().ShowDialog(this);
                ctx.SetOutput(System.Reactive.Unit.Default);
            }));

            d(ViewModel!.ShowMapperSettingsDialog.RegisterHandler(async ctx =>
            {
                // Needs the live config (created at connect); pre-connect, point
                // the user at connecting rather than opening an inert dialog.
                if (ViewModel!.Core?.Config is { } cfg)
                    await new MapperSettingsDialog(cfg, ViewModel!.Mapper).ShowDialog(this);
                else
                    await new NoticeDialog("AutoMapper Settings",
                        "Connect first — the AutoMapper settings load with your session.").ShowDialog(this);
                ctx.SetOutput(System.Reactive.Unit.Default);
            }));

            d(ViewModel!.ShowDisconnectNotice.RegisterHandler(async ctx =>
            {
                try
                {
                    await new NoticeDialog("Disconnected", ctx.Input).ShowDialog(this);
                }
                catch (Exception ex)
                {
                    // A modal needs a live owner; if the window is mid-close
                    // (app shutting down) ShowDialog can throw — swallow so the
                    // disconnect path doesn't surface a spurious error.
                    Genie.App.Diagnostics.ErrorLog.Log("NoticeDialog.Show", ex);
                }
                ctx.SetOutput(System.Reactive.Unit.Default);
            }));

            d(ViewModel!.ShowLayoutSavePrompt.RegisterHandler(async ctx =>
            {
                try
                {
                    var result = await SaveLayoutDialog.Show(this, ctx.Input);
                    ctx.SetOutput(result);
                }
                catch (Exception ex)
                {
                    Genie.App.Diagnostics.ErrorLog.Log("SaveLayoutDialog.Show", ex);
                    ctx.SetOutput(null);
                }
            }));

            // #20: "Save Current As…" theme name prompt. Existing custom
            // theme names show as a click-to-overwrite list, mirroring the
            // layout save dialog's affordance.
            d(ViewModel!.ShowThemeNamePrompt.RegisterHandler(async ctx =>
            {
                try
                {
                    var existing = ViewModel!.Themes.All
                        .Where(t => !t.IsBuiltIn)
                        .Select(t => t.Name)
                        .ToList();
                    ctx.SetOutput(await NamePromptDialog.Show(
                        this, "Theme name:", ctx.Input, "Save Theme As", existing));
                }
                catch (Exception ex)
                {
                    Genie.App.Diagnostics.ErrorLog.Log("ThemeNamePrompt.Show", ex);
                    ctx.SetOutput(null);
                }
            }));

            // #20: Edit Theme… editor. ShowDialog<bool> yields false on ✕ /
            // Cancel, which the VM's command treats as "restore pre-edit".
            d(ViewModel!.ShowThemeEditorDialog.RegisterHandler(async ctx =>
            {
                try
                {
                    var dlg = new ThemeEditorDialog { DataContext = ctx.Input };
                    ctx.SetOutput(await dlg.ShowDialog<bool>(this));
                }
                catch (Exception ex)
                {
                    Genie.App.Diagnostics.ErrorLog.Log("ThemeEditorDialog.Show", ex);
                    ctx.SetOutput(false);
                }
            }));

            // (#80 content-recycling band-aid removed.) The stacked-tab "wrong
            // view" bug is now fixed at the source: Themes/ToolControlCachedSkin.axaml
            // overrides the stock ToolControl with a cached, per-dockable content
            // host, so there is no shared recycled presenter to repair on add.

            // Command-line auto-connect (--host/--port/--profile). Fired here so
            // the dialog interaction handlers above are already registered (the
            // connect path itself doesn't need them, but anything it triggers
            // might). The VM guards against running more than once, so repeated
            // activations are harmless.
            _ = ViewModel!.RunStartupConnectAsync();
        });
    }

    /// <summary>
    /// Once the window is shown (so the DockControl is attached and a host
    /// window can be created), pop the Mapper out into its own floating window
    /// if the startup default armed it. Posted at Background priority so the
    /// dock tree has finished initialising before FloatDockable runs. No-op
    /// when a saved layout is showing (the flag is only armed for the default).
    /// </summary>
    protected override void OnOpened(System.EventArgs e)
    {
        base.OnOpened(e);
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => ViewModel?.TryFloatPendingMapper(),
            Avalonia.Threading.DispatcherPriority.Background);

        // #flash → taskbar/dock attention flash. Subscribed here (not in the
        // VM) because the flash needs the Window; may fire from a script
        // thread, so hop to the UI thread. Core is persistent for the app
        // lifetime, so a one-time hook is enough.
        if (!_flashHooked && ViewModel?.Core is { } core)
        {
            _flashHooked = true;
            core.FlashRequested += () =>
                Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => Services.WindowFlashService.Flash(this));
        }
    }

    private bool _flashHooked;

    /// <summary>
    /// Pull truth from the dock factory just before the Window menu renders so
    /// each check mark reflects the dock's actual state — guards against any
    /// close/move path that didn't trigger a sync event.
    /// </summary>
    private void OnWindowMenuOpened(object? sender, RoutedEventArgs e)
        => ViewModel?.RefreshVisibilityBools();

    // Scripts → External Editor: re-resolve the "current editor" caption on
    // open, so a change made in the Display Settings dialog (same setting)
    // shows here without a restart.
    private void OnScriptsEditorMenuOpened(object? sender, RoutedEventArgs e)
        => ViewModel?.Scripts.RefreshEditorDisplay();

    // Layout menu uses SubmenuOpened (not Command on the parent MenuItem,
    // which doesn't fire when the click opens a submenu) so the Load Layout ▶
    // child sees a freshly-populated SavedLayouts collection. Without this,
    // SavedLayouts stays empty and Avalonia renders the Load Layout item as
    // disabled because its ItemsSource has nothing in it.
    private void OnLayoutMenuOpened(object? sender, RoutedEventArgs e)
        => ViewModel?.RefreshLayoutListCommand.Execute().Subscribe();

    // #20: rebuild the Edit → Theme submenu each open. Built in code (not an
    // ItemsSource) because it mixes per-theme radio items with the static
    // Save Current As… / Reset entries — a single ItemsSource can't express
    // that, and SubmenuOpened guarantees fresh radio checks + newly-dropped
    // Config/Themes files either way.
    private void OnThemeMenuOpened(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm) return;
        vm.RefreshThemeListCommand.Execute().Subscribe();

        ThemeMenu.Items.Clear();
        foreach (var t in vm.ThemeMenuItems)
            ThemeMenu.Items.Add(new MenuItem
            {
                Header           = t.Display,
                ToggleType       = MenuItemToggleType.Radio,
                IsChecked        = t.IsCurrent,
                Command          = t.Command,
                CommandParameter = t,
            });
        ThemeMenu.Items.Add(new Separator());
        ThemeMenu.Items.Add(new MenuItem
        {
            Header  = "Edit Theme…",
            Command = vm.EditThemeCommand,
            [ToolTip.TipProperty] = "Open the theme editor: tweak every colour role with live preview, then save as a custom theme. Editing a built-in saves a copy.",
        });
        ThemeMenu.Items.Add(new MenuItem
        {
            Header  = "Save Current As…",
            Command = vm.SaveThemeAsCommand,
            [ToolTip.TipProperty] = "Snapshot the current colours (including any Display Settings tweaks) as a named custom theme in Config/Themes.",
        });
        ThemeMenu.Items.Add(new MenuItem
        {
            Header  = "Reset to Dark",
            Command = vm.ResetThemeCommand,
            [ToolTip.TipProperty] = "Back to the default Dark theme.",
        });
    }

    // Rebuild the Plugins-menu list when it opens, so newly-loaded plugins
    // (e.g. after Reload) show up.
    private void OnPluginsMenuOpened(object? sender, RoutedEventArgs e)
        => ViewModel?.RefreshPluginListCommand.Execute().Subscribe();

    /// <summary>
    /// Set to <c>true</c> right before we re-call <see cref="Window.Close()"/>
    /// after the confirm dialog returns Yes, so the second OnClosing pass
    /// short-circuits past the prompt instead of looping forever.
    /// </summary>
    private bool _closeConfirmed;

    /// <summary>
    /// Intercept window close to prompt for confirmation while a live game
    /// connection is active. Closing during disconnect drops straight through
    /// — no point in nagging when there's nothing to lose. Cancelling the
    /// close lets <see cref="ShowConfirmAndMaybeReclose"/> drive the async
    /// dialog without blocking the message pump.
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        // Window geometry + windowed-mode layout are no longer auto-saved on
        // close — they persist only when the user saves a layout profile.
        if (e.Cancel)            return;   // something upstream already vetoed
        if (_closeConfirmed)     return;   // second pass after user said Yes
        if (ViewModel?.IsConnected != true) return;
        // IgnoreCloseAlert (Genie 4 parity): user opted out of the
        // "still connected, really close?" prompt — let the close proceed.
        if (ViewModel?.Core?.Config.IgnoreCloseAlert == true) return;

        e.Cancel = true;
        ShowConfirmAndMaybeReclose();
    }

    /// <summary>
    /// Async tail of <see cref="OnClosing"/> — shows the modal confirmation
    /// dialog and re-issues <see cref="Window.Close()"/> if the user said Yes.
    /// async void is intentional here: this is event-driven UI glue, not a
    /// task that anything awaits.
    /// </summary>
    private async void ShowConfirmAndMaybeReclose()
    {
        // Try the saved-profile name first (most accurate, set explicitly by
        // the user), then the live GameState name (populated from the server's
        // <component id='pc name'> push during connect), then a generic
        // fallback for ad-hoc connections that never had a profile picked.
        var character = ViewModel?.ConnectedProfile?.CharacterName
                        ?? ViewModel?.Core?.State.CharacterName
                        ?? "your character";
        if (string.IsNullOrWhiteSpace(character)) character = "your character";

        var dlg = new ConfirmDialog(
            "Close Genie 5",
            $"You're still connected as {character}.\n\n" +
            "Closing will disconnect from the game. Continue?");

        var confirmed = await dlg.ShowDialog<bool>(this);
        if (!confirmed) return;

        _closeConfirmed = true;
        Close();
    }

    private void CommandInput_KeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel?.Command is not { } cmd) return;

        switch (e.Key)
        {
            case Key.Up:
                cmd.HistoryUp();
                e.Handled = true;
                MoveCaretToEnd(sender as TextBox);
                _tabMatches = null;       // any nav resets the tab-cycle
                break;
            case Key.Down:
                cmd.HistoryDown();
                e.Handled = true;
                MoveCaretToEnd(sender as TextBox);
                _tabMatches = null;
                break;
            case Key.Tab:
                if (HandleTabComplete(sender as TextBox,
                                       reverse: e.KeyModifiers.HasFlag(KeyModifiers.Shift)))
                    e.Handled = true;
                break;
            case Key.Enter:
                // Submit is normally wired via the XAML <KeyBinding
                // Gesture="Return">, which Avalonia matches against the
                // focused element and marks handled BEFORE the routed
                // KeyDown dispatch even reaches here — so on a real,
                // physically-typed Enter this case never runs. It only
                // fires for a KeyDown synthesized by ForwardKeyToBar
                // (OnGlobalKeyDown's forward-to-bar fallback), which raises
                // the event directly and so skips the KeyBindings pass.
                // Same CanExecute guard the KeyBinding itself uses, so the
                // two paths behave identically.
                if (((ICommand)cmd.SubmitCommand).CanExecute(null))
                {
                    ((ICommand)cmd.SubmitCommand).Execute(null);
                    e.Handled = true;
                }
                break;
            default:
                _tabMatches = null;       // any other key resets the cycle
                break;
        }
    }

    // ── Script-name tab completion state ────────────────────────────────────
    // Captured the first time the user hits Tab on a script prefix, then
    // re-used for subsequent Tabs to cycle through matches. Reset on any
    // other key so editing in the middle of a recalled completion doesn't
    // produce surprising re-completions.
    private string?              _tabPrefix;
    private IReadOnlyList<string>? _tabMatches;
    private int                  _tabIndex;

    /// <summary>
    /// Handles Tab in the command bar: if the current text looks like a
    /// script invocation (starts with <see cref="GenieConfig.ScriptChar"/>,
    /// no whitespace), cycles through matching <c>.cmd</c> / <c>.inc</c>
    /// scripts in the user's Scripts directory.
    /// <para>
    /// Returns true when the Tab was consumed (so the caller can mark
    /// <c>KeyEventArgs.Handled</c>), false when the prefix isn't script-
    /// shaped and the default Tab behavior (focus-shift) should fire.
    /// </para>
    /// </summary>
    private bool HandleTabComplete(TextBox? tb, bool reverse)
    {
        if (tb is null) return false;
        var vm = ViewModel;
        if (vm?.Command is null) return false;

        var text = tb.Text ?? "";
        if (text.Length == 0) return false;

        // Must start with the script-invocation character (default `.`)
        // and contain no whitespace — otherwise it's not a script call,
        // it's a regular game command that should pass Tab through.
        var scriptChar = '.';
        if (text[0] != scriptChar) return false;
        if (text.Any(char.IsWhiteSpace)) return false;

        var prefix = text.Substring(1); // strip the leading `.`

        // First Tab on this prefix? Build the match list and start at 0.
        // Subsequent Tabs reuse the list and bump the index — this is how
        // we get the bash-style cycle behavior.
        if (_tabMatches is null || _tabPrefix != prefix)
        {
            _tabPrefix  = prefix;
            _tabMatches = vm.GetScriptCompletions(prefix);
            _tabIndex   = reverse ? _tabMatches.Count - 1 : 0;
        }
        else
        {
            _tabIndex = reverse
                ? (_tabIndex - 1 + _tabMatches.Count) % _tabMatches.Count
                : (_tabIndex + 1) % _tabMatches.Count;
        }

        if (_tabMatches.Count == 0)
        {
            // No matches — keep the user's typed text, just consume the
            // Tab so focus doesn't jump away.
            _tabMatches = null;
            return true;
        }

        var match = _tabMatches[_tabIndex];
        // Two-way binding writes back to CommandText, then the dispatcher
        // post moves the caret after the binding has propagated.
        vm.Command.CommandText = scriptChar + match;
        MoveCaretToEnd(tb);
        return true;
    }

    /// <summary>
    /// After Up/Down recalls a previous command from history the TextBox's
    /// Text property is updated via the two-way binding, but the caret stays
    /// at index 0 — typing then inserts at the FRONT of the recalled line
    /// instead of appending at the end (counter-intuitive vs every shell and
    /// Genie 4). Posting through the dispatcher ensures the caret-set runs
    /// AFTER the bound text has actually been written to the control, which
    /// avoids a race with the binding update.
    /// </summary>
    private static void MoveCaretToEnd(TextBox? tb)
    {
        if (tb is null) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => tb.CaretIndex = tb.Text?.Length ?? 0);
    }

    /// <summary>
    /// Re-raises a KeyDown for <paramref name="key"/> directly on <paramref
    /// name="bar"/> (used by <see cref="OnGlobalKeyDown"/> to forward
    /// navigation/editing keys once no other control claimed them). This is
    /// a normal routed KeyDownEvent, so it drives the bar's own <c>OnKeyDown</c>
    /// override (TextBox's built-in Backspace/Delete/caret-move editing) and
    /// its instance handler <see cref="CommandInput_KeyDown"/> (Up/Down
    /// history recall, and Enter's submit fallback) exactly as if the key
    /// had been pressed with the bar already focused. It deliberately does
    /// NOT go through <c>KeyBindings</c> matching — those are resolved by
    /// Avalonia's <c>KeyboardDevice</c> against the focused element BEFORE
    /// routed dispatch even starts (so CommandInput's own <c>&lt;KeyBinding
    /// Gesture="Return"&gt;</c> only ever fires for a real, physically-typed
    /// Enter) — which is exactly why <c>CommandInput_KeyDown</c> carries its
    /// own manual Enter case: it's the fallback for this forwarded path.
    /// </summary>
    private static void ForwardKeyToBar(TextBox bar, Key key, KeyModifiers mods) =>
        bar.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Source = bar,
            Key = key,
            KeyModifiers = mods,
        });

    /// <summary>
    /// Handles Ctrl+Right-Click anywhere in the window. If the click landed
    /// on a <see cref="SelectableTextBlock"/> that has a non-empty selection,
    /// the selected text is appended to the command bar (with an auto-space
    /// separator when the existing text doesn't already end with whitespace),
    /// and focus moves to the command input so the user can hit Enter
    /// immediately.
    /// </summary>
    private void OnGlobalPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Right button only.
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsRightButtonPressed) return;

        // Ctrl modifier only — let plain right-click keep its default behavior
        // (context menu / nothing, depending on the control).
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;

        // Walk up the visual tree from the click source until we hit a
        // SelectableTextBlock. Inline children (Run, etc.) are the actual
        // hit-test source on text selections; the SelectableTextBlock itself
        // sits a level or two up.
        var visual = e.Source as Visual;
        while (visual is not null)
        {
            if (visual is SelectableTextBlock stb)
            {
                var selection = stb.SelectedText;
                if (!string.IsNullOrEmpty(selection))
                {
                    AppendToCommandBar(selection);
                    _suppressNextContextMenu = true;   // swallow the default Copy menu
                    e.Handled = true;
                }
                return;
            }
            visual = visual.GetVisualParent();
        }
    }

    /// <summary>
    /// Cancels the default right-click context menu in the one-shot tick
    /// immediately after a Ctrl+Right paste. <see cref="ContextRequestedEventArgs"/>
    /// doesn't expose modifier state directly, so we set this flag from the
    /// PointerPressed handler when the paste fires and clear it here.
    /// </summary>
    private bool _suppressNextContextMenu;

    private void OnGlobalContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (_suppressNextContextMenu)
        {
            _suppressNextContextMenu = false;
            e.Handled = true;
        }
    }

    /// <summary>
    /// PageUp/PageDown → scroll the selected game window (#136). Registered on
    /// the tunnel phase (see ctor). Plain = one page, Ctrl = top/bottom;
    /// Alt combos and multiline editors are left alone.
    /// </summary>
    private void OnPageScrollKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.PageUp or Key.PageDown)) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) return;

        // A focused multiline editor keeps the native page-the-caret behavior.
        if (FocusManager?.GetFocusedElement() is TextBox { AcceptsReturn: true }) return;

        if (PageScroll.HandleKey(e.Key, e.KeyModifiers))
            e.Handled = true;
    }

    /// <summary>
    /// Translates the keystroke into a Genie 4 macro key string and fires
    /// the matching macro action (if any) through the command pipeline.
    /// </summary>
    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        // Esc is the always-available kill switch (Genie 3/4 parity, #81): it
        // cancels any in-flight auto-walk AND aborts all running scripts. Per the
        // DR policy compliance review the user needs an obvious stop for any
        // automated traversal; Genie 3/4 also used Esc to abort scripts. Hits
        // before macros so an Esc-bound macro can't swallow the stop. When there's
        // nothing to stop, falls through so such a macro can still fire.
        if (e.Key == Key.Escape)
        {
            var stopped = false;

            if (ViewModel?.Mapper?.AutoWalk is { Current: not null } walk)
            {
                walk.Cancel("user pressed Esc");
                stopped = true;
            }

            if (ViewModel?.Core is { } core && core.Scripts.AnyRunning)
            {
                core.Commands.ProcessInput("#stopall");
                stopped = true;
            }

            if (stopped)
            {
                e.Handled = true;
                return;
            }
        }

        // #141: Backspace/Delete/Enter/arrows while no text control has focus →
        // forward to the command bar, same idea as the TextInput redirect
        // below (these keys produce no TextInput, so they need their own
        // hook). Only bare keypresses are forwarded — modified combos are
        // left alone in case they're macro-bound. A focused TextBox handles
        // its own copy of these keys and marks the event handled before this
        // bubble handler runs; the guard is belt-and-braces. The `e.Source`
        // check stops the re-raised KeyDown below (see ForwardKeyToBar) from
        // looping back into this same block once it bubbles back up here.
        if (e.KeyModifiers == KeyModifiers.None &&
            !ReferenceEquals(e.Source, CommandInput) &&
            FocusManager?.GetFocusedElement() is not TextBox &&
            CommandInput is { } bar)
        {
            switch (e.Key)
            {
                // TextBox's own OnKeyDown implements Backspace/Delete/caret
                // movement against whatever CaretIndex/selection it already
                // has, and CommandInput_KeyDown implements Up/Down history
                // and (for Enter) a manual submit fallback — so re-raising
                // the KeyDown directly on the bar after focusing gives all
                // of that for free, no hand-rolled logic needed here.
                case Key.Back:
                case Key.Delete:
                case Key.Left:
                case Key.Right:
                case Key.Up:
                case Key.Down:
                case Key.Enter:
                    bar.Focus();
                    ForwardKeyToBar(bar, e.Key, e.KeyModifiers);
                    e.Handled = true;
                    return;
            }
        }

        // The platform Find gesture (Cmd+F on macOS, Ctrl+F elsewhere) opens
        // whichever find UI the selected window's renderer provides: AvaloniaEdit's
        // search panel on editor-backed windows, the in-window Find bar on the
        // rest. Targeting is PageScroll's click-target either way, so Find follows
        // the same "active window" the PageUp/PageDown keys scroll.
        //
        // Only reached when the game text does NOT have focus: with focus inside an
        // editor window, AvaloniaEdit's own TextArea binding matches the same
        // gesture further down the bubble route and opens the panel itself. Same
        // destination, so the two paths agree — this one just extends it to the
        // normal case where focus is sitting in the command bar.
        if (e.Key == Key.F && e.KeyModifiers == PlatformCommandModifiers &&
            !IsMacroBound(Key.F, e.KeyModifiers) &&
            PageScroll.CurrentTarget?.DataContext is Docking.IFindHost platformFindHost)
        {
            platformFindHost.Find.Open();
            e.Handled = true;
            return;
        }

        // #120: Ctrl+F opens the Find bar on the same selected window. On Windows
        // and Linux this is the platform gesture, already handled above; it stays
        // live on macOS, where Ctrl+F is a separate keystroke from Cmd+F, so the
        // Genie-4-era shortcut keeps working and stays a way to reach the bar even
        // on a window whose renderer prefers its own panel.
        // Checked before the generic macro dispatch but only when no ctrl+f
        // macro exists, so an existing user binding keeps winning.
        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control &&
            ViewModel?.Core?.Commands?.Macros?.Get("ctrl+f") is null &&
            PageScroll.CurrentTarget?.DataContext is Docking.IFindHost findHost)
        {
            findHost.Find.IsOpen = true;
            e.Handled = true;
            return;
        }

        if (ViewModel?.Core?.Commands?.Macros is not { } macros) return;

        var keyString = MacroKeyConverter.ToMacroKey(e.Key, e.KeyModifiers);
        if (keyString is null) return;

        var macro = macros.Get(keyString);
        if (macro is null) return;

        ViewModel.Core.ProcessInput(macro.Action);
        e.Handled = true;
    }

    /// <summary>The platform's primary command modifier — Meta (Cmd) on macOS,
    /// Control everywhere else. Read per use rather than cached, since
    /// <c>Application.Current</c> may not be up when this type is first touched.</summary>
    private static KeyModifiers PlatformCommandModifiers =>
        Application.Current?.PlatformSettings?.HotkeyConfiguration.CommandModifiers
        ?? KeyModifiers.Control;

    /// <summary>True when the user has a macro bound to this keystroke, in which
    /// case a built-in shortcut on the same keys must stand down (#120). Keystrokes
    /// that can't be expressed as a macro at all — Cmd-modified ones, since
    /// <see cref="MacroKeyConverter"/> knows only ctrl/alt/shift — are never
    /// bound.</summary>
    private bool IsMacroBound(Key key, KeyModifiers mods)
        => MacroKeyConverter.ToMacroKey(key, mods) is { } macroKey &&
           ViewModel?.Core?.Commands?.Macros?.Get(macroKey) is not null;

    /// <summary>
    /// Type-anywhere capture (public #141): printable text that reached the
    /// window unhandled (focus was on game text, a panel, a button — anything
    /// that isn't a text editor) is appended to the command bar, which also
    /// takes focus so the rest of the phrase types normally. Control
    /// characters (Enter's \r, Esc) keep their control meaning and are left
    /// alone.
    /// </summary>
    private void OnGlobalTextInput(object? sender, TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text)) return;
        if (e.Text.All(ch => ch < ' ')) return;                    // control chars only
        if (FocusManager?.GetFocusedElement() is TextBox) return;  // already typing somewhere real
        if (CommandInput is not { } bar || !bar.IsEffectivelyVisible) return;

        bar.Focus();
        bar.Text = (bar.Text ?? string.Empty) + e.Text;
        bar.CaretIndex = bar.Text?.Length ?? 0;
        e.Handled = true;
    }

    /// <summary>
    /// Edit → "Paste Multi Line" (Genie 4's SpecialPaste): joins the clipboard's
    /// lines with the command separator char so each line executes as its own
    /// command when sent — ProcessInput splits on the separator. Genie 4
    /// hardcoded ';' and only handled CRLF; here the separator is the user's
    /// configured one and lone-LF clipboards (copied from macOS/Linux, or a
    /// browser) split correctly too. Blank lines are dropped rather than sent
    /// as empty commands.
    /// </summary>
    private async void OnPasteMultiLine(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is not { } cb) return;
        var text = await cb.TryGetTextAsync();
        if (string.IsNullOrWhiteSpace(text)) return;

        var separator = ViewModel?.Core?.Config.SeparatorChar ?? ';';
        var lines = text.Split('\n')
                        .Select(l => l.Trim())
                        .Where(l => l.Length > 0);
        AppendToCommandBar(string.Join(separator, lines));
    }

    private void AppendToCommandBar(string selection)
    {
        if (ViewModel?.Command is not { } cmd) return;

        var existing = cmd.CommandText ?? string.Empty;
        var separator = existing.Length > 0 && !char.IsWhiteSpace(existing[^1]) ? " " : "";
        cmd.CommandText = existing + separator + selection;

        // Focus the command bar so the user can edit or press Enter without
        // an extra click. The TextBox has no x:Name in App.axaml, so look it
        // up by walking children — there's only one in the bottom command
        // grid so the first hit is correct.
        var input = this.GetVisualDescendants()
                        .OfType<TextBox>()
                        .FirstOrDefault(tb => tb.Watermark == "Enter command...");
        if (input is not null)
        {
            input.Focus();
            input.CaretIndex = cmd.CommandText.Length;
        }
    }
}
