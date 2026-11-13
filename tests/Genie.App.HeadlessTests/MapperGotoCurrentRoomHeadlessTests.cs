using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Genie.App.ViewModels;
using Genie.Core;
using Genie.Core.Mapper;
using Xunit;

namespace Genie.App.HeadlessTests;

/// <summary>
/// #427: a <c>#goto</c> to the room the character is already in must answer
/// <c>YOU HAVE ARRIVED!</c>, as the built-in walker does, also when
/// automapper.cmd is installed. travel.cmd / uber.cmd <c>matchwait</c> on that
/// line after every <c>#goto</c>; silence read as a failed move and set off
/// retries, <c>#mapper reset</c> and random moves.
/// </summary>
public class MapperGotoCurrentRoomHeadlessTests : IAsyncLifetime
{
    private string          _dir  = "";
    private GenieCore       _core = null!;
    private MapperViewModel _vm   = null!;

    private sealed class FakeGameState : IMapperGameState
    {
        public string RoomTitle { get; set; } = "";
        public string RoomDescription { get; set; } = "";
        public IReadOnlyCollection<string> Exits { get; set; } = Array.Empty<string>();
        public string ServerRoomId { get; set; } = "";
        public event Action? StateChanged;
        public void Fire() => StateChanged?.Invoke();
    }

    public Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "genie_gotohere_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_core is not null) await _core.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static void Pump() => Dispatcher.UIThread.RunJobs();

    private void PumpScripts()
    {
        for (int i = 0; i < 50; i++) { Pump(); _core.Scripts.Tick(); }
    }

    [AvaloniaFact]
    public void Goto_to_the_current_room_resumes_a_script_waiting_for_the_arrival()
    {
        _core = new GenieCore(dataDirectoryOverride: _dir, gameThreadOverride: false);
        _vm   = new MapperViewModel();
        _vm.Attach(_core);

        // automapper.cmd installed (the hand-off case from the report), and a
        // script shaped like uber.cmd's AUTOMOVE: #goto, then wait for the result.
        var scripts = _core.Config.ScriptDir;
        Directory.CreateDirectory(scripts);
        File.WriteAllText(Path.Combine(scripts, "automapper.cmd"), "echo automapper\n");
        File.WriteAllText(Path.Combine(scripts, "mover.cmd"),
            "matchre ARRIVED ^YOU HAVE ARRIVED(?:\\!)?\n" +
            "matchre BAIL ^DESTINATION NOT FOUND\n" +
            "matchwait 30\n" +
            "var result timeout\n" +
            "goto HOLD\n" +
            "BAIL:\n" +
            "var result notfound\n" +
            "goto HOLD\n" +
            "ARRIVED:\n" +
            "var result arrived\n" +
            "HOLD:\n" +
            "pause 60\n");

        // Standing in the fur shop, whose note is the #goto target.
        var zone = new MapZone { Name = "Fang Cove", Genie4Id = "150" };
        zone.Nodes[137] = new MapNode { Id = 137, Title = "Gorund's Furs", Notes = "bundle|furs" };
        zone.Nodes[137].Exits.Add(new MapExit { Direction = Direction.West, MoveCommand = "west" });
        var state = new FakeGameState { RoomTitle = "Gorund's Furs", Exits = new[] { "w" } };
        _core.AutoMapper.Attach(state);
        _core.AutoMapper.LoadZone(zone);
        Pump();
        Assert.Equal(137, _core.AutoMapper.CurrentNode?.Id);

        Assert.True(_core.Scripts.TryStart("mover", Array.Empty<string>()));
        PumpScripts();                       // the script is now in its matchwait
        var mover = Assert.Single(_core.Scripts.Instances, i => i.Name == "mover");
        Assert.True(mover.InMatchWait);

        // The script's `#goto bundle` (MainWindowViewModel routes it here).
        _vm.GotoByName("bundle");
        PumpScripts();

        Assert.Equal("arrived", mover.Vars.TryGetValue("result", out var r) ? r : null);
        Assert.Equal("Already here.", _vm.LoadStatus);
    }
}
