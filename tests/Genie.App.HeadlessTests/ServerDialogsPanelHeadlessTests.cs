using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Genie.App.Views;
using Genie.Core.Dialogs;
using Xunit;

namespace Genie.App.HeadlessTests;

/// <summary>
/// Configuration → Layout → Server Dialogs (#156), driven through the real
/// controls: pick a row, change where it goes, Save; Forget drops the answer.
/// </summary>
public class ServerDialogsPanelHeadlessTests
{
    private static (Hosted<ServerDialogsPanel> H, ServerDialogMappings M, int[] Saves) Make()
    {
        var m = new ServerDialogMappings();
        m.Set(new ServerDialogMapping { Id = "bank_debt", Mode = ServerDialogMode.Ignore, Title = "Bank Debt" });
        m.Set(new ServerDialogMapping { Id = "spellChoose", Mode = ServerDialogMode.NewWindow, Title = "Spells" });
        var saves = new int[1];
        var h = new Hosted<ServerDialogsPanel>(new ServerDialogsPanel());
        h.Panel.Initialize(m, () => saves[0]++, config: null, onConfigChanged: null);
        h.Pump();
        return (h, m, saves);
    }

    private static void Select(Hosted<ServerDialogsPanel> h, string id)
    {
        h.Grid.SelectedItem = h.Rows<ServerDialogsPanel.MappingRow>().Single(r => r.Id == id);
        h.Pump();
    }

    [AvaloniaFact]
    public void Lists_every_saved_answer()
    {
        var (h, _, _) = Make();
        using (h)
        {
            var rows = h.Rows<ServerDialogsPanel.MappingRow>();
            Assert.Equal(new[] { "bank_debt", "spellChoose" }, rows.Select(r => r.Id));
            Assert.Equal("Never show it", rows[0].Where);
        }
    }

    /// <summary>The recovery path #343 and #dialogs forget were stopgaps for:
    /// undoing "Never show it" from inside the app.</summary>
    [AvaloniaFact]
    public void Changing_never_show_it_to_its_own_window_saves()
    {
        var (h, m, saves) = Make();
        using (h)
        {
            Select(h, "bank_debt");
            var mode = h.Panel.FindControl<ComboBox>("ModeBox")!;
            mode.SelectedItem = mode.Items.Cast<object>().Single(o => o.ToString() == "Its own window");
            h.Panel.FindControl<CheckBox>("AutoOpenCheck")!.IsChecked = false;

            h.ClickButton("Save");

            var saved = m.Find("bank_debt")!;
            Assert.Equal(ServerDialogMode.NewWindow, saved.Mode);
            Assert.False(saved.AutoOpen);
            Assert.Equal(1, saves[0]);
            Assert.Equal("Its own window",
                h.Rows<ServerDialogsPanel.MappingRow>().Single(r => r.Id == "bank_debt").Where);
        }
    }

    [AvaloniaFact]
    public void Forget_drops_the_answer_so_the_chooser_asks_again()
    {
        var (h, m, saves) = Make();
        using (h)
        {
            Select(h, "spellChoose");
            h.ClickButton("Forget");

            Assert.Null(m.Find("spellChoose"));
            Assert.True(m.Resolve("spellChoose").NeedsPrompt);
            Assert.Equal(1, saves[0]);
            Assert.DoesNotContain(h.Rows<ServerDialogsPanel.MappingRow>(), r => r.Id == "spellChoose");
        }
    }

    [AvaloniaFact]
    public void The_master_switch_is_disabled_until_connected()
    {
        var (h, _, _) = Make();
        using (h)
        {
            Assert.False(h.Panel.FindControl<CheckBox>("MasterCheck")!.IsEnabled);
            Assert.True(h.Panel.FindControl<TextBlock>("MasterHint")!.IsVisible);
        }
    }
}
