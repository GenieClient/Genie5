using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Genie.App.Views;
using Genie.Core.Highlights;
using Genie.Core.Macros;
using Genie.Core.Variables;
using Xunit;

namespace Genie.App.HeadlessTests;

/// <summary>
/// "Find…" filter boxes on the Variables / Macros / Names config panels —
/// the three list panels that shipped without the <c>PanelFilterHelpers</c>
/// filter the other five rule panels already had. Each test drives the real
/// TextBox (TextChanged → Refresh) rather than calling the helper directly,
/// so a renamed control or unhooked handler fails here.
/// </summary>
public class PanelFilterHeadlessTests
{
    /// <summary>Shows the panel in a window; disposal closes the window even
    /// when an assertion throws (the headless platform + dispatcher are shared
    /// by the whole test assembly, so a leaked window outlives the test).</summary>
    private sealed class Hosted<T> : IDisposable where T : Control
    {
        public Window Window { get; }
        public T      Panel  { get; }

        public Hosted(T panel)
        {
            Panel  = panel;
            Window = new Window { Width = 800, Height = 600, Content = panel };
            Window.Show();
            Pump();
        }

        public void Pump()
        {
            Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
        }

        public void SetFilter(string text)
        {
            var box = Panel.FindControl<TextBox>("FilterBox");
            Assert.NotNull(box);
            box!.Text = text;
            Pump();
        }

        public DataGrid Grid
        {
            get
            {
                var grid = Panel.FindControl<DataGrid>("ItemsList");
                Assert.NotNull(grid);
                return grid!;
            }
        }

        public IReadOnlyList<TRow> Rows<TRow>() =>
            (Grid.ItemsSource ?? Enumerable.Empty<object>()).Cast<TRow>().ToList();

        public void Dispose()
        {
            try { Window.Close(); } catch { /* teardown */ }
        }
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
        return h;
    }

    [AvaloniaFact]
    public void Variables_filter_narrows_by_name()
    {
        using var h = MakeVariablesPanel();
        Assert.Equal(3, h.Rows<VariablesPanel.VariableRow>().Count);

        h.SetFilter("age");
        var rows = h.Rows<VariablesPanel.VariableRow>();
        Assert.Single(rows);
        Assert.Equal("age", rows[0].Name);
    }

    [AvaloniaFact]
    public void Variables_filter_matches_values_and_is_case_insensitive()
    {
        using var h = MakeVariablesPanel();

        // "off" only appears in AA's VALUE; case differs from the stored "OFF".
        h.SetFilter("off");
        var rows = h.Rows<VariablesPanel.VariableRow>();
        Assert.Single(rows);
        Assert.Equal("AA", rows[0].Name);
    }

    [AvaloniaFact]
    public void Variables_clearing_the_filter_restores_the_full_list()
    {
        using var h = MakeVariablesPanel();
        h.SetFilter("automapper");
        Assert.Single(h.Rows<VariablesPanel.VariableRow>());

        h.SetFilter("");
        Assert.Equal(3, h.Rows<VariablesPanel.VariableRow>().Count);
    }

    [AvaloniaFact]
    public void Variables_save_while_filtered_keeps_the_filter_applied()
    {
        using var h = MakeVariablesPanel();
        h.SetFilter("age");
        Assert.Single(h.Rows<VariablesPanel.VariableRow>());

        // Saving a non-matching variable refreshes the grid; the active filter
        // must survive the refresh (the new row simply doesn't match).
        h.Panel.FindControl<TextBox>("NameBox")!.Text  = "ZZZ";
        h.Panel.FindControl<TextBox>("ValueBox")!.Text = "1";
        var save = h.Panel.GetLogicalDescendants().OfType<Button>()
            .First(b => (string?)b.Content == "Save");
        save.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        h.Pump();

        Assert.Single(h.Rows<VariablesPanel.VariableRow>());
        h.SetFilter("");
        Assert.Equal(4, h.Rows<VariablesPanel.VariableRow>().Count);
    }

    [AvaloniaFact]
    public void Variables_filter_keystroke_preserves_a_multi_row_selection()
    {
        using var h = MakeVariablesPanel();
        var rows = h.Rows<VariablesPanel.VariableRow>();

        // Ctrl-click two rows, then type a filter both still match: the grid
        // is Extended-mode with a multi-row Copy (#97), so the selection must
        // survive the per-keystroke Refresh.
        h.Grid.SelectedItems.Add(rows.First(r => r.Name == "AA"));
        h.Grid.SelectedItems.Add(rows.First(r => r.Name == "age"));
        h.Pump();

        h.SetFilter("a");   // matches AA, age, automapper.pause
        var selected = h.Grid.SelectedItems.Cast<VariablesPanel.VariableRow>()
            .Select(r => r.Name).OrderBy(n => n).ToList();
        Assert.Equal(new[] { "AA", "age" }, selected);
    }

    // ------------------------------------------------------------------- Macros

    [AvaloniaFact]
    public void Macros_filter_matches_key_or_action()
    {
        // Key strings use the real MacroKeyConverter vocabulary ("ctrl+f2",
        // not Avalonia's "Control+F2") — the filter must match what the
        // capture box actually stores.
        var engine = new MacroEngine();
        engine.Add("f1", "attack");
        engine.Add("ctrl+f2", "hide");
        using var h = new Hosted<MacrosPanel>(new MacrosPanel());
        h.Panel.Initialize(engine);

        Assert.Equal(2, h.Rows<MacrosPanel.MacroRow>().Count);

        h.SetFilter("attack");   // action text
        var rows = h.Rows<MacrosPanel.MacroRow>();
        Assert.Single(rows);
        Assert.Equal("f1", rows[0].Key);

        h.SetFilter("ctrl");     // key text, as a user would type it
        rows = h.Rows<MacrosPanel.MacroRow>();
        Assert.Single(rows);
        Assert.Equal("ctrl+f2", rows[0].Key);
    }

    // -------------------------------------------------------------------- Names

    [AvaloniaFact]
    public void Names_filter_matches_the_name()
    {
        var engine = new NameHighlightEngine();
        engine.Add("Renucci", "Yellow");
        engine.Add("Naper",   "Cyan");
        using var h = new Hosted<NamesPanel>(new NamesPanel());
        h.Panel.Initialize(engine);

        Assert.Equal(2, h.Rows<NamesPanel.NameRow>().Count);

        h.SetFilter("ren");
        var rows = h.Rows<NamesPanel.NameRow>();
        Assert.Single(rows);
        Assert.Equal("Renucci", rows[0].Name);
    }
}
