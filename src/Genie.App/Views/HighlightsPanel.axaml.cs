using Avalonia.Controls;
using Genie.Core.Highlights;

namespace Genie.App.Views;

/// <summary>
/// Parent control for the Highlights tab — hosts Strings / Names as nested
/// TabItems. Presets are a top-level tab of their own (public #304). Each
/// sub-panel is wired up by the owning dialog via <see cref="Initialize"/>.
/// </summary>
public partial class HighlightsPanel : UserControl
{
    public HighlightsPanel() => InitializeComponent();

    public void Initialize(
        HighlightEngine      highlights,
        NameHighlightEngine  names,
        Action?              onHighlightsChanged = null,
        Action?              onNamesChanged      = null,
        ScopeEditingContext? highlightsScope     = null,
        ScopeEditingContext? namesScope          = null)
    {
        StringsPanelCtrl.Initialize(highlights, onHighlightsChanged, highlightsScope);
        NamesPanelCtrl  .Initialize(names,      onNamesChanged,      namesScope);
    }
}
