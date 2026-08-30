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
    private static (Window Window, T Panel) Host<T>(T panel) where T : Control
    {
        var w = new Window { Width = 800, Height = 600, Content = panel };
        w.Show();
        Pump(w);
        return (w, panel);
    }

    private static void Pump(Window w)
    {
        w.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
    }

    private static void SetFilter(Window w, Control panel, string text)
    {
        var box = panel.FindControl<TextBox>("FilterBox");
        Assert.NotNull(box);
        box!.Text = text;
        Pump(w);
    }

    private static IReadOnlyList<object> Rows(Control panel)
    {
        var grid = panel.FindControl<DataGrid>("ItemsList");
        Assert.NotNull(grid);
        return (grid!.ItemsSource ?? Enumerable.Empty<object>()).Cast<object>().ToList();
    }

    // ---------------------------------------------------------------- Variables

    private static VariablesPanel MakeVariablesPanel(out Window w)
    {
        var store = new VariableStore();
        store.Set("AA", "OFF");
        store.Set("age", "123");
        store.Set("automapper.pause", "0.01");
        var panel = new VariablesPanel();
        (w, _) = Host(panel);
        panel.Initialize(store, onChanged: () => { });
        return panel;
    }

    [AvaloniaFact]
    public void Variables_filter_narrows_by_name()
    {
        var panel = MakeVariablesPanel(out var w);
        Assert.Equal(3, Rows(panel).Count);

        SetFilter(w, panel, "age");
        var rows = Rows(panel).Cast<VariablesPanel.VariableRow>().ToList();
        Assert.Single(rows);
        Assert.Equal("age", rows[0].Name);
        w.Close();
    }

    [AvaloniaFact]
    public void Variables_filter_matches_values_and_is_case_insensitive()
    {
        var panel = MakeVariablesPanel(out var w);

        // "off" only appears in AA's VALUE; case differs from the stored "OFF".
        SetFilter(w, panel, "off");
        var rows = Rows(panel).Cast<VariablesPanel.VariableRow>().ToList();
        Assert.Single(rows);
        Assert.Equal("AA", rows[0].Name);
        w.Close();
    }

    [AvaloniaFact]
    public void Variables_clearing_the_filter_restores_the_full_list()
    {
        var panel = MakeVariablesPanel(out var w);
        SetFilter(w, panel, "automapper");
        Assert.Single(Rows(panel));

        SetFilter(w, panel, "");
        Assert.Equal(3, Rows(panel).Count);
        w.Close();
    }

    [AvaloniaFact]
    public void Variables_save_while_filtered_keeps_the_filter_applied()
    {
        var panel = MakeVariablesPanel(out var w);
        SetFilter(w, panel, "age");
        Assert.Single(Rows(panel));

        // Saving a non-matching variable refreshes the grid; the active filter
        // must survive the refresh (the new row simply doesn't match).
        panel.FindControl<TextBox>("NameBox")!.Text  = "ZZZ";
        panel.FindControl<TextBox>("ValueBox")!.Text = "1";
        var save = panel.GetLogicalDescendants().OfType<Button>()
            .First(b => (string?)b.Content == "Save");
        save.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Pump(w);

        Assert.Single(Rows(panel));
        SetFilter(w, panel, "");
        Assert.Equal(4, Rows(panel).Count);
        w.Close();
    }

    // ------------------------------------------------------------------- Macros

    [AvaloniaFact]
    public void Macros_filter_matches_key_or_action()
    {
        var engine = new MacroEngine();
        engine.Add("F1", "attack");
        engine.Add("Control+F2", "hide");
        var panel = new MacrosPanel();
        var (w, _) = Host(panel);
        panel.Initialize(engine);

        Assert.Equal(2, Rows(panel).Count);

        SetFilter(w, panel, "attack");   // action text
        var rows = Rows(panel).Cast<MacrosPanel.MacroRow>().ToList();
        Assert.Single(rows);
        Assert.Equal("F1", rows[0].Key);

        SetFilter(w, panel, "control");  // key text
        rows = Rows(panel).Cast<MacrosPanel.MacroRow>().ToList();
        Assert.Single(rows);
        Assert.Equal("Control+F2", rows[0].Key);
        w.Close();
    }

    // -------------------------------------------------------------------- Names

    [AvaloniaFact]
    public void Names_filter_matches_the_name()
    {
        var engine = new NameHighlightEngine();
        engine.Add("Renucci", "Yellow");
        engine.Add("Naper",   "Cyan");
        var panel = new NamesPanel();
        var (w, _) = Host(panel);
        panel.Initialize(engine);

        Assert.Equal(2, Rows(panel).Count);

        SetFilter(w, panel, "ren");
        var rows = Rows(panel).Cast<NamesPanel.NameRow>().ToList();
        Assert.Single(rows);
        Assert.Equal("Renucci", rows[0].Name);
        w.Close();
    }
}
