using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Xunit;

namespace Genie.App.HeadlessTests;

/// <summary>Shows a config panel in a window; disposal closes the window even
/// when an assertion throws (the headless platform + dispatcher are shared
/// by the whole test assembly, so a leaked window outlives the test).</summary>
internal sealed class Hosted<T> : IDisposable where T : Control
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

    /// <summary>The named TextBox on the panel (editor-form field or FilterBox).</summary>
    public TextBox Text(string name)
    {
        var box = Panel.FindControl<TextBox>(name);
        Assert.NotNull(box);
        return box!;
    }

    public string Status => Panel.FindControl<TextBlock>("StatusText")?.Text ?? string.Empty;

    /// <summary>Raise Click on the panel button carrying this label, driving
    /// the real XAML-wired handler.</summary>
    public void ClickButton(string label)
    {
        var button = Panel.GetLogicalDescendants().OfType<Button>()
            .First(b => (string?)b.Content == label);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
    }

    public void Dispose()
    {
        try { Window.Close(); } catch { /* teardown */ }
    }
}
