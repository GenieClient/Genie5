using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Genie.App.ViewModels;
using Genie.Core;
using Genie.Core.Mapper;
using Xunit;

namespace Genie.App.HeadlessTests;

/// <summary>
/// The mapper's label editing (Genie 4 LabelDetails: New / Apply / Remove) and
/// the write paths for room tags and notes (<c>#mapper tag</c>, <c>#mapper
/// note</c>, the Details "Tags" field). Before these, labels were render-only
/// and nothing in the app could write a tag, so <c>#goto @tag</c> was
/// unreachable in practice (the smoke sheet marked room tags "not performable").
/// </summary>
public class MapperLabelTagEditingHeadlessTests : IAsyncLifetime
{
    private string          _dir  = "";
    private GenieCore       _core = null!;
    private MapperViewModel _vm   = null!;
    private FakeState       _state = null!;

    public Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "genie_labeltag_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_core is not null) await _core.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed class FakeState : IMapperGameState
    {
        public string RoomTitle       { get; private set; } = "";
        public string RoomDescription { get; private set; } = "";
        public string ServerRoomId    { get; private set; } = "";
        public IReadOnlyCollection<string> Exits { get; private set; } = Array.Empty<string>();
        public event Action? StateChanged;
        public void EnterRoom(string title, string description, IReadOnlyCollection<string> exits)
        {
            RoomTitle = title; RoomDescription = description; Exits = exits;
            StateChanged?.Invoke();
        }
    }

    private static void Pump() => Dispatcher.UIThread.RunJobs();

    /// <summary>Two-room zone; the character is placed in room 1 by feeding the
    /// engine a matching room through a fake game state.</summary>
    private MapZone Build()
    {
        _core  = new GenieCore(dataDirectoryOverride: _dir, gameThreadOverride: false);
        _vm    = new MapperViewModel();
        _vm.Attach(_core);

        var zone = new MapZone { Name = "Testville", Genie4Id = "7" };
        var r1 = new MapNode { Id = 1, Title = "Town Square", Description = "A square.", PixelX = 100, PixelY = 100 };
        var r2 = new MapNode { Id = 2, Title = "Bank Lobby",  Description = "A lobby.",  PixelX = 120, PixelY = 100 };
        r1.Exits.Add(new MapExit { Direction = Direction.East, MoveCommand = "east", DestinationId = 2 });
        r2.Exits.Add(new MapExit { Direction = Direction.West, MoveCommand = "west", DestinationId = 1 });
        zone.Nodes[1] = r1; zone.Nodes[2] = r2;

        _core.AutoMapper.LoadZone(zone);
        _state = new FakeState();
        _core.AutoMapper.Attach(_state);
        _state.EnterRoom("Town Square", "A square.", new[] { "east" });
        Pump();
        Assert.Equal(1, _core.AutoMapper.CurrentNode?.Id);
        return zone;
    }

    // ── Tags ──────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void Mapper_tag_add_tags_the_current_room_and_makes_it_routable_at_once()
    {
        var zone = Build();
        Assert.Null(_core.AutoMapper.FindNearestByTag(zone.Nodes[2], "square"));

        var msg = _vm.TagCommand("add", "square");
        Assert.Contains("Tagged room 1", msg);
        Assert.Contains("Save to persist", msg);                       // no backing file → dirty, not written
        Assert.True(_vm.IsZoneDirty);
        Assert.Contains("square", zone.Nodes[1].Tags);
        Assert.Equal(1, _core.AutoMapper.FindNearestByTag(zone.Nodes[2], "square")!.Id);

        // Idempotent, and a leading '@' is tolerated.
        Assert.Contains("already has @square", _vm.TagCommand("add", "@square"));
        Assert.Single(zone.Nodes[1].Tags);

        Assert.Contains("@square", _vm.TagCommand("list", ""));
        Assert.Contains("@square (1)", _vm.TagCommand("zone", ""));
    }

    [AvaloniaFact]
    public void Mapper_tag_remove_unroutes_and_a_pipe_is_refused()
    {
        var zone = Build();
        _vm.TagCommand("add", "square");
        Assert.Contains("Removed @square", _vm.TagCommand("remove", "SQUARE"), StringComparison.OrdinalIgnoreCase);   // case-insensitive
        Assert.Empty(zone.Nodes[1].Tags);
        Assert.Null(_core.AutoMapper.FindNearestByTag(zone.Nodes[2], "square"));
        Assert.Contains("has no tag", _vm.TagCommand("remove", "square"));
        Assert.Contains("can't contain '|'", _vm.TagCommand("add", "a|b"));
    }

    [AvaloniaFact]
    public void Details_panel_tags_field_round_trips_through_Apply()
    {
        var zone = Build();
        _vm.EditMode = true;
        _vm.SelectedNode = zone.Nodes[2];
        Assert.Equal("", _vm.SelNodeTags);

        _vm.SelNodeTags = "bank, Teller|bank|@vault";     // commas or pipes, dupes, a stray '@'
        _vm.ApplyNodePropsCommand.Execute().Subscribe();
        Pump();

        Assert.Equal(new[] { "bank", "Teller", "vault" }, zone.Nodes[2].Tags);
        Assert.Equal(2, _core.AutoMapper.FindNearestByTag(zone.Nodes[1], "vault")!.Id);

        // Re-selecting mirrors the stored tags back, '|'-joined.
        _vm.SelectedNode = null;
        _vm.SelectedNode = zone.Nodes[2];
        Assert.Equal("bank|Teller|vault", _vm.SelNodeTags);

        // And the exporter writes them (sorted) so the file carries them.
        Assert.Contains("tags=\"bank|Teller|vault\"", Genie4MapExporter.Serialize(zone));
    }

    // ── Notes (Genie 4 #automapper note) ───────────────────────────────────

    [AvaloniaFact]
    public void Mapper_note_appends_a_goto_label_and_bare_note_lists_them()
    {
        var zone = Build();
        Assert.Contains("No notes", _vm.NoteCommand(""));

        Assert.Contains("Note added for room 1: Square", _vm.NoteCommand("Square"));
        Assert.Equal("Square", zone.Nodes[1].Notes);
        _vm.NoteCommand("Fountain Steps");
        Assert.Equal("Square|Fountain Steps", zone.Nodes[1].Notes);   // '|'-separated, Genie 4 style

        var list = _vm.NoteCommand("");
        Assert.Contains("Square|Fountain Steps (1)", list);
    }

    // ── Labels ────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void Toolbar_add_label_places_it_beside_the_current_room_and_selects_it()
    {
        var zone = Build();
        _vm.EditMode = true;
        _vm.AddLabelCommand.Execute(null).Subscribe();
        Pump();

        var label = Assert.Single(zone.Labels);
        Assert.Equal("New Label", label.Text);
        Assert.Same(label, _vm.SelectedLabel);
        Assert.Null(_vm.SelectedNode);
        Assert.True(_vm.IsZoneDirty);
        // Beside room 1 (100,100): one snap step right and up, in map pixels.
        Assert.Equal("110, 90, 0", _vm.SelLabelPosition);
        Assert.Equal("New Label", _vm.SelLabelText);
    }

    [AvaloniaFact]
    public void Context_menu_add_label_here_uses_the_supplied_position()
    {
        var zone = Build();
        var at = new MapLabel { X = 180 / 20.0, Y = -36 / 20.0, Z = 0 };
        _vm.AddLabelCommand.Execute(at).Subscribe();
        Pump();
        Assert.Same(at, Assert.Single(zone.Labels));
        Assert.Equal("New Label", at.Text);
        Assert.Equal("180, -36, 0", _vm.SelLabelPosition);
    }

    [AvaloniaFact]
    public void Apply_writes_text_and_position_back_and_rejects_bad_input()
    {
        var zone = Build();
        _vm.AddLabelCommand.Execute(null).Subscribe();
        Pump();
        var label = zone.Labels[0];

        _vm.SelLabelText     = "Town Hall";
        _vm.SelLabelPosition = "180, -36, 1";
        _vm.ApplyLabelPropsCommand.Execute().Subscribe();
        Assert.Equal("Town Hall", label.Text);
        Assert.Equal(180 / 20.0, label.X);
        Assert.Equal(-36 / 20.0, label.Y);
        Assert.Equal(1, label.Z);

        // Bad position → rejected, label untouched, status explains.
        _vm.SelLabelPosition = "somewhere";
        _vm.ApplyLabelPropsCommand.Execute().Subscribe();
        Assert.Equal(180 / 20.0, label.X);
        Assert.Contains("x, y, z", _vm.LoadStatus);

        // Empty text → rejected (Remove is the way to delete).
        _vm.SelLabelText = "   ";
        _vm.SelLabelPosition = "0, 0";
        _vm.ApplyLabelPropsCommand.Execute().Subscribe();
        Assert.Equal("Town Hall", label.Text);
        Assert.Contains("needs some text", _vm.LoadStatus);
    }

    [AvaloniaFact]
    public void Remove_selected_deletes_a_selected_label_and_labels_survive_save()
    {
        var zone = Build();
        _vm.AddLabelCommand.Execute(null).Subscribe();
        _vm.AddLabelCommand.Execute(new MapLabel { Text = "Keep Me", X = 1, Y = 1, Z = 0 }).Subscribe();
        Pump();
        Assert.Equal(2, zone.Labels.Count);

        _vm.SelectedLabel = zone.Labels[0];
        _vm.RemoveSelectedCommand.Execute().Subscribe();
        Assert.Single(zone.Labels);
        Assert.Null(_vm.SelectedLabel);
        Assert.Equal("Keep Me", zone.Labels[0].Text);

        // The exporter writes the survivor, at its pixel position.
        var xml = Genie4MapExporter.Serialize(zone);
        Assert.Contains("<label text=\"Keep Me\">", xml);
        Assert.Contains("<position x=\"20\" y=\"20\" z=\"0\" />", xml);
    }

    [AvaloniaFact]
    public void Label_moved_marks_dirty_and_refreshes_the_position_field()
    {
        var zone = Build();
        _vm.AddLabelCommand.Execute(null).Subscribe();
        Pump();
        var label = zone.Labels[0];

        // Save to clear the dirty flag (the zone gets a file under the temp
        // Maps directory), so the move below is what dirties it.
        _vm.MapsDirectory = _dir;
        _vm.SaveMapCommand.Execute().Subscribe();
        Pump();
        Assert.False(_vm.IsZoneDirty);
        Assert.Contains("<label text=\"New Label\">", File.ReadAllText(Path.Combine(_dir, "Testville.xml")));

        // What the canvas does on drag release.
        label.X = 300 / 20.0; label.Y = 40 / 20.0;
        _vm.LabelMovedCommand.Execute(label).Subscribe();
        Assert.True(_vm.IsZoneDirty);
        Assert.Equal("300, 40, 0", _vm.SelLabelPosition);
    }
}
