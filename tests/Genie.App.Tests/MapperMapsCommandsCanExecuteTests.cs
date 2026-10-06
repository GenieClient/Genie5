using System;
using System.IO;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Genie.App.ViewModels;
using Genie.Core;
using Xunit;

namespace Genie.App.Tests;

/// <summary>
/// Update / Repair Maps were greyed out for the whole session. Their CanExecute
/// sampled <c>_zoneRepo is not null</c> only when IsUpdating or MapsDirectory
/// changed, but the Maps directory is set at startup and the repo arrives later
/// in <see cref="MapperViewModel.Attach"/>, so the last evaluation saw a null
/// repo and nothing re-ran it. Attaching must re-enable both commands.
/// </summary>
public class MapperMapsCommandsCanExecuteTests
{
    private static bool Latest(IObservable<bool> canExecute)
    {
        var value = false;
        using (canExecute.Subscribe(v => value = v)) { }
        return value;
    }

    [Fact]
    public async Task Update_and_repair_enable_once_the_core_attaches_after_the_maps_dir_was_set()
    {
        var dir = Path.Combine(Path.GetTempPath(), "genie_app_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        await using var core = new GenieCore(dataDirectoryOverride: dir, gameThreadOverride: false);
        try
        {
            var mapper = new MapperViewModel { MapsDirectory = dir };   // startup order: dir first

            Assert.False(Latest(mapper.UpdateMapsCommand.CanExecute));
            Assert.False(Latest(mapper.RepairMapsCommand.CanExecute));

            mapper.Attach(core);

            Assert.True(Latest(mapper.UpdateMapsCommand.CanExecute));
            Assert.True(Latest(mapper.RepairMapsCommand.CanExecute));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
