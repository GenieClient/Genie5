using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Genie.App.ViewModels;
using Xunit;

namespace Genie.App.Tests;

/// <summary>
/// Guards the Window-menu bindings for the stream panels.
///
/// <para>
/// Why this exists: <c>MainWindow.axaml</c> declares <c>x:DataType</c> but the
/// project does not enable <c>AvaloniaUseCompiledBindingsByDefault</c>, so
/// <c>{Binding …}</c> paths are resolved by reflection at RUNTIME. Verified
/// empirically by renaming a binding to a property that doesn't exist — the
/// build still succeeded. A misspelled <c>IsChecked</c> path or a missing
/// <c>Command</c> therefore ships as a silently dead menu item: the entry
/// renders, the checkbox never updates, and clicking it does nothing.
/// </para>
///
/// <para>
/// These tests read the shipped XAML and assert every stream toggle's two
/// binding targets actually exist on <see cref="MainWindowViewModel"/>. They
/// reflect over the TYPE only — no construction, no Avalonia platform.
/// </para>
/// </summary>
public class WindowMenuBindingTests
{
    private static string MainWindowXaml()
    {
        // Walk up from the test binary to the repo root, then to the view.
        var dir = AppContext.BaseDirectory;
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "src", "Genie.App", "Views", "MainWindow.axaml");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        throw new FileNotFoundException(
            $"Could not locate src/Genie.App/Views/MainWindow.axaml walking up from {dir}");
    }

    private static bool HasMember(string name) =>
        typeof(MainWindowViewModel).GetMember(
            name, BindingFlags.Public | BindingFlags.Instance).Length > 0;

    [Theory]
    [InlineData("OocVisible", "ToggleOocCommand")]                   // public #260
    [InlineData("AtmosphericsVisible", "ToggleAtmosphericsCommand")] // the pattern OOC was modelled on
    [InlineData("WhispersVisible", "ToggleWhispersCommand")]
    [InlineData("ThoughtsVisible", "ToggleThoughtsCommand")]
    public void Stream_toggle_binding_targets_exist_on_the_view_model(string visible, string command)
    {
        Assert.True(HasMember(visible),
            $"MainWindow.axaml binds IsChecked to '{visible}', which does not exist on MainWindowViewModel.");
        Assert.True(HasMember(command),
            $"MainWindow.axaml binds Command to '{command}', which does not exist on MainWindowViewModel.");
    }

    [Fact]
    public void Ooc_menu_item_is_present_and_wired()
    {
        var xaml = MainWindowXaml();

        // The menu entry itself — without it the window is unreachable from the UI
        // even though the tool is registered in the dock factory.
        Assert.Contains("{Binding OocVisible, Mode=OneWay}", xaml);
        Assert.Contains("{Binding ToggleOocCommand}", xaml);
    }

    /// <summary>The <c>&lt;MenuItem Header="_Window"&gt;</c> subtree, sliced out by
    /// brace-free scanning from its header to the start of the next top-level
    /// menu. Scoping matters: elsewhere in the file, DataTemplates bind against
    /// their own <c>x:DataType</c> (ScriptBarItem, PerfRowViewModel, …), and
    /// those paths correctly do NOT resolve on MainWindowViewModel.</summary>
    private static string WindowMenuBlock()
    {
        var xaml = MainWindowXaml();
        var start = xaml.IndexOf("<MenuItem Header=\"_Window\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "Could not find the Window menu in MainWindow.axaml.");

        // The next top-level menu header ends the block. Search past our own.
        var next = xaml.IndexOf("<MenuItem Header=\"_Scripts\"", start + 1, StringComparison.Ordinal);
        return next > start ? xaml[start..next] : xaml[start..];
    }

    [Fact]
    public void Every_toggle_binding_in_the_window_menu_resolves_to_a_real_member()
    {
        // Broader net over the same failure mode, scoped to the Window menu:
        // pull every simple {Binding X} path naming a visibility flag or a
        // command and confirm it resolves. Dotted paths (Display.AlwaysOnTop)
        // are excluded by the regex — those walk into nested view models this
        // test doesn't own.
        // Drop <Setter …> elements first. The Window menu hosts three
        // ItemsSource-driven submenus (plugin windows, loadable plugin files,
        // loaded plugins) whose container styles bind against the ITEM type —
        // IsVisible / ToggleCommand / LoadCommand / UnloadCommand live on those
        // items, not on MainWindowViewModel, and are correct as written.
        var block = Regex.Replace(WindowMenuBlock(), @"<Setter\b[^>]*/?>", "");

        var paths = Regex.Matches(block, @"\{Binding\s+(?<path>[A-Za-z_][A-Za-z0-9_]*)\s*(?:,|\})")
            .Select(m => m.Groups["path"].Value)
            .Where(p => p.EndsWith("Visible", StringComparison.Ordinal)
                     || p.EndsWith("Command", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // Sanity-check the slice actually caught the menu we care about.
        Assert.Contains("OocVisible", paths);
        Assert.Contains("ToggleOocCommand", paths);

        var missing = paths.Where(p => !HasMember(p)).ToList();
        Assert.True(missing.Count == 0,
            "The Window menu binds to members that don't exist on MainWindowViewModel: "
            + string.Join(", ", missing));
    }
}
