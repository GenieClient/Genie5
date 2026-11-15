using System;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace Genie.App.HeadlessTests;

/// <summary>
/// Public #398 / #399: an exception nothing caught on the UI thread closed the
/// app. With the guard installed, a stray one is logged and the dispatcher
/// keeps running; a burst still gets through.
/// </summary>
public class UiCrashGuardHeadlessTests
{
    // The tests keep their own record; the real crash log is not theirs to write.
    private readonly System.Collections.Generic.List<string> _logged = new();
    private void Log(string source, Exception ex) => _logged.Add(source);

    [AvaloniaFact]
    public void A_stray_exception_on_the_ui_thread_no_longer_stops_the_dispatcher()
    {
        var handler = Program.InstallUiCrashGuard(Dispatcher.UIThread, new UiCrashGuard(), Log);
        try
        {
            var ranAfter = false;
            Dispatcher.UIThread.Post(() => throw new InvalidOperationException("Collection was modified"));
            Dispatcher.UIThread.Post(() => ranAfter = true);

            Dispatcher.UIThread.RunJobs();   // threw before the guard

            Assert.True(ranAfter);
            Assert.Equal(new[] { "UiThread.Survived" }, _logged);
        }
        finally { Dispatcher.UIThread.UnhandledException -= handler; }
    }

    [AvaloniaFact]
    public void A_burst_of_exceptions_still_gets_through()
    {
        var handler = Program.InstallUiCrashGuard(Dispatcher.UIThread, new UiCrashGuard(), Log);
        try
        {
            for (var i = 0; i < UiCrashGuard.MaxPerWindow; i++)
            {
                Dispatcher.UIThread.Post(() => throw new InvalidOperationException("stray"));
                Dispatcher.UIThread.RunJobs();
            }

            Dispatcher.UIThread.Post(() => throw new InvalidOperationException("burst"));
            Assert.Throws<InvalidOperationException>(() => Dispatcher.UIThread.RunJobs());
            Assert.Equal("UiThread.Burst", _logged[^1]);
        }
        finally { Dispatcher.UIThread.UnhandledException -= handler; }
    }
}
