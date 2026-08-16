using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Genie.App.ViewModels;
using Genie.App.Views;
using Xunit;

namespace Genie.App.Tests;

/// <summary>
/// Guards the top-level <b>Alterations</b> menu — the entry point for the ported
/// Alteration Buddy feature.
///
/// <para>
/// Same failure mode <see cref="WindowMenuBindingTests"/> exists for: this
/// project does not enable <c>AvaloniaUseCompiledBindingsByDefault</c>, so a
/// mistyped <c>{Binding …}</c> path or a renamed <c>SubmenuOpened</c> handler
/// builds clean and ships a menu whose items simply do nothing. Since the whole
/// feature is reachable ONLY through this menu, a dead binding here means a
/// shipped feature no user can open.
/// </para>
///
/// Reflection over types only — no Avalonia platform, no window construction.
/// </summary>
public class AlterationsMenuBindingTests
{
    private static string MainWindowXaml()
    {
        var dir = AppContext.BaseDirectory;
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "src", "Genie.App", "Views", "MainWindow.axaml");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        throw new FileNotFoundException(
            $"Could not locate src/Genie.App/Views/MainWindow.axaml walking up from {dir}");
    }

    /// <summary>The Alterations menu subtree. It is the last top-level menu, so
    /// the block runs from its header to the closing <c>&lt;/Menu&gt;</c>.</summary>
    private static string AlterationsMenuBlock()
    {
        var xaml  = MainWindowXaml();
        var start = xaml.IndexOf("<MenuItem Header=\"A_lterations\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "Could not find the Alterations menu in MainWindow.axaml.");

        var end = xaml.IndexOf("</Menu>", start, StringComparison.Ordinal);
        return end > start ? xaml[start..end] : xaml[start..];
    }

    private static bool HasMember(string name) =>
        typeof(MainWindowViewModel).GetMember(
            name, BindingFlags.Public | BindingFlags.Instance).Length > 0;

    [Fact]
    public void The_menu_exists_as_a_top_level_entry()
    {
        // Not a dockable panel — the whole point of the port's shape. If this
        // ever moves under Window, the feature's discoverability changes and
        // this test should be updated deliberately, not silently.
        var xaml = MainWindowXaml();

        Assert.Contains("<MenuItem Header=\"A_lterations\"", xaml);
        Assert.DoesNotContain("Header=\"_Alteration Designer\"", xaml);
    }

    [Fact]
    public void Every_command_binding_in_the_menu_resolves_to_a_real_member()
    {
        var paths = Regex.Matches(AlterationsMenuBlock(),
                                  @"\{Binding\s+(?<path>[A-Za-z_][A-Za-z0-9_]*)\s*(?:,|\})")
            .Select(m => m.Groups["path"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // Sanity-check the slice caught the menu we care about.
        Assert.Contains("ShowAlterationDesignerCommand", paths);

        var missing = paths.Where(p => !HasMember(p)).ToList();
        Assert.True(missing.Count == 0,
            "The Alterations menu binds to members that don't exist on MainWindowViewModel: "
            + string.Join(", ", missing));
    }

    [Theory]
    [InlineData("ShowAlterationDesignerCommand")]
    [InlineData("OpenAlterationCommand")]
    [InlineData("ImportAlterationsCommand")]
    [InlineData("ExportAlterationsCommand")]
    [InlineData("ReloadAlterationsCommand")]
    [InlineData("OpenAlterationsFolderCommand")]
    [InlineData("OpenAlterationGuideCommand")]
    [InlineData("OpenWitchsWorkshopCommand")]
    [InlineData("Alterations")]
    public void The_view_model_exposes_every_member_the_menu_and_its_builder_need(string member)
    {
        Assert.True(HasMember(member),
            $"MainWindowViewModel is missing '{member}', which the Alterations menu depends on.");
    }

    [Fact]
    public void The_saved_designs_submenu_is_named_and_has_an_open_handler()
    {
        // The submenu is populated in code-behind on SubmenuOpened; both the
        // x:Name lookup and the handler name are resolved at runtime by XAML,
        // so a rename on either side fails silently.
        var block = AlterationsMenuBlock();

        Assert.Contains("SubmenuOpened=\"OnAlterationsMenuOpened\"", block);
        Assert.Contains("x:Name=\"SavedAlterationsMenu\"", block);

        var handler = typeof(MainWindow).GetMethod(
            "OnAlterationsMenuOpened",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(handler);
    }

    [Fact]
    public void The_designer_dialog_takes_the_view_model_and_a_load_index()
    {
        // The Saved Designs submenu passes an index through OpenAlterationCommand;
        // "open blank" is the same path with -1. A constructor change that drops
        // the index would break every saved-design menu entry.
        var ctor = typeof(AlterationDesignerDialog).GetConstructor(
            new[] { typeof(AlterationsViewModel), typeof(int) });

        Assert.NotNull(ctor);
    }
}
