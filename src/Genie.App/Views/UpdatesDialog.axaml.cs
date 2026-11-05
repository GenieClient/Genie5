using Avalonia.Interactivity;
using Avalonia.ReactiveUI;
using Genie.App.ViewModels;
using ReactiveUI;

namespace Genie.App.Views;

/// <summary>
/// Updates dialog code-behind. The VM is owned by the caller; we just bridge
/// the <see cref="UpdatesDialogViewModel.ShowAddSourceDialog"/> interaction
/// to the <see cref="AddSourceDialog"/> modal so the VM stays free of
/// Avalonia types.
/// </summary>
public partial class UpdatesDialog : ReactiveWindow<UpdatesDialogViewModel>
{
    public UpdatesDialog()
    {
        InitializeComponent();
        this.WhenActivated(d =>
        {
            d(ViewModel!.ShowAddSourceDialog.RegisterHandler(async ctx =>
            {
                var entry = await AddSourceDialog.ShowAsync(this, ctx.Input);
                ctx.SetOutput(entry);
            }));
            d(ViewModel!.ShowScriptFilesDialog.RegisterHandler(async ctx =>
                ctx.SetOutput(await ScriptFilesDialog.ShowAsync(this, ctx.Input))));
        });
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
