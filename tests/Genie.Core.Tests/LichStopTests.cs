using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Genie.Core.Connection;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// <see cref="LichLauncher.TryStop"/> — ownership cleanup for Genie-started Lich.
/// </summary>
public class LichStopTests
{
    [Fact]
    public void TryStop_treats_already_exited_pid_as_success()
    {
        // Spawn a process that exits immediately, wait for it, then TryStop.
        using var proc = StartShortLivedProcess();
        Assert.NotNull(proc);
        proc!.WaitForExit(5_000);
        var pid = proc.Id;

        var ok = LichLauncher.TryStop(pid, out var message);

        Assert.True(ok);
        Assert.Contains("already exited", message);
    }

    [Fact]
    public void TryStop_kills_a_running_process()
    {
        using var proc = StartLongLivedProcess();
        Assert.NotNull(proc);
        Assert.False(proc!.HasExited);
        var pid = proc.Id;

        var ok = LichLauncher.TryStop(pid, out var message);

        Assert.True(ok);
        Assert.Contains("stopped", message);
        // Give the OS a moment; HasExited can lag a tick after Kill.
        proc.WaitForExit(5_000);
        Assert.True(proc.HasExited);
    }

    [Fact]
    public void TryStop_rejects_non_positive_pid()
    {
        Assert.False(LichLauncher.TryStop(0, out var message));
        Assert.Equal(string.Empty, message);
        Assert.False(LichLauncher.TryStop(-1, out _));
    }

    [Fact]
    public void Launched_result_can_carry_process_id_and_start_time()
    {
        var start = DateTime.UtcNow;
        var result = new LichLaunchResult(LichLaunchOutcome.Launched, "[lich] up", 42_424, start);
        Assert.Equal(42_424, result.ProcessId);
        Assert.Equal(start, result.ProcessStartTimeUtc);

        var attach = new LichLaunchResult(LichLaunchOutcome.AlreadyRunning, "[lich] attaching");
        Assert.Null(attach.ProcessId);
        Assert.Null(attach.ProcessStartTimeUtc);
    }

    private static Process? StartShortLivedProcess()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return Process.Start(new ProcessStartInfo
            {
                FileName               = "cmd.exe",
                Arguments              = "/c exit 0",
                UseShellExecute        = false,
                CreateNoWindow         = true,
            });

        return Process.Start(new ProcessStartInfo
        {
            FileName        = "/bin/sh",
            Arguments       = "-c exit 0",
            UseShellExecute = false,
            CreateNoWindow  = true,
        });
    }

    private static Process? StartLongLivedProcess()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return Process.Start(new ProcessStartInfo
            {
                FileName               = "cmd.exe",
                Arguments              = "/c ping -n 60 127.0.0.1 >NUL",
                UseShellExecute        = false,
                CreateNoWindow         = true,
            });

        return Process.Start(new ProcessStartInfo
        {
            FileName        = "/bin/sleep",
            Arguments       = "60",
            UseShellExecute = false,
            CreateNoWindow  = true,
        });
    }
}
