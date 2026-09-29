using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Genie.App.Docking;
using Genie.App.ViewModels;
using Genie.App.Views;
using Genie.Core.Dialogs;
using Genie.Core.Events;
using Genie.Core.Parser;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Genie.App.HeadlessTests;

/// <summary>
/// Public #374, over the production <c>ServerDialogTool</c> template: a dialog
/// with a bespoke view must NOT also draw the generic grid underneath it. The
/// generic panel's IsVisible was bound on the same element that set its
/// DataContext to the ViewModel, so the path never resolved and the grid
/// always drew, printing every body-part label over the injuries sprites.
/// </summary>
public class ServerDialogBespokeHeadlessTests
{
    private static Genie.App.App? _app;

    private const string OtherInjuriesXml =
        "<openDialog type=\"dynamic\" id=\"injuries-1\" title=\"Renucci's Injuries\" location=\"center\" height=\"200\" width=\"190\">\n" +
        "<dialogData id=\"injuries-1\"><progressBar id=\"health2\" value=\"100\" text=\"HEALTH 100%\" customText=\"t\" top=\"0\" left=\"0\" width=\"140\" height=\"15\"/>" +
        "<cmdButton id=\"xferVit\" value=\"Transfer Vit\" cmd=\"transfer Renucci vitality\" top=\"20\" left=\"150\"/>" +
        // Untinted sprites only: the headless renderer can't CopyPixels, which the
        // wound tint needs. Injured-part behaviour is covered at VM level.
        "<image id=\"chest\" name=\"chest\" height=\"0\" width=\"0\"/>" +
        "<image id=\"head\" name=\"head\" height=\"0\" width=\"0\"/></dialogData>\n";

    private const string BankXml =
        "<openDialog type=\"dynamic\" id=\"bank_debt\" title=\"Bank\" location=\"center\" height=\"100\" width=\"190\">\n" +
        "<dialogData id=\"bank_debt\"><cmdButton id=\"dep\" value=\"Deposit\" cmd=\"deposit\" top=\"0\" left=\"0\"/></dialogData>\n";

    private static void EnsureProductionTemplates()
    {
        _app ??= Build();
        static Genie.App.App Build() { var a = new Genie.App.App(); a.Initialize(); return a; }

        var cur = Application.Current!;
        if (!cur.DataTemplates.OfType<Avalonia.Markup.Xaml.Templates.DataTemplate>().Any(t => t.DataType == typeof(ServerDialogTool)))
            foreach (var t in _app.DataTemplates) cur.DataTemplates.Add(t);
        if (!cur.Resources.ContainsKey("Theme.PanelBg"))
            cur.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Genie.App"))
            {
                Source = new Uri("avares://Genie5/Themes/ThemePalette.axaml")
            });
    }

    private static ServerDialogViewModel Dialog(string xml, string id)
    {
        var engine = new ServerDialogEngine();
        var parser = new DrXmlParser(NullLogger<DrXmlParser>.Instance);
        using (parser.GameEvents.Subscribe(new Sink(engine))) parser.Feed(xml);
        var vm = new ServerDialogViewModel(id);
        vm.Apply(engine.Get(id)!);
        return vm;
    }

    private sealed class Sink(ServerDialogEngine engine) : IObserver<GameEvent>
    {
        public void OnNext(GameEvent e)
        {
            switch (e)
            {
                case OpenDialogEvent od: engine.Observe(od); break;
                case DialogDataEvent dd: engine.Observe(dd); break;
            }
        }
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    private static Window Show(ServerDialogViewModel vm, string id)
    {
        EnsureProductionTemplates();
        var tool   = new ServerDialogTool(vm, id, vm.Title);
        var window = new Window { Width = 600, Height = 500, Content = new ContentControl { Content = tool } };
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return window;
    }

    private static bool Shown(Control c) => c.IsEffectivelyVisible;

    [AvaloniaFact]
    public void A_bespoke_dialog_does_not_draw_the_generic_grid_underneath()
    {
        var window = Show(Dialog(OtherInjuriesXml, "injuries-1"), "injuries-1");
        try
        {
            var bespoke = window.GetVisualDescendants().OfType<OtherInjuriesPanel>().Single();
            Assert.True(Shown(bespoke));

            // The generic panel may be realised, but never visible.
            Assert.All(window.GetVisualDescendants().OfType<ServerDialogPanel>(), p => Assert.False(Shown(p)));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void The_bespoke_view_shows_the_dialogs_own_button_and_bar()
    {
        var window = Show(Dialog(OtherInjuriesXml, "injuries-1"), "injuries-1");
        try
        {
            var bespoke = window.GetVisualDescendants().OfType<OtherInjuriesPanel>().Single();
            var buttons = bespoke.GetVisualDescendants().OfType<Button>()
                                 .Where(b => b.Content is string && Shown(b))
                                 .Select(b => (string)b.Content!).ToList();
            Assert.Contains("Transfer Vit", buttons);
            Assert.Contains(bespoke.GetVisualDescendants().OfType<ProgressBar>(), Shown);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void A_generic_dialog_still_draws_its_grid()
    {
        var window = Show(Dialog(BankXml, "bank_debt"), "bank_debt");
        try
        {
            Assert.Contains(window.GetVisualDescendants().OfType<ServerDialogPanel>(), Shown);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<OtherInjuriesPanel>(), Shown);
        }
        finally { window.Close(); }
    }
}
