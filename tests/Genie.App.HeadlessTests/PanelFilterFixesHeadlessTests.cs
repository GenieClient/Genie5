using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Genie.App.Views;
using Genie.Core.Aliases;
using Genie.Core.Classes;
using Genie.Core.Highlights;
using Genie.Core.Macros;
using Genie.Core.Variables;
using Xunit;

namespace Genie.App.HeadlessTests;

/// <summary>
/// The deferred Find…-filter gaps shared by every filtered config panel
/// (review of the original filter-box branch): a profile-switch re-Initialize
/// must drop the old filter and form; filtering out the selected row must
/// clear the editor pane instead of leaving it stale; a filter keystroke must
/// not silently revert unsaved editor edits; Toggle must keep the Enabled
/// checkbox honest despite the unsaved-edit guard; MacrosPanel Save must not
/// reset a class-scoped macro to "default"; ClassesPanel gains the same Find…
/// box; and the Variables Select All reports when a filter truncates it.
/// The panels share one drop-in pattern, so AliasesPanel stands in for the
/// rule panels (HighlightStrings has its own guard via _editingPattern and
/// gets its own edit-preservation test).
/// </summary>
public class PanelFilterFixesHeadlessTests
{
    // ------------------------------------------------------------------ Aliases

    private static AliasEngine MakeAliases(params (string Name, string Expansion)[] aliases)
    {
        var engine = new AliasEngine();
        foreach (var (name, expansion) in aliases)
            engine.AddAlias(name, expansion);
        return engine;
    }

    private static Hosted<AliasesPanel> MakeAliasesPanel(AliasEngine engine)
    {
        var h = new Hosted<AliasesPanel>(new AliasesPanel());
        h.Panel.Initialize(engine);
        h.Pump();
        return h;
    }

    private static void Select(Hosted<AliasesPanel> h, string name)
    {
        h.Grid.SelectedItem = h.Rows<AliasesPanel.AliasRow>().First(r => r.Name == name);
        h.Pump();
    }

    [AvaloniaFact]
    public void ReInitialize_clears_the_filter_and_shows_the_new_engines_full_list()
    {
        using var h = MakeAliasesPanel(MakeAliases(("hunt", "east"), ("stow", "west")));
        h.SetFilter("hunt");
        Assert.Single(h.Rows<AliasesPanel.AliasRow>());

        // Profile switch = the dialog re-invokes Initialize with new engines.
        // The old profile's filter must not make the new list render empty.
        h.Panel.Initialize(MakeAliases(("walk", "n"), ("run", "s")));
        h.Pump();

        Assert.True(string.IsNullOrEmpty(h.Text("FilterBox").Text));
        Assert.Equal(2, h.Rows<AliasesPanel.AliasRow>().Count);
    }

    [AvaloniaFact]
    public void ReInitialize_clears_the_form_even_when_the_new_engine_has_a_same_named_rule()
    {
        using var h = MakeAliasesPanel(MakeAliases(("hunt", "old expansion")));
        Select(h, "hunt");
        Assert.Equal("old expansion", h.Text("ExpansionBox").Text);

        h.Panel.Initialize(MakeAliases(("hunt", "new expansion")));
        h.Pump();

        // The form must not keep showing the OLD profile's values against the
        // new profile's engine.
        Assert.True(string.IsNullOrEmpty(h.Text("NameBox").Text));
        Assert.True(string.IsNullOrEmpty(h.Text("ExpansionBox").Text));
        Assert.Null(h.Grid.SelectedItem);
    }

    [AvaloniaFact]
    public void Filtering_out_the_selected_row_clears_the_editor_pane()
    {
        using var h = MakeAliasesPanel(MakeAliases(("hunt", "east"), ("stow", "west")));
        Select(h, "hunt");
        Assert.Equal("hunt", h.Text("NameBox").Text);

        h.SetFilter("stow");   // hides the selected "hunt" row

        // Without the fix the form kept showing "hunt" while nothing was
        // selected — Delete then no-oped with a contradictory message and
        // Save silently rewrote a hidden rule.
        Assert.Null(h.Grid.SelectedItem);
        Assert.True(string.IsNullOrEmpty(h.Text("NameBox").Text));
        Assert.True(string.IsNullOrEmpty(h.Text("ExpansionBox").Text));
    }

    [AvaloniaFact]
    public void Filter_keystroke_preserves_unsaved_editor_edits()
    {
        using var h = MakeAliasesPanel(MakeAliases(("hunt", "east"), ("stow", "west")));
        Select(h, "hunt");
        h.Text("ExpansionBox").Text = "unsaved edit";

        h.SetFilter("hu");   // still matches the selected row

        // The Refresh restores the selection to the same rule; the form must
        // NOT be rewritten from stored values.
        Assert.Equal("unsaved edit", h.Text("ExpansionBox").Text);
        Assert.Equal("hunt", ((AliasesPanel.AliasRow)h.Grid.SelectedItem!).Name);
    }

    [AvaloniaFact]
    public void Toggle_keeps_the_enabled_checkbox_in_sync()
    {
        var engine = MakeAliases(("hunt", "east"));
        using var h = MakeAliasesPanel(engine);
        Select(h, "hunt");
        var check = h.Panel.FindControl<CheckBox>("EnabledCheck")!;
        Assert.True(check.IsChecked);

        // The unsaved-edit guard skips the form rewrite on the restored
        // selection, so Toggle syncs the checkbox explicitly.
        h.ClickButton("Toggle");
        Assert.False(engine.Aliases.Single().IsEnabled);
        Assert.False(check.IsChecked);

        h.ClickButton("Toggle");
        Assert.True(engine.Aliases.Single().IsEnabled);
        Assert.True(check.IsChecked);
    }

    // --------------------------------------------------------------- Highlights

    [AvaloniaFact]
    public void Highlights_filter_keystroke_preserves_unsaved_editor_edits()
    {
        var engine = new HighlightEngine();
        engine.AddRule("dragon", "Red");
        engine.AddRule("kobold", "Green");
        using var h = new Hosted<HighlightStringsPanel>(new HighlightStringsPanel());
        h.Panel.Initialize(engine);
        h.Pump();

        h.Grid.SelectedItem = h.Rows<HighlightStringsPanel.HighlightRow>()
            .First(r => r.Pattern == "dragon");
        h.Pump();
        h.Text("ClassBox").Text = "unsaved-class";

        h.SetFilter("drag");
        Assert.Equal("unsaved-class", h.Text("ClassBox").Text);
    }

    // ------------------------------------------------------------------- Macros

    [AvaloniaFact]
    public void Macros_save_preserves_the_class_of_a_class_scoped_macro()
    {
        var engine = new MacroEngine();
        engine.Add("f1", "attack", "hunting");
        using var h = new Hosted<MacrosPanel>(new MacrosPanel());
        h.Panel.Initialize(engine);
        h.Pump();

        h.Grid.SelectedItem = h.Rows<MacrosPanel.MacroRow>().Single();
        h.Pump();
        h.ClickButton("Save");

        // The form doesn't surface ClassName; a dialog save must carry it
        // through instead of resetting it to "default".
        Assert.Equal("hunting", engine.Rules.Single().ClassName);
    }

    // ------------------------------------------------------------------ Classes

    [AvaloniaFact]
    public void Classes_find_box_narrows_by_name_and_clears()
    {
        var engine = new ClassEngine();     // seeds "default"
        engine.Set("hunting", true);
        engine.Set("travel", false);
        using var h = new Hosted<ClassesPanel>(new ClassesPanel());
        h.Panel.Initialize(engine);
        h.Pump();

        Assert.Equal(3, h.Rows<ClassesPanel.ClassRow>().Count);

        h.SetFilter("trav");
        var rows = h.Rows<ClassesPanel.ClassRow>();
        Assert.Single(rows);
        Assert.Equal("travel", rows[0].Name);

        h.SetFilter("");
        Assert.Equal(3, h.Rows<ClassesPanel.ClassRow>().Count);
    }

    // ---------------------------------------------------------------- Variables

    private static Hosted<VariablesPanel> MakeVariablesPanel()
    {
        var store = new VariableStore();
        store.Set("AA", "OFF");
        store.Set("age", "123");
        store.Set("automapper.pause", "0.01");
        var h = new Hosted<VariablesPanel>(new VariablesPanel());
        h.Panel.Initialize(store, onChanged: () => { });
        h.Pump();
        return h;
    }

    [AvaloniaFact]
    public void Variables_filtering_out_the_selected_row_clears_the_editor_pane()
    {
        using var h = MakeVariablesPanel();
        h.Grid.SelectedItem = h.Rows<VariablesPanel.VariableRow>().First(r => r.Name == "age");
        h.Pump();
        Assert.Equal("age", h.Text("NameBox").Text);

        h.SetFilter("automapper");
        Assert.True(string.IsNullOrEmpty(h.Text("NameBox").Text));
        Assert.True(string.IsNullOrEmpty(h.Text("ValueBox").Text));
    }

    [AvaloniaFact]
    public void Variables_select_all_under_a_filter_reports_the_partial_selection()
    {
        using var h = MakeVariablesPanel();
        h.SetFilter("age");
        Assert.Single(h.Rows<VariablesPanel.VariableRow>());

        ClickSelectAll(h);
        Assert.Contains("1 of 3", h.Status);

        // Unfiltered, Select All really does select everything — no hint.
        h.SetFilter("");
        ClickSelectAll(h);
        Assert.Equal(string.Empty, h.Status);
        Assert.Equal(3, h.Grid.SelectedItems!.Count);
    }

    /// <summary>Drive the grid's context-menu "Select All" through its real
    /// Click handler (the menu itself never opens headless).</summary>
    private static void ClickSelectAll(Hosted<VariablesPanel> h)
    {
        var item = h.Grid.ContextMenu!.Items.OfType<MenuItem>()
            .First(m => (string?)m.Header == "Select All");
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        h.Pump();
    }
}
