using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media;
using Dock.Model.Mvvm.Controls;
using Genie.App.Controls;
using Genie.App.ViewModels;
using Genie.Core.Layout;

namespace Genie.App.Docking;

public class StreamTool : Tool, IWindowMenuHost, IFindHost, ITextEditorHost
{
    public StreamBuffer Buffer { get; }

    public ObservableCollection<TextLine> Lines => Buffer.Lines;
    public bool EnableColorizing => true;
    public bool EnableLinks      => true;

    /// <summary>In-window Find bar state (#120).</summary>
    public FindInWindowModel Find { get; }

    /// <summary>Right-click window menu (Clear / Time Stamp / Name List Only /
    /// Close), built by <see cref="GenieDockFactory"/>.</summary>
    public WindowMenuModel? WindowMenu { get; set; }

    // Per-window appearance overrides. SetProperty-backed (Dock.Model.Mvvm base
    // is a CommunityToolkit ObservableObject) instead of Fody [Reactive].
    private IBrush?    _toolForeground;
    public  IBrush?    ToolForeground { get => _toolForeground; private set => SetProperty(ref _toolForeground, value); }

    private IBrush?    _toolBackground;
    public  IBrush?    ToolBackground { get => _toolBackground; private set => SetProperty(ref _toolBackground, value); }

    private FontFamily _toolFontFamily = new("Cascadia Mono,Consolas,Courier New,monospace");
    public  FontFamily ToolFontFamily { get => _toolFontFamily; private set => SetProperty(ref _toolFontFamily, value); }

    private double     _toolFontSize = 11;
    public  double     ToolFontSize { get => _toolFontSize; private set => SetProperty(ref _toolFontSize, value); }

    // Word Wrap (#120) — see GameTextDocument for the semantics.
    private TextWrapping _toolTextWrapping = TextWrapping.Wrap;
    public  TextWrapping ToolTextWrapping { get => _toolTextWrapping; private set => SetProperty(ref _toolTextWrapping, value); }

    private Avalonia.Controls.Primitives.ScrollBarVisibility _toolHScroll
        = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
    public  Avalonia.Controls.Primitives.ScrollBarVisibility ToolHScroll
    { get => _toolHScroll; private set => SetProperty(ref _toolHScroll, value); }

    /// <summary>"Pause Scrolling" window-menu state. Bound to the stream
    /// ScrollViewer's <c>AutoScrollBehavior.Paused</c>; the window menu toggles
    /// it. Transient (not persisted).</summary>
    private bool       _isScrollPaused;
    public  bool       IsScrollPaused { get => _isScrollPaused; set => SetProperty(ref _isScrollPaused, value); }

    public StreamTool(StreamBuffer buffer, WindowSettings? settings = null)
    {
        Buffer = buffer;
        Find   = new FindInWindowModel(() => buffer.Lines.Select(l => l.Text).ToArray());
        // Plain lowercased buffer name (e.g. "logons", "talk") matches the
        // Window-menu toggle command ids and the WindowSettingsStore keys.
        Id     = buffer.Name.ToLowerInvariant();
        Title  = buffer.Name;

        if (settings is not null)
        {
            // #90: hand the buffer its live settings so StreamBuffer.Add can
            // prepend a timestamp when the Layout-tab toggle is on.
            Buffer.Settings = settings;
            ApplySettings(settings);
            settings.Changed += () => ApplySettings(settings);
        }
    }

    private void ApplySettings(WindowSettings s)
    {
        Title          = string.IsNullOrEmpty(s.DisplayTitle) ? s.DefaultTitle : s.DisplayTitle;
        // Resolve per-window sentinels (empty FontFamily / non-positive
        // FontSize / "Default" Foreground) against the global DisplaySettings
        // values pushed to Application.Resources by DisplaySettings.Apply().
        // Option A: per-window overrides global only when explicitly set.
        ToolFontFamily = WindowSettingsResolver.ResolveFontFamily(s.FontFamily);
        ToolFontSize   = WindowSettingsResolver.ResolveFontSize(s.FontSize);
        ToolForeground = WindowSettingsResolver.ResolveForeground(s.Foreground);
        ToolBackground = WindowSettingsResolver.ResolveBackground(s.Background);
        ToolTextWrapping = s.WordWrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        ToolHScroll      = s.WordWrap
            ? Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
            : Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
    }
}

/// <summary>
/// A Stream window (Logons, Talk, Whispers, ...) rendered by
/// <see cref="Controls.GameTextEditor"/> (AvaloniaEdit) instead of the
/// per-line <c>ItemsControl</c>. Created by <see cref="GenieDockFactory"/> in
/// place of a plain <see cref="StreamTool"/> when
/// <c>GenieConfig.UseEditorStreamWindow</c> is on — one flag governs every
/// Stream instance, since they all share this one class. Experimental;
/// default off. Same type-based renderer selection as
/// <see cref="EditorGameTextDocument"/> — see that type's doc comment for why.
/// </summary>
public sealed class EditorStreamTool : StreamTool
{
    public EditorStreamTool(StreamBuffer buffer, WindowSettings? settings = null)
        : base(buffer, settings) { }
}
