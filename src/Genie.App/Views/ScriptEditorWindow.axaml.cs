using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using AvaloniaEdit.Search;
using Genie.App.Diagnostics;
using Genie.App.ScriptEditing;

namespace Genie.App.Views;

/// <summary>
/// The built-in script editor (public #243): one non-modal window per open
/// script, opened by <see cref="ScriptEditorService"/> from <c>#edit</c>, the
/// Script Manager and the Script Bar pencil.
/// <list type="bullet">
///   <item>Ctrl+S saves through <see cref="ScriptTextFile.Save"/> (atomic, same
///   encoding and line endings as the file had).</item>
///   <item>Ctrl+F opens AvaloniaEdit's own search panel.</item>
///   <item>The title carries a <c>*</c> while there are unsaved edits, and closing
///   then asks Save / Don't Save / Cancel.</item>
///   <item>A change made to the file by another program — seen by a watcher, and
///   re-checked whenever the window gets focus — raises a banner offering a
///   reload; nothing is replaced without the user's say-so.</item>
/// </list>
/// </summary>
public partial class ScriptEditorWindow : Window
{
    private readonly ScriptTextFile? _file;
    private readonly SearchPanel? _search;
    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _diskCheckDebounce;
    private bool _closeConfirmed;
    private bool? _paletteDark;

    /// <summary>Designer / XAML loader constructor.</summary>
    public ScriptEditorWindow()
    {
        InitializeComponent();
    }

    public ScriptEditorWindow(ScriptTextFile file, string text) : this()
    {
        _file = file;
        Editor.Document.Text = text;
        MarkLoaded();
        Editor.Document.UndoStack.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AvaloniaEdit.Document.UndoStack.IsOriginalFile)) UpdateTitle();
        };
        Editor.TextArea.Caret.PositionChanged += (_, _) => UpdateStatus();

        _search = SearchPanel.Install(Editor);

        // Ctrl+S / Ctrl+F on the tunnel, so they work wherever focus sits in the
        // window (toolbar, banner) and before the text area can eat the key.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

        // Palette follows the theme: re-pick when the editor's background brush
        // (a theme resource) or the theme variant changes.
        Editor.PropertyChanged += (_, e) =>
        {
            if (e.Property == BackgroundProperty) ApplyHighlighting();
        };
        ActualThemeVariantChanged += (_, _) => ApplyHighlighting();
        Activated += (_, _) => CheckDiskNow();

        PathText.Text = file.Path;
        ApplyHighlighting();
        UpdateTitle();
        UpdateStatus();
        StartWatching();
    }

    /// <summary>Absolute path of the file this window edits.</summary>
    public string FilePath => _file?.Path ?? string.Empty;

    /// <summary>The on-disk side of this window.</summary>
    public ScriptTextFile? File => _file;

    /// <summary>True while the text differs from what was last loaded or saved
    /// (undoing back to the saved text clears it).</summary>
    public bool IsDirty => !Editor.Document.UndoStack.IsOriginalFile;

    /// <summary>True while the changed-on-disk banner is up.</summary>
    public bool IsExternalChangeShown => ChangedBanner.IsVisible;

    /// <summary>The editor control (tests, and the service's focus handling).</summary>
    public AvaloniaEdit.TextEditor TextEditor => Editor;

    /// <summary>The search panel installed on the editor.</summary>
    public SearchPanel? Search => _search;

    /// <summary>Asks what to do with unsaved edits on close: true = save, false =
    /// discard, null = cancel the close. Defaults to the Save / Don't Save /
    /// Cancel dialog; tests swap it.</summary>
    public Func<ScriptEditorWindow, Task<bool?>> ConfirmClose { get; set; } = ShowSavePromptAsync;

    /// <summary>Raised after a successful save, with a note for the host to echo
    /// (e.g. an encoding fallback), or null.</summary>
    public event Action<ScriptEditorWindow, string?>? Saved;

    // ── save / reload ────────────────────────────────────────────────────────

    /// <summary>Write the text back to disk. Returns false (and says why in the
    /// status strip) when the write failed; the edits stay in the window.</summary>
    public bool Save()
    {
        if (_file is null) return false;
        try
        {
            _file.Save(Editor.Document.Text);
        }
        catch (Exception ex)
        {
            ErrorLog.Log("ScriptEditor.Save", ex);
            StatusText.Text = $"Save failed: {ex.Message}";
            return false;
        }
        Editor.Document.UndoStack.MarkAsOriginalFile();
        HideBanner();
        UpdateTitle();
        UpdateStatus();
        string? note = _file.EncodingChangedOnLastSave
            ? $"{Path.GetFileName(_file.Path)} had characters its old encoding can't hold, so it was saved as UTF-8."
            : null;
        Saved?.Invoke(this, note);
        return true;
    }

    /// <summary>Replace the text with the file on disk (drops unsaved edits).</summary>
    public void ReloadFromDisk()
    {
        if (_file is null) return;
        string text;
        try
        {
            text = _file.Reload();
        }
        catch (Exception ex)
        {
            ErrorLog.Log("ScriptEditor.Reload", ex);
            StatusText.Text = $"Reload failed: {ex.Message}";
            return;
        }
        var caret = Editor.CaretOffset;
        Editor.Document.Text = text;
        MarkLoaded();
        Editor.CaretOffset = Math.Min(caret, Editor.Document.TextLength);
        HideBanner();
        UpdateTitle();
        UpdateStatus();
    }

    /// <summary>Compare the file on disk with what we last read or wrote, and
    /// raise the banner if another program changed or removed it.</summary>
    public void CheckDiskNow()
    {
        if (_file is null || IsExternalChangeShown) return;
        if (!_file.HasChangedOnDisk()) return;

        var name = Path.GetFileName(_file.Path);
        if (_file.IsMissingOnDisk)
        {
            BannerText.Text = $"{name} was deleted or moved outside Genie. Save writes it back.";
            BannerReloadButton.IsVisible = false;
        }
        else
        {
            BannerText.Text = IsDirty
                ? $"{name} was changed by another program. Reload to use that version (your unsaved edits here are discarded), or keep yours."
                : $"{name} was changed by another program. Reload to see the new version.";
            BannerReloadButton.IsVisible = true;
        }
        ChangedBanner.IsVisible = true;
    }

    /// <summary>Keep this window's text; the disk version is noted so the same
    /// change doesn't raise the banner again.</summary>
    public void KeepMine()
    {
        _file?.AcknowledgeDiskState();
        HideBanner();
    }

    /// <summary>Close without the unsaved-changes prompt (the host has already
    /// asked, e.g. when the main window closes).</summary>
    public void ForceClose()
    {
        _closeConfirmed = true;
        Close();
    }

    /// <summary>Open the search panel (Ctrl+F).</summary>
    public void OpenSearch()
    {
        if (_search is null) return;
        _search.Open();
        Dispatcher.UIThread.Post(() => _search.Reactivate(), DispatcherPriority.Input);
    }

    // ── window lifecycle ─────────────────────────────────────────────────────

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || _closeConfirmed || !IsDirty) return;
        e.Cancel = true;
        PromptAndMaybeClose();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _diskCheckDebounce?.Stop();
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
    }

    // async void: event-driven UI glue nothing awaits (same shape as MainWindow's
    // close confirmation).
    private async void PromptAndMaybeClose()
    {
        bool? answer;
        try { answer = await ConfirmClose(this); }
        catch (Exception ex)
        {
            ErrorLog.Log("ScriptEditor.ConfirmClose", ex);
            return;
        }
        if (answer is null) return;               // Cancel: stay open
        if (answer == true && !Save()) return;    // save failed: stay open
        _closeConfirmed = true;
        Close();
    }

    private static async Task<bool?> ShowSavePromptAsync(ScriptEditorWindow owner)
    {
        var dlg = new ConfirmDialog(
            "Unsaved changes",
            $"Save the changes to {Path.GetFileName(owner.FilePath)} before closing?",
            "_Save", "Do_n't Save", "_Cancel");
        return await dlg.ShowDialog<bool?>(owner);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private void StartWatching()
    {
        if (_file is null) return;
        var dir = Path.GetDirectoryName(_file.Path);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        try
        {
            _watcher = new FileSystemWatcher(dir, Path.GetFileName(_file.Path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            void Poke(object? s, FileSystemEventArgs e) => Dispatcher.UIThread.Post(ScheduleDiskCheck);
            _watcher.Changed += Poke;
            _watcher.Created += Poke;
            _watcher.Deleted += Poke;
            _watcher.Renamed += (s, e) => Dispatcher.UIThread.Post(ScheduleDiskCheck);
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            // Exotic mounts / permissions: the focus check still catches changes.
            ErrorLog.Log("ScriptEditor.Watch", ex);
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    private void ScheduleDiskCheck()
    {
        // Editors often write in several steps (truncate, write, rename); wait
        // for the burst to settle before comparing.
        _diskCheckDebounce ??= new DispatcherTimer(TimeSpan.FromMilliseconds(300),
            DispatcherPriority.Background, (_, _) =>
            {
                _diskCheckDebounce!.Stop();
                CheckDiskNow();
            });
        _diskCheckDebounce.Stop();
        _diskCheckDebounce.Start();
    }

    private void HideBanner() => ChangedBanner.IsVisible = false;

    /// <summary>The text now matches the file: drop the undo history and make
    /// this the clean point. ClearAll alone keeps the old "original" marker, so
    /// the freshly loaded text would read as dirty.</summary>
    private void MarkLoaded()
    {
        Editor.Document.UndoStack.ClearAll();
        Editor.Document.UndoStack.MarkAsOriginalFile();
    }

    private void UpdateTitle()
    {
        var name = _file is null ? "Script Editor" : Path.GetFileName(_file.Path);
        Title = $"{name}{(IsDirty ? "*" : "")} — Script Editor";
    }

    private void UpdateStatus()
    {
        if (_file is null) return;
        var caret = Editor.TextArea.Caret;
        StatusText.Text = $"Ln {caret.Line}, Col {caret.Column}    {_file.EncodingLabel}    {_file.LineEndingLabel}";
    }

    private void ApplyHighlighting()
    {
        if (_file is null) return;
        var dark = IsDarkBackground();
        if (_paletteDark == dark && Editor.SyntaxHighlighting is not null) return;
        _paletteDark = dark;
        Editor.SyntaxHighlighting = ScriptSyntax.ForFile(_file.Path, dark);
    }

    /// <summary>Whether the editor paints on a dark background: the theme's own
    /// panel colour when it has one (custom themes can pair any palette with
    /// either base variant), else the theme variant.</summary>
    internal bool IsDarkBackground()
    {
        if (Editor.Background is ISolidColorBrush { Color.A: > 0 } b)
        {
            var c = b.Color;
            var luminance = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
            return luminance < 0.5;
        }
        return ActualThemeVariant != ThemeVariant.Light;
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        var command = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (!command || e.KeyModifiers.HasFlag(KeyModifiers.Alt)) return;
        switch (e.Key)
        {
            case Key.S:
                Save();
                e.Handled = true;
                break;
            case Key.F:
                OpenSearch();
                e.Handled = true;
                break;
        }
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e) => Save();
    private void OnFindClick(object? sender, RoutedEventArgs e) => OpenSearch();

    private async void OnReloadClick(object? sender, RoutedEventArgs e)
    {
        if (IsDirty)
        {
            var ok = await new ConfirmDialog("Reload script",
                $"Discard your unsaved edits to {Path.GetFileName(FilePath)} and read it from disk again?")
                .ShowDialog<bool>(this);
            if (!ok) return;
        }
        ReloadFromDisk();
    }

    private void OnBannerReloadClick(object? sender, RoutedEventArgs e) => ReloadFromDisk();
    private void OnBannerKeepClick(object? sender, RoutedEventArgs e) => KeepMine();
}
