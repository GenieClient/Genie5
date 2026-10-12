using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Genie.Core.Mapper;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace Genie.App.ViewModels;

/// <summary>One pickable room in a zone: the text stored in the connection and what the list shows.</summary>
public sealed record RoomChoice(string Value, string Display);

/// <summary>
/// View-model for the Cross-Zone Connections editor dialog. Shows the
/// full list of connections from <c>ZoneConnections.xml</c>; the user
/// can add / edit / delete entries. Save persists back to disk.
///
/// <para>
/// Connections drive the multi-zone pathfinder
/// (<see cref="MultiZonePathfinder"/>). Each row is one transit link
/// between two zones: "board boat at Crossing docks → arrive Throne
/// City pier, 5-10 min wait, no requirements."
/// </para>
///
/// <para>
/// The four endpoint fields are pickers: the zone dropdowns list the zone
/// files in the Maps folder (plus an "Add zone…" entry for a zone that is
/// not there yet), and the room fields suggest the chosen zone's rooms. Both
/// still accept typed text, so a value the lists don't know is never blocked.
/// </para>
/// </summary>
public sealed class ZoneConnectionsViewModel : ReactiveObject
{
    /// <summary>The dropdown entry that opens the inline "add a zone name" row.</summary>
    public const string AddZoneItem = "➕ Add zone…";

    private readonly ZoneConnectionsRepository _repo;
    private readonly string? _mapsDir;
    private readonly Dictionary<string, IReadOnlyList<RoomChoice>> _roomCache =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _syncing;
    private bool _addingForFrom;

    public ObservableCollection<ZoneConnection> Connections { get; } = new();

    /// <summary>"Add zone…" first, then every known zone name.</summary>
    public ObservableCollection<string> ZoneChoices { get; } = new();

    [Reactive] public ZoneConnection? Selected { get; set; }

    [Reactive] public string Status { get; private set; } = "";

    // ── Endpoint pickers (mirror the selected connection) ───────────────────
    [Reactive] public string? FromZoneSel  { get; set; }
    [Reactive] public string? ToZoneSel    { get; set; }
    [Reactive] public string  FromRoomText { get; set; } = "";
    [Reactive] public string  ToRoomText   { get; set; } = "";
    [Reactive] public IReadOnlyList<RoomChoice> FromRooms { get; private set; } = Array.Empty<RoomChoice>();
    [Reactive] public IReadOnlyList<RoomChoice> ToRooms   { get; private set; } = Array.Empty<RoomChoice>();

    // ── Inline "add zone" row ───────────────────────────────────────────────
    [Reactive] public bool   AddingZone   { get; private set; }
    [Reactive] public string NewZoneName  { get; set; } = "";
    [Reactive] public string AddZoneError { get; private set; } = "";

    public ReactiveCommand<Unit, Unit>  AddCommand    { get; }
    public ReactiveCommand<Unit, Unit>  RemoveCommand { get; }
    public ReactiveCommand<Unit, bool>  SaveCommand   { get; }
    public ReactiveCommand<Unit, bool>  CloseCommand  { get; }
    public ReactiveCommand<Unit, Unit>  ConfirmAddZoneCommand { get; }
    public ReactiveCommand<Unit, Unit>  CancelAddZoneCommand  { get; }

    public ZoneConnectionsViewModel(ZoneConnectionsRepository repo, string? mapsDir = null)
    {
        _repo    = repo;
        _mapsDir = mapsDir;
        foreach (var c in _repo.Load()) Connections.Add(c);
        RebuildZoneChoices();

        AddCommand = ReactiveCommand.Create(() =>
        {
            // Insert a stub connection that the user fills in. Default
            // verb suggests boats — the most common use case — but the
            // user can change any field.
            var fresh = new ZoneConnection
            {
                Verb        = "board boat",
                TransitType = "boat",
            };
            Connections.Add(fresh);
            Selected = fresh;
        });

        var canRemove = this.WhenAnyValue(x => x.Selected).Select(s => s is not null);
        RemoveCommand = ReactiveCommand.Create(() =>
        {
            if (Selected is not null) Connections.Remove(Selected);
        }, canRemove);

        SaveCommand = ReactiveCommand.Create(() =>
        {
            try
            {
                _repo.Save(Connections);
                Status = $"Saved {Connections.Count} connection{(Connections.Count == 1 ? "" : "s")}.";
                return true;
            }
            catch (Exception ex)
            {
                Status = $"Save failed: {ex.Message}";
                return false;
            }
        });

        CloseCommand = ReactiveCommand.Create(() => false);

        ConfirmAddZoneCommand = ReactiveCommand.Create(ConfirmAddZone);
        CancelAddZoneCommand  = ReactiveCommand.Create(() => { AddingZone = false; AddZoneError = ""; });

        // Selecting a connection fills the pickers from it.
        this.WhenAnyValue(x => x.Selected).Subscribe(_ => LoadPickersFromSelected());

        // Picker edits write back to the selected connection.
        this.WhenAnyValue(x => x.FromZoneSel).Skip(1).Subscribe(v => OnZonePicked(v, isFrom: true));
        this.WhenAnyValue(x => x.ToZoneSel).Skip(1).Subscribe(v => OnZonePicked(v, isFrom: false));
        this.WhenAnyValue(x => x.FromRoomText).Skip(1).Subscribe(v =>
        {
            if (!_syncing && Selected is not null) Selected.FromRoom = v ?? "";
        });
        this.WhenAnyValue(x => x.ToRoomText).Skip(1).Subscribe(v =>
        {
            if (!_syncing && Selected is not null) Selected.ToRoom = v ?? "";
        });
    }

    // ── Zone list ───────────────────────────────────────────────────────────

    /// <summary>Zone file names (no <c>.xml</c>) in the Maps folder, plus any zone
    /// a saved connection already names so an existing value is always selectable.</summary>
    private void RebuildZoneChoices()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(_mapsDir) && Directory.Exists(_mapsDir))
            foreach (var f in Directory.EnumerateFiles(_mapsDir, "*.xml"))
            {
                var n = Path.GetFileNameWithoutExtension(f);
                if (!n.Equals("ZoneConnections", StringComparison.OrdinalIgnoreCase)) names.Add(n);
            }
        foreach (var c in Connections)
        {
            if (!string.IsNullOrWhiteSpace(c.FromZone)) names.Add(c.FromZone);
            if (!string.IsNullOrWhiteSpace(c.ToZone))   names.Add(c.ToZone);
        }

        ZoneChoices.Clear();
        ZoneChoices.Add(AddZoneItem);
        foreach (var n in names) ZoneChoices.Add(n);
    }

    private void EnsureZoneListed(string zone)
    {
        if (string.IsNullOrWhiteSpace(zone) || zone == AddZoneItem) return;
        if (ZoneChoices.Any(z => z.Equals(zone, StringComparison.OrdinalIgnoreCase))) return;
        // Keep the list sorted after the sentinel at index 0.
        var i = 1;
        while (i < ZoneChoices.Count &&
               string.Compare(ZoneChoices[i], zone, StringComparison.OrdinalIgnoreCase) < 0) i++;
        ZoneChoices.Insert(i, zone);
    }

    private void LoadPickersFromSelected()
    {
        _syncing = true;
        try
        {
            var s = Selected;
            if (s is not null)
            {
                EnsureZoneListed(s.FromZone);
                EnsureZoneListed(s.ToZone);
            }
            FromZoneSel  = string.IsNullOrWhiteSpace(s?.FromZone) ? null : s!.FromZone;
            ToZoneSel    = string.IsNullOrWhiteSpace(s?.ToZone)   ? null : s!.ToZone;
            FromRoomText = s?.FromRoom ?? "";
            ToRoomText   = s?.ToRoom   ?? "";
        }
        finally { _syncing = false; }
        _ = RefreshRoomsAsync(isFrom: true);
        _ = RefreshRoomsAsync(isFrom: false);
    }

    private void OnZonePicked(string? zone, bool isFrom)
    {
        if (_syncing) return;

        if (zone == AddZoneItem)
        {
            // Open the inline row, and put the picker back on the zone it had.
            _addingForFrom = isFrom;
            AddingZone     = true;
            AddZoneError   = "";
            NewZoneName    = "";
            _syncing = true;
            try
            {
                var prior = isFrom ? Selected?.FromZone : Selected?.ToZone;
                var restore = string.IsNullOrWhiteSpace(prior) ? null : prior;
                if (isFrom) FromZoneSel = restore; else ToZoneSel = restore;
            }
            finally { _syncing = false; }
            return;
        }

        if (Selected is not null)
        {
            if (isFrom) Selected.FromZone = zone ?? ""; else Selected.ToZone = zone ?? "";
        }
        _ = RefreshRoomsAsync(isFrom);
    }

    private void ConfirmAddZone()
    {
        var name = (NewZoneName ?? "").Trim();
        if (name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        if (name.Length == 0) { AddZoneError = "Type the zone's file name, like Map150_Fang_Cove."; return; }
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        { AddZoneError = "A zone name can't contain slashes or other file-name symbols."; return; }

        var existing = ZoneChoices.FirstOrDefault(z => z.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) name = existing; else EnsureZoneListed(name);

        AddingZone   = false;
        AddZoneError = "";
        if (_addingForFrom) FromZoneSel = name; else ToZoneSel = name;
    }

    // ── Room suggestions ────────────────────────────────────────────────────

    /// <summary>
    /// Load the rooms of the zone chosen for one end and publish them as
    /// suggestions. A zone with no file in the Maps folder simply has none.
    /// Public so tests can await it; the dialog fires it and moves on.
    /// </summary>
    public async Task RefreshRoomsAsync(bool isFrom)
    {
        var zone = isFrom ? FromZoneSel : ToZoneSel;
        IReadOnlyList<RoomChoice> rooms = Array.Empty<RoomChoice>();
        if (!string.IsNullOrWhiteSpace(zone) && zone != AddZoneItem && !string.IsNullOrEmpty(_mapsDir))
        {
            if (!_roomCache.TryGetValue(zone, out var cached))
            {
                var path = Path.Combine(_mapsDir, zone + ".xml");
                cached = await Task.Run(() => LoadRooms(path));
                _roomCache[zone] = cached;
            }
            rooms = cached;
        }

        // Ignore a result that is stale because the user picked another zone meanwhile.
        if ((isFrom ? FromZoneSel : ToZoneSel) != zone) return;
        if (isFrom) FromRooms = rooms; else ToRooms = rooms;
    }

    private static IReadOnlyList<RoomChoice> LoadRooms(string path)
    {
        var zone = new MapZoneRepository().Load(path);
        if (zone is null) return Array.Empty<RoomChoice>();
        return zone.Nodes.Values
            .OrderBy(n => n.Id)
            .Select(n =>
            {
                // A server room id survives map regeneration, so prefer it when the map has one.
                var value = !string.IsNullOrWhiteSpace(n.ServerRoomId) ? "#" + n.ServerRoomId : n.Id.ToString();
                var srv   = !string.IsNullOrWhiteSpace(n.ServerRoomId) ? $"  ·  #{n.ServerRoomId}" : "";
                return new RoomChoice(value, $"{n.Id}  ·  {n.Title}{srv}");
            })
            .ToList();
    }
}
