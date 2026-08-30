using System.Collections.ObjectModel;
using Avalonia.Media;
using Dock.Model.Mvvm.Controls;
using Genie.App.Controls;
using Genie.App.ViewModels;
using Genie.Core.Layout;

namespace Genie.App.Docking;

/// <summary>
/// Raw XML dock tool (issue #14). A read-only live view of the raw server XML
/// stream — capped rolling buffer, auto-scroll, default hidden. Re-opens via
/// Window → Raw XML. A dev/debug panel, grouped beside the other utility tabs
/// (Scripts / Scene) in the default layout.
/// </summary>
public class RawXmlTool : ActivityTool, IWindowMenuHost, ITextEditorHost
{
    public RawXmlViewModel ViewModel { get; }

    /// <summary>Right-click window menu (Clear / Close), built by
    /// <see cref="GenieDockFactory"/>.</summary>
    public WindowMenuModel? WindowMenu { get; set; }

    // Per-window font, resolved from this panel's WindowSettings the same way
    // StreamTool does, so the Layout-tab font change reaches the Raw XML dump
    // instead of being ignored (it used to be hardcoded in the template).
    // Foreground stays the panel's distinctive green — only the font is tunable.
    private FontFamily _toolFontFamily = new("Cascadia Mono,Consolas,Courier New,monospace");
    public  FontFamily ToolFontFamily { get => _toolFontFamily; private set => SetProperty(ref _toolFontFamily, value); }

    private double     _toolFontSize = 11;
    public  double     ToolFontSize { get => _toolFontSize; private set => SetProperty(ref _toolFontSize, value); }

    // ── ITextEditorHost (consumed only when useeditorrawxmlwindow is on) ────────
    // Raw XML stays exactly as minimal under the editor renderer as it is under
    // the legacy one: a fixed colour (not a resolvable per-window
    // ToolForeground like Game/Stream get), no word-wrap toggle (NoWrap +
    // horizontal scroll, matching the legacy "long tags stay on one line"
    // behavior), no Find, no highlighting/links.
    private static readonly IBrush RawXmlForeground = new SolidColorBrush(Color.Parse("#7fc4a0"));

    public ObservableCollection<TextLine> Lines           => ViewModel.Lines;
    public IBrush?                        ToolForeground   => RawXmlForeground;
    public TextWrapping                   ToolTextWrapping => TextWrapping.NoWrap;
    public Avalonia.Controls.Primitives.ScrollBarVisibility ToolHScroll
        => Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;

    private bool _isScrollPaused;
    public  bool IsScrollPaused { get => _isScrollPaused; set => SetProperty(ref _isScrollPaused, value); }

    public FindInWindowModel? Find             => null;
    public bool                EnableColorizing => false;
    public bool                EnableLinks      => false;

    public RawXmlTool(RawXmlViewModel vm, WindowSettings? settings = null)
    {
        ViewModel = vm;
        Id        = "raw-xml";
        Title     = "Raw XML";

        if (settings is not null)
        {
            ApplySettings(settings);
            settings.Changed += () => ApplySettings(settings);
        }

        ActivitySettings = settings;

        // Unread-activity flash. NB: while connected this stream is near-
        // constant, so a backgrounded Raw XML tab blinks most of the time —
        // consistent with "data arrived while hidden", and it is a debug
        // panel that is rarely stacked behind anything.
        WireActivity(vm.Lines);
    }

    private void ApplySettings(WindowSettings s)
    {
        Title          = string.IsNullOrEmpty(s.DisplayTitle) ? s.DefaultTitle : s.DisplayTitle;
        ToolFontFamily = WindowSettingsResolver.ResolveFontFamily(s.FontFamily);
        ToolFontSize   = WindowSettingsResolver.ResolveFontSize(s.FontSize);
    }
}

/// <summary>
/// The Raw XML window rendered by <see cref="Controls.GameTextEditor"/>
/// (AvaloniaEdit) instead of the per-line <c>ItemsControl</c>. Created by
/// <see cref="GenieDockFactory"/> in place of a plain <see cref="RawXmlTool"/>
/// when <c>GenieConfig.UseEditorRawXmlWindow</c> is on. Experimental; default
/// off. Same type-based renderer selection as
/// <see cref="EditorGameTextDocument"/> — see that type's doc comment for why.
/// </summary>
public sealed class EditorRawXmlTool : RawXmlTool
{
    public EditorRawXmlTool(RawXmlViewModel vm, WindowSettings? settings = null)
        : base(vm, settings) { }
}
