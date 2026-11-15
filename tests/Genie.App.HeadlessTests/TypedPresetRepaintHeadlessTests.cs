using System;
using System.Diagnostics;
using System.IO;
using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Genie.App.Controls;
using Genie.App.Highlighting;
using Genie.App.ViewModels;
using Xunit;

namespace Genie.App.HeadlessTests;

/// <summary>
/// #406 review — a typed <c>#preset</c> repaints what paints from presets, as in
/// Genie 4 (Command.cs EventPresetChanged → FormMain.PresetChanged). Before,
/// PresetEngine.Apply raised nothing, so only the pickers repainted the bars.
/// </summary>
[Collection("highlight-statics")]
public class TypedPresetRepaintHeadlessTests
{
    [AvaloniaFact]
    public void Typed_preset_repaints_a_vitals_bar()
    {
        var prevEngine = DefaultHighlights.PresetEngine;
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "genie_typedpreset_" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var vm = new MainWindowViewModel(startup: null, dataDirectoryOverride: dir);
            vm.Command.CommandText = "#echo build";        // builds Core offline, like a first typed command
            vm.Command.SubmitCommand.Execute().Subscribe();
            Assert.True(WaitFor(() => vm.Core is not null));

            var bar = new ProgressBar();
            PresetBar.SetPreset(bar, "health");
            new Window { Content = bar }.Show();
            Dispatcher.UIThread.RunJobs();

            vm.Command.CommandText = "#preset health #123456 #654321";
            vm.Command.SubmitCommand.Execute().Subscribe();

            Assert.True(WaitFor(() => (bar.Foreground as ISolidColorBrush)?.Color == Color.Parse("#123456")),
                "the bar did not repaint after a typed #preset");
            Assert.Equal(Color.Parse("#654321"), (bar.Background as ISolidColorBrush)?.Color);
        }
        finally
        {
            DefaultHighlights.PresetEngine = prevEngine;
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static bool WaitFor(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return true;
            System.Threading.Thread.Sleep(10);
        }
        Dispatcher.UIThread.RunJobs();
        return condition();
    }
}
