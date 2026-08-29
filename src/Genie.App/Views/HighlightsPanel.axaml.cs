using Avalonia.Controls;
using Genie.Core.Config;
using Genie.Core.Highlights;
using Genie.Core.Presets;

namespace Genie.App.Views;

/// <summary>
/// Parent control for the Highlights tab — hosts Strings / Names / Presets
/// as nested TabItems. Each sub-panel is wired up by the owning dialog via
/// <see cref="Initialize"/>.
/// </summary>
public partial class HighlightsPanel : UserControl
{
    public HighlightsPanel() => InitializeComponent();

    public void Initialize(
        HighlightEngine      highlights,
        NameHighlightEngine  names,
        PresetEngine         presets,
        Action?              onHighlightsChanged = null,
        Action?              onNamesChanged      = null,
        Action?              onPresetsChanged    = null,
        GenieConfig?         config              = null,
        Action?              onConfigChanged     = null,
        ScopeEditingContext? highlightsScope     = null,
        ScopeEditingContext? namesScope          = null,
        ScopeEditingContext? presetsScope        = null)
    {
        StringsPanelCtrl.Initialize(highlights, onHighlightsChanged, highlightsScope);
        NamesPanelCtrl  .Initialize(names,      onNamesChanged,      namesScope);
        PresetsPanelCtrl.Initialize(presets,    onPresetsChanged,    config, onConfigChanged, presetsScope);
    }
}
