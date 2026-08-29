using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Genie.Core.Persistence;

namespace Genie.App.Views;

/// <summary>
/// Per-panel handle for the #257 scope-editing UI, built by
/// <see cref="ViewModels.ConfigurationViewModel.ScopeContextFor"/>. When only
/// one config layer exists (profile-less / legacy-global editing) the Scope
/// controls hide and every rule behaves as before.
/// </summary>
public sealed class ScopeEditingContext
{
    /// <summary>Two config layers exist — show the Scope column/field and the
    /// shared-rule delete/toggle semantics.</summary>
    public bool TwoLayers { get; init; }

    /// <summary>Report an explicit delete (or rename-away) of a Global-scoped
    /// rule, keyed by the rule's natural key — without it the next save's
    /// shadowed-twin merge would resurrect the rule in the shared file.</summary>
    public Action<string>? NoteGlobalDelete { get; init; }
}

/// <summary>Shared bits for the per-panel scope UI.</summary>
public static class ScopeEditing
{
    /// <summary>Editor-combo labels, index-aligned with <see cref="FromIndex"/>.
    /// New rules default to index 0 — "This character".</summary>
    public static readonly string[] Labels = ["This character", "All characters"];

    public static int ToIndex(RuleScope scope) => scope == RuleScope.Global ? 1 : 0;

    public static RuleScope FromIndex(int index) =>
        index == 1 ? RuleScope.Global : RuleScope.Character;

    /// <summary>Short grid-column label.</summary>
    public static string RowLabel(RuleScope scope) =>
        scope == RuleScope.Global ? "Global" : "Char";

    /// <summary>Hide/show a named DataGrid column (the Scope column is
    /// informational and meaningless in single-layer editing).</summary>
    public static void SetColumnVisible(DataGrid grid, string header, bool visible)
    {
        foreach (var c in grid.Columns)
            if (c.Header as string == header) { c.IsVisible = visible; return; }
    }
}

public enum ScopeDeleteChoice { Cancel, LocalOptOut, RemoveForAll }

/// <summary>
/// The #257 shared-rule delete prompt: deleting a rule every character uses
/// is destructive beyond this profile, so the user picks between a local
/// opt-out (a disabled this-character copy shadows the shared rule — fully
/// reversible) and removing it for all characters. Rule types without an
/// enabled flag offer only remove-for-all.
/// </summary>
public sealed class ScopeDeleteDialog : Window
{
    private ScopeDeleteDialog(string what, bool allowOptOut)
    {
        Title                 = "Shared rule";
        SizeToContent         = SizeToContent.WidthAndHeight;
        CanResize             = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        MaxWidth              = 520;

        Button Make(string text, ScopeDeleteChoice choice, bool isDefault = false)
        {
            var b = new Button { Content = text, IsDefault = isDefault };
            b.Click += (_, _) => Close(choice);
            return b;
        }

        var buttons = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            Spacing             = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        if (allowOptOut)
            buttons.Children.Add(Make("Disable for this character", ScopeDeleteChoice.LocalOptOut, isDefault: true));
        buttons.Children.Add(Make("Remove for all characters", ScopeDeleteChoice.RemoveForAll, isDefault: !allowOptOut));
        var cancel = Make("Cancel", ScopeDeleteChoice.Cancel);
        cancel.IsCancel = true;
        buttons.Children.Add(cancel);

        Content = new StackPanel
        {
            Margin  = new Thickness(16),
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = $"“{what}” is shared by all characters." + (allowOptOut
                        ? "\n\nDisable it for this character only (reversible — a disabled" +
                          " local copy shadows it), or remove it for everyone?"
                        : "\n\nRemove it for every character?"),
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                },
                buttons,
            },
        };
    }

    /// <summary>Show modally; resolves to Cancel when closed any other way.</summary>
    public static async Task<ScopeDeleteChoice> Show(Window owner, string what, bool allowOptOut)
    {
        var dlg    = new ScopeDeleteDialog(what, allowOptOut);
        var result = await dlg.ShowDialog<ScopeDeleteChoice?>(owner);
        return result ?? ScopeDeleteChoice.Cancel;
    }
}
