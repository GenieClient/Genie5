using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.ReactiveUI;
using Avalonia.VisualTree;
using Genie.App.Controls;
using Genie.App.ViewModels;
using ReactiveUI;

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

        // Keyboard macros — F-keys + modifier+letter/digit. Bubble phase
        // (the default) so any inner control that wants to capture the
        // keystroke first (e.g. the MacrosPanel KeyBox capture mode) can
        // mark the event handled and skip the macro fire entirely.
        AddHandler(InputElement.KeyDownEvent, OnGlobalKeyDown,
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
                        ViewModel.LastConnectionConfig)
                };
                var result = await dlg.ShowDialog<ConnectResult?>(this);
                ctx.SetOutput(result);
            }));

            d(ViewModel!.ShowDisplaySettingsDialog.RegisterHandler(async ctx =>
            {
                var dlg = new DisplaySettingsDialog
                {
                    DataContext = new DisplaySettingsViewModel(ctx.Input)
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

            // Command-line auto-connect (--host/--port/--profile). Fired here so
            // the dialog interaction handlers above are already registered (the
            // connect path itself doesn't need them, but anything it triggers
            // might). The VM guards against running more than once, so repeated
            // activations are harmless.
            _ = ViewModel!.RunStartupConnectAsync();
        });
    }

    /// <summary>
    /// Pull truth from the dock factory just before the Window menu renders so
    /// each check mark reflects the dock's actual state — guards against any
    /// close/move path that didn't trigger a sync event.
    /// </summary>
    private void OnWindowMenuOpened(object? sender, RoutedEventArgs e)
        => ViewModel?.RefreshVisibilityBools();

    // Layout menu uses SubmenuOpened (not Command on the parent MenuItem,
    // which doesn't fire when the click opens a submenu) so the Load Layout ▶
    // child sees a freshly-populated SavedLayouts collection. Without this,
    // SavedLayouts stays empty and Avalonia renders the Load Layout item as
    // disabled because its ItemsSource has nothing in it.
    private void OnLayoutMenuOpened(object? sender, RoutedEventArgs e)
        => ViewModel?.RefreshLayoutListCommand.Execute().Subscribe();

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
    /// Translates the keystroke into a Genie 4 macro key string and fires
    /// the matching macro action (if any) through the command pipeline.
    /// </summary>
    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        // Esc cancels any in-flight auto-walk — per DR policy compliance
        // review, the user needs an obvious always-available kill switch
        // for any automated traversal. Hits before macros so an Esc-bound
        // macro can't accidentally swallow the cancel.
        if (e.Key == Key.Escape
            && ViewModel?.Mapper?.AutoWalk is { Current: not null } walk)
        {
            walk.Cancel("user pressed Esc");
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
