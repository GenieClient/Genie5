using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Genie.App.Docking;
using Genie.App.ViewModels;

namespace Genie.App.Controls;

/// <summary>
/// Contract the AvaloniaEdit-backed <see cref="GameTextEditor"/> control needs
/// from whatever dock tool or document hosts it. Game
/// (<see cref="Docking.GameTextDocument"/>), Stream
/// (<see cref="Docking.StreamTool"/>), and Raw XML
/// (<see cref="Docking.RawXmlTool"/>) all implement this instead of the
/// control depending on any one concrete host type.
/// </summary>
public interface ITextEditorHost : INotifyPropertyChanged
{
    /// <summary>The buffered lines to render — one document line per entry,
    /// the same source of truth the legacy ItemsControl renderer reads.</summary>
    ObservableCollection<TextLine> Lines { get; }

    FontFamily ToolFontFamily { get; }
    double     ToolFontSize   { get; }

    /// <summary>Null means "inherit the global game colour" (Game, Stream);
    /// hosts with a fixed colour (Raw XML) always return a brush.</summary>
    IBrush? ToolForeground { get; }

    TextWrapping        ToolTextWrapping { get; }
    ScrollBarVisibility  ToolHScroll      { get; }

    /// <summary>"Pause Scrolling" window-menu state. Settable so the control
    /// can resume it on toggle.</summary>
    bool IsScrollPaused { get; set; }

    /// <summary>Null disables the in-window Find bar entirely (Raw XML).</summary>
    FindInWindowModel? Find { get; }

    /// <summary>Run highlight-rule colorizing (<see cref="GameTextColorizer"/>)
    /// over each line. On for Game/Stream; off for Raw XML — a verbatim
    /// protocol dump with no rule matching.</summary>
    bool EnableColorizing { get; }

    /// <summary>Detect and render clickable links (<see cref="GameLinkGenerator"/>).
    /// On for Game/Stream; off for Raw XML.</summary>
    bool EnableLinks { get; }
}
