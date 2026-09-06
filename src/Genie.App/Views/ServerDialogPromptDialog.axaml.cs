using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Genie.Core.Dialogs;

namespace Genie.App.Views;

/// <summary>
/// The first-seen chooser for a server dialog (#156 Phase 1). Returns the mode
/// the user picked, or <see cref="ServerDialogMode.AskLater"/> on Esc or the
/// close box — the safe answer, since it decides nothing permanent.
///
/// <para><see cref="ServerDialogMode.ExistingWindow"/> is deliberately absent:
/// it needs a picker of existing windows, which belongs with the Server Dialogs
/// settings grid. The mode works today by hand-editing
/// <c>dialogmappings.json</c>.</para>
/// </summary>
public partial class ServerDialogPromptDialog : Window
{
    /// <summary>Whether the window should open on its own when DR sends it.</summary>
    public bool AutoOpen => AutoOpenCheck.IsChecked == true;

    public ServerDialogPromptDialog()
    {
        InitializeComponent();
        LaterButton.IsCancel = true;   // Esc / close-box decides nothing
    }

    public ServerDialogPromptDialog(ServerDialogState state) : this()
    {
        var name = string.IsNullOrWhiteSpace(state.Title) ? state.Id : state.Title!;
        HeadingText.Text = name;

        var where = string.IsNullOrWhiteSpace(state.Location) ? "unspecified" : state.Location!;
        DetailText.Text = $"Dialog id: {state.Id}   ·   DR suggests: {where}";
        ContentsText.Text = Describe(state.Controls);
    }

    /// <summary>A plain-language census, so the choice is not made blind — the
    /// window has not been rendered yet at this point.</summary>
    private static string Describe(IReadOnlyList<Genie.Core.Events.DialogControl> controls)
    {
        if (controls.Count == 0) return "Contents: nothing yet.";

        var counts = controls
            .GroupBy(c => c.Type)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Count()} {Friendly(g.Key, g.Count())}");

        return "Contains: " + string.Join(", ", counts) + ".";
    }

    private static string Friendly(Genie.Core.Events.DialogControlType type, int count)
    {
        var word = type switch
        {
            Genie.Core.Events.DialogControlType.Label        => "label",
            Genie.Core.Events.DialogControlType.CmdButton    => "button",
            Genie.Core.Events.DialogControlType.CloseButton  => "button",
            Genie.Core.Events.DialogControlType.CheckBox     => "checkbox",
            Genie.Core.Events.DialogControlType.Radio        => "option",
            Genie.Core.Events.DialogControlType.StreamBox    => "text panel",
            Genie.Core.Events.DialogControlType.DropDownBox  => "dropdown",
            Genie.Core.Events.DialogControlType.EditBox      => "text field",
            Genie.Core.Events.DialogControlType.UpDownEditBox => "number field",
            Genie.Core.Events.DialogControlType.ProgressBar  => "bar",
            Genie.Core.Events.DialogControlType.Link         => "link",
            _                                                => "item",
        };
        return count == 1 ? word : word + "s";
    }

    private void OnNewWindow      (object? sender, RoutedEventArgs e) => Close(ServerDialogMode.NewWindow);
    private void OnWhereDrProposes(object? sender, RoutedEventArgs e) => Close(ServerDialogMode.WhereDrProposes);
    private void OnIgnore         (object? sender, RoutedEventArgs e) => Close(ServerDialogMode.Ignore);
    private void OnAskLater       (object? sender, RoutedEventArgs e) => Close(ServerDialogMode.AskLater);
}
