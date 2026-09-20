using Avalonia.Controls;

namespace Genie.App.Views;

/// <summary>
/// The Script Bar — one chip per running script, with per-script
/// pause / debug / edit / stop controls (Genie 4's script toolbar).
/// <para>
/// Purely a markup container: every binding resolves against the inherited
/// <see cref="ViewModels.MainWindowViewModel"/>, and the per-chip commands
/// live on <see cref="ViewModels.ScriptBarViewModel"/>. It exists as its own
/// control so the main window can host it in both the Top and Bottom dock
/// slots and let the user pick between them (#357).
/// </para>
/// </summary>
public partial class ScriptBarView : UserControl
{
    public ScriptBarView() => InitializeComponent();
}
