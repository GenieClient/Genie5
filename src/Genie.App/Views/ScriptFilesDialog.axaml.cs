using Avalonia.Controls;
using Avalonia.Interactivity;
using Genie.App.ViewModels;

namespace Genie.App.Views;

/// <summary>
/// Per-file chooser for a Scripts source: tick which differing files to update
/// now and which to ignore permanently. Returns null on cancel.
/// </summary>
public partial class ScriptFilesDialog : Window
{
    private ScriptFilesResult? _result;

    public ScriptFilesDialog()
    {
        InitializeComponent();
    }

    public static async Task<ScriptFilesResult?> ShowAsync(Window owner, ScriptFilesRequest request)
    {
        var dlg = new ScriptFilesDialog { DataContext = new ScriptFilesViewModel(request) };
        await dlg.ShowDialog(owner);
        return dlg._result;
    }

    private ScriptFilesViewModel Vm => (ScriptFilesViewModel)DataContext!;

    private void OnSelectAll(object? sender, RoutedEventArgs e)  => Vm.SetAllUpdate(true);
    private void OnSelectNone(object? sender, RoutedEventArgs e) => Vm.SetAllUpdate(false);

    private void OnSaveIgnores(object? sender, RoutedEventArgs e)
    {
        var r   = Vm.BuildResult();
        _result = new ScriptFilesResult(r.Ignored, Array.Empty<string>());
        Close();
    }

    private void OnUpdateSelected(object? sender, RoutedEventArgs e)
    {
        _result = Vm.BuildResult();
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        _result = null;
        Close();
    }
}
