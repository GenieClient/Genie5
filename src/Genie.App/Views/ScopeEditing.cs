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

    /// <summary>After deleting a this-character rule (by its natural key),
    /// put the shared rule it was shadowing back into the engine now (public
    /// #315). Returns true when a shared twin was restored.</summary>
    public Func<string, bool>? RestoreGlobalTwin { get; init; }
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

    /// <summary>After a panel removed a rule from its engine: when the rule
    /// was a this-character one in two-layer editing, put the shared rule it
    /// shadowed back into the engine (public #315). True when one was restored.</summary>
    public static bool RestoreTwinAfterDelete(ScopeEditingContext? ctx, RuleScope deletedScope, string key) =>
        ctx is { TwoLayers: true } && deletedScope == RuleScope.Character
        && ctx.RestoreGlobalTwin?.Invoke(key) == true;

    /// <summary>Status line for a delete, naming a restored shared twin.</summary>
    public static string DeletedStatus(string key, bool restoredTwin) => restoredTwin
        ? $"Deleted this character's '{key}' — the shared (all characters) version is active again."
        : $"Deleted '{key}'.";

    // ── Scope filter (public #315) ─────────────────────────────────────────────────

    /// <summary>Scope-filter combo labels, index-aligned with <see cref="PassesFilter"/>.</summary>
    public static readonly string[] FilterLabels = ["All scopes", "This character", "All characters"];

    /// <summary>True when a row of <paramref name="scope"/> shows under the
    /// scope filter's <paramref name="index"/> (0 = everything).</summary>
    public static bool PassesFilter(int index, RuleScope scope) => index switch
    {
        1 => scope == RuleScope.Character,
        2 => scope == RuleScope.Global,
        _ => true,
    };

    /// <summary>Wire a panel's scope-filter combo: fill it, reset it to "All
    /// scopes", show it only in two-layer editing (a filter over one layer
    /// hides nothing), and hook <paramref name="refresh"/> once.</summary>
    public static void InitFilter(ComboBox box, bool twoLayers, Action refresh)
    {
        if (box.ItemsSource is null)
        {
            box.ItemsSource = FilterLabels;
            box.SelectionChanged += (_, _) => refresh();
        }
        box.SelectedIndex = 0;
        box.IsVisible     = twoLayers;
    }

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
