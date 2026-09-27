using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Genie.App.Views;
using Genie.Core.Aliases;
using Genie.Core.Classes;
using Genie.Core.Persistence;
using Genie.Core.Variables;
using Xunit;

namespace Genie.App.HeadlessTests;

/// <summary>
/// Public #315 and public #304 through the real panels: the Scope filter next to
/// each rule grid, a deleted per-character override handing back to its
/// shared twin via the Delete button, the Scope column/field on Variables and
/// Classes, and Presets as its own top-level Configuration tab.
/// </summary>
public class RuleScopePanelsHeadlessTests
{
    private static ScopeEditingContext TwoLayers(Func<string, bool>? restore = null) => new()
    {
        TwoLayers         = true,
        NoteGlobalDelete  = _ => { },
        RestoreGlobalTwin = restore,
    };

    private static void Filter(Hosted<AliasesPanel> h, int index)
    {
        var box = h.Panel.FindControl<ComboBox>("ScopeFilterBox");
        Assert.NotNull(box);
        box!.SelectedIndex = index;
        h.Pump();
    }

    private static Hosted<AliasesPanel> MixedAliases(AliasEngine engine, ScopeEditingContext ctx)
    {
        engine.AddAlias("mine", "put mine").Scope   = RuleScope.Character;
        engine.AddAlias("shared", "put all").Scope  = RuleScope.Global;
        var h = new Hosted<AliasesPanel>(new AliasesPanel());
        h.Panel.Initialize(engine, () => { }, ctx);
        h.Pump();
        return h;
    }

    [AvaloniaFact]
    public void ScopeFilter_ShowsAllThenEachLayer()
    {
        using var h = MixedAliases(new AliasEngine(), TwoLayers());
        Assert.Equal(2, h.Rows<AliasesPanel.AliasRow>().Count);

        Filter(h, 1);                                                        // This character
        Assert.Equal("mine", Assert.Single(h.Rows<AliasesPanel.AliasRow>()).Name);
        Filter(h, 2);                                                        // All characters
        Assert.Equal("shared", Assert.Single(h.Rows<AliasesPanel.AliasRow>()).Name);
        Filter(h, 0);
        Assert.Equal(2, h.Rows<AliasesPanel.AliasRow>().Count);
    }

    [AvaloniaFact]
    public void ScopeFilter_IsHiddenInSingleLayerEditing()
    {
        var engine = new AliasEngine();
        engine.AddAlias("a", "b");
        using var h = new Hosted<AliasesPanel>(new AliasesPanel());
        h.Panel.Initialize(engine, () => { }, new ScopeEditingContext { TwoLayers = false });
        h.Pump();
        Assert.False(h.Panel.FindControl<ComboBox>("ScopeFilterBox")!.IsVisible);
    }

    [AvaloniaFact]
    public void DeletingAnOverride_AsksForTheSharedTwin_AndSaysSo()
    {
        var engine    = new AliasEngine();
        var asked     = new List<string>();
        using var h   = MixedAliases(engine, TwoLayers(key =>
        {
            asked.Add(key);
            engine.AddAlias(key, "put shared version").Scope = RuleScope.Global;   // what the VM does
            return true;
        }));

        h.Grid.SelectedItem = h.Rows<AliasesPanel.AliasRow>().First(r => r.Name == "mine");
        h.Pump();
        h.ClickButton("Delete");

        Assert.Equal(new[] { "mine" }, asked);
        var mine = engine.Aliases.Single(a => a.Name == "mine");
        Assert.Equal("put shared version", mine.Expansion);
        Assert.Equal(RuleScope.Global, mine.Scope);
        Assert.Contains("shared (all characters) version is active again", h.Status);
    }

    [AvaloniaFact]
    public void VariablesPanel_ShowsScopeColumn_AndSavesTheChosenLayer()
    {
        var store = new VariableStore();
        store.Set("home", "crossing");
        store.SetConfigScope("home", RuleScope.Global);
        store.Set("hunt", "rats");
        using var h = new Hosted<VariablesPanel>(new VariablesPanel());
        h.Panel.Initialize(store, () => { }, scopeContext: TwoLayers());
        h.Pump();

        Assert.True(h.Grid.Columns.Single(c => c.Header as string == "Scope").IsVisible);
        var rows = h.Rows<VariablesPanel.VariableRow>();
        Assert.Equal("Global", rows.Single(r => r.Name == "home").Scope);
        Assert.Equal("Char",   rows.Single(r => r.Name == "hunt").Scope);

        h.Text("NameBox").Text  = "weapon";
        h.Text("ValueBox").Text = "sword";
        h.Panel.FindControl<ComboBox>("ScopeBox")!.SelectedIndex = 1;        // All characters
        h.ClickButton("Save");
        Assert.Equal(RuleScope.Global, store.GetAll()["weapon"].ConfigScope);
    }

    [AvaloniaFact]
    public void ClassesPanel_ShowsScopeColumn_AndRemovingAnOverrideRestoresTheTwin()
    {
        var engine = new ClassEngine();
        engine.Set("combat", false);                                         // this character's state
        using var h = new Hosted<ClassesPanel>(new ClassesPanel());
        h.Panel.Initialize(engine, () => { }, TwoLayers(key =>
        {
            engine.Set(key, true);
            engine.SetScope(key, RuleScope.Global);
            return true;
        }));
        h.Pump();
        Assert.True(h.Grid.Columns.Single(c => c.Header as string == "Scope").IsVisible);

        h.Grid.SelectedItem = h.Rows<ClassesPanel.ClassRow>().First(r => r.Name == "combat");
        h.Pump();
        h.ClickButton("Remove");

        Assert.True(engine.IsActive("combat"));
        Assert.Equal(RuleScope.Global, engine.ScopeOf("combat"));
        Assert.Equal("Global", h.Rows<ClassesPanel.ClassRow>().Single(r => r.Name == "combat").Scope);
    }

    // ── public #304: Presets is a top-level tab, before Highlights ────────────────

    private static List<string> Headers(TabControl tabs) =>
        tabs.Items.OfType<TabItem>().Select(t => t.Header as string ?? "").ToList();

    [AvaloniaFact]
    public void Presets_IsATopLevelConfigTab_BeforeHighlights_AndNotUnderHighlights()
    {
        var dialog = new ConfigurationDialog();
        try
        {
            dialog.Show();
            var top = dialog.GetLogicalDescendants().OfType<TabControl>().First();
            var headers = Headers(top);
            Assert.Contains("Presets", headers);
            Assert.Equal(headers.IndexOf("Highlights") - 1, headers.IndexOf("Presets"));

            var highlights = dialog.FindControl<HighlightsPanel>("HighlightsPanelCtrl");
            Assert.NotNull(highlights);
            var sub = highlights!.GetLogicalDescendants().OfType<TabControl>().First();
            Assert.Equal(new[] { "Strings", "Names" }, Headers(sub));
            Assert.NotNull(dialog.FindControl<PresetsPanel>("PresetsPanelCtrl"));
        }
        finally { dialog.Close(); }
    }
}
