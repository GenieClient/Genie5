using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Genie.App.ViewModels;
using Genie.App.Views;
using Xunit;

namespace Genie.App.HeadlessTests;

/// <summary>
/// #422 review — the Display Settings dialog grew to 640 px with no resize, so on
/// a 1366x768 screen at 125% (about 614 logical px) its OK / Cancel buttons were
/// off screen. It keeps its old 560 px height, scrolls its tabs, and can resize.
/// </summary>
public class DisplaySettingsDialogFitHeadlessTests
{
    [AvaloniaFact]
    public void Dialog_fits_a_small_screen_with_its_buttons_visible()
    {
        var dialog = new DisplaySettingsDialog { DataContext = new DisplaySettingsViewModel() };
        dialog.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.True(dialog.Height <= 600, $"dialog is {dialog.Height} px tall");
        Assert.True(dialog.CanResize);

        foreach (var label in new[] { "OK", "Cancel" })
        {
            var button = dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == label);
            var bottom = button.TranslatePoint(new Point(0, button.Bounds.Height), dialog)!.Value.Y;
            Assert.True(bottom <= dialog.ClientSize.Height, $"{label} ends at {bottom} px, below the {dialog.ClientSize.Height} px window");
        }
        dialog.Close();
    }
}
