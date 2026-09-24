using System;
using Avalonia.Media;
using Dock.Model.Mvvm.Controls;
using Genie.App.Controls;
using Genie.App.ViewModels;
using ReactiveUI;

namespace Genie.App.Docking;

/// <summary>
/// Dock panel for one server-driven dialog (#156 Phase 1). One instance per
/// dialog id; the <see cref="ServerDialogViewModel"/> carries the controls and
/// the title DR gave it.
///
/// <para>The dock <c>Id</c> is the canonical <c>serverdlg:&lt;id&gt;</c> key
/// (see <see cref="GenieDockFactory.ServerDialogId"/>), so the panel round-trips
/// through saved layouts and the Window menu exactly like a built-in tool.</para>
///
/// <para>Proportional rather than monospaced, unlike the plugin windows: these
/// render real controls with captions, not column-aligned text.</para>
/// </summary>
public class ServerDialogTool : ActivityTool, IWindowMenuHost
{
    public ServerDialogViewModel ViewModel { get; }

    /// <summary>Right-click window menu (Close), built by <see cref="GenieDockFactory"/>.</summary>
    public WindowMenuModel? WindowMenu { get; set; }

    // Per-window font (#156 Phase 3, via the #233 plumbing). Seeded from the
    // proportional UI font the factory registers dialogs with, so an untouched
    // dialog looks exactly as it did before it had settings.
    private FontFamily _dialogFontFamily = FontFamily.Default;
    public  FontFamily DialogFontFamily { get => _dialogFontFamily; private set => SetProperty(ref _dialogFontFamily, value); }
    private double     _dialogFontSize = 14;
    public  double     DialogFontSize { get => _dialogFontSize; private set => SetProperty(ref _dialogFontSize, value); }

    public ServerDialogTool(ServerDialogViewModel vm, string id, string title,
                            Genie.Core.Layout.WindowSettings? settings = null)
    {
        ViewModel = vm;
        Id        = id;
        Title     = string.IsNullOrWhiteSpace(title) ? vm.Title : title;

        // DR renames its own dialogs (the per-character injuries window is
        // titled for whoever it describes), so follow the VM.
        vm.WhenAnyValue(x => x.Title)
          .Subscribe(t => { if (!string.IsNullOrWhiteSpace(t)) Title = t; });

        if (settings is not null)
        {
            ApplyFonts(settings);
            settings.Changed += () => ApplyFonts(settings);
        }

        ActivitySettings = settings;

        // Unread-activity flash when a delta lands on a tab sitting behind
        // another one — the server changing a dialog is worth noticing.
        WireActivity(vm.Controls);
    }

    private void ApplyFonts(Genie.Core.Layout.WindowSettings s)
    {
        DialogFontFamily = WindowSettingsResolver.ResolveFontFamily(s.FontFamily);
        DialogFontSize   = WindowSettingsResolver.ResolveFontSize(s.FontSize);
    }
}
