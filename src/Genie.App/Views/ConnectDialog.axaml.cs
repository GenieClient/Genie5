using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.ReactiveUI;
using Genie.Core.Connection;
using Genie.App.ViewModels;
using ReactiveUI;

namespace Genie.App.Views;

public partial class ConnectDialog : ReactiveWindow<ConnectDialogViewModel>
{
    public ConnectDialog()
    {
        InitializeComponent();
        this.WhenActivated(d =>
        {
            // OK path: surface a Yes/No save prompt before connecting in two
            // cases: (a) the user typed a profile name with no matching saved
            // profile — offer to save it as a new profile; (b) the user picked
            // an existing profile and edited the account or password — offer
            // to update the stored credentials. Bare-credential connects
            // (ProfileName blank) skip the prompt; they aren't intended to
            // persist. Saying No still connects — the prompt is just about
            // whether the change reaches disk.
            d(ViewModel!.OkCommand.Subscribe(result => _ = HandleOkAsync(result)));
            d(ViewModel!.CancelCommand.Subscribe(_ => Close(null)));

            // After Fetch completes, auto-open the Character dropdown so
            // the user sees the populated list immediately instead of
            // having to hunt for it. The SGE handshake returns
            // synchronously enough that the visual transition from "(click
            // Fetch)" placeholder → expanded list feels like one motion.
            //
            // Only pop the dropdown when fetch actually produced characters
            // — opening an empty list after a failed fetch would be jarring
            // (the user just saw the "Failed: ..." status text). Posted
            // through the dispatcher so the binding has flushed the
            // ItemsSource before we set IsDropDownOpen.
            d(ViewModel!.FetchCharactersCommand.Subscribe(_ =>
            {
                if (ViewModel!.AvailableCharacters.Count == 0) return;
                Dispatcher.UIThread.Post(() => CharacterCombo.IsDropDownOpen = true);
            }));

            // Browse → OS folder picker for the optional per-profile data folder.
            ViewModel!.BrowseDataDirRequested += async () => await PickDataFolderAsync();
        });
    }

    private async Task PickDataFolderAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null || ViewModel is null) return;

        var startLocation = !string.IsNullOrWhiteSpace(ViewModel.DataDirectory)
                            && Directory.Exists(ViewModel.DataDirectory)
            ? await top.StorageProvider.TryGetFolderFromPathAsync(ViewModel.DataDirectory)
            : null;

        var picked = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title                  = "Select this profile's data folder",
            SuggestedStartLocation = startLocation,
            AllowMultiple          = false,
        });

        var folder = picked.FirstOrDefault();
        if (folder is null) return;

        ViewModel.SetDataDirectoryFromBrowse(folder.Path.LocalPath);
    }

    private async Task HandleOkAsync(ConnectResult? result)
    {
        if (result is null || ViewModel is null) { Close(result); return; }

        var existing = ViewModel.FindProfileByEnteredName();
        var hasName  = !string.IsNullOrWhiteSpace(ViewModel.ProfileName);

        string? title   = null;
        string? message = null;

        if (existing is null && hasName)
        {
            title   = "Save new profile?";
            message = $"Save these credentials as a new profile named " +
                      $"'{ViewModel.ProfileName.Trim()}'?";
        }
        else if (existing is not null && ViewModel.EnteredCredentialsDifferFromStored())
        {
            title   = "Save profile changes?";
            message = $"The account or password for profile '{existing.Name}' has changed.\n\n" +
                      "Save changes to the profile before connecting?";
        }

        if (title is null) { Close(result); return; }

        // Three options: Yes saves and connects; No connects without saving;
        // Cancel returns to the Connect dialog without closing so the user
        // can keep editing fields.
        var choice = await new ConfirmDialog(
            title, message!,
            yesText:    "_Save profile",
            noText:     "Connect _without saving",
            cancelText: "_Cancel").ShowDialog<bool?>(this);

        if (choice is null) return;                  // Cancel — stay in Connect dialog
        if (choice == true) ViewModel.PersistCurrentEdits();
        // The result was snapshotted at OK-click, BEFORE the save above — a
        // profile it just created (or updated) isn't attached yet. Re-resolve
        // so the session scopes to the right profile (LIVE badge, per-profile
        // config dir, layouts) instead of connecting bare-credential — or,
        // worse, scoped to a stale earlier dropdown pick.
        Close(result with { Profile = ViewModel.ResolveConnectProfile() });
    }
}
