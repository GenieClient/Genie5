using Genie.Core.Events;

namespace Genie.Core.Mapper;

/// <summary>
/// Adapts our <see cref="Models.GameState"/> + the parser's
/// <see cref="IObservable{GameEvent}"/> stream to the <see cref="IMapperGameState"/>
/// interface that <see cref="AutoMapperEngine"/> consumes.
///
/// Field mapping:
/// <list type="bullet">
///   <item><c>RoomTitle</c>        ← <c>GameState.Room.Title</c></item>
///   <item><c>RoomDescription</c>  ← <c>GameState.Room.Description</c></item>
///   <item><c>Exits</c>            ← <c>GameState.Room.CompassExits</c> split on whitespace</item>
///   <item><c>ServerRoomId</c>     ← <c>GameState.Room.RoomId</c> (int) converted to string;
///                                   empty when unknown</item>
/// </list>
/// <see cref="StateChanged"/> fires whenever any of the underlying game events
/// that touch the room arrives (<see cref="NavEvent"/>, <see cref="CompassEvent"/>,
/// or a "room title" / "room desc" <see cref="ComponentEvent"/>). The engine
/// internally rate-limits by tracking title + exit-set deltas, so over-firing
/// is harmless.
/// </summary>
public sealed class MapperGameStateAdapter : IMapperGameState, IDisposable
{
    private static readonly char[] WhitespaceSeparators = { ' ', '\t', '\r', '\n' };

    private readonly Models.GameState _state;
    private readonly IDisposable      _subscription;

    /// <summary>
    /// Room title, normalised for engine matching. DR wraps the live room
    /// name in square brackets — both in the text stream ("[Garden Rooftop,
    /// Medical Pavilion]") and as the content of the <c>&lt;component
    /// id="room name"&gt;</c> tag — but the Genie 4 XML maps store titles
    /// bracket-free (<c>name="Garden Rooftop, Medical Pavilion"</c>). Without
    /// stripping here, EVERY matching tier (server-id, fingerprint, desc,
    /// title+exits-overlap) fails because they all use
    /// <c>Title.Equals(...)</c>. Display surfaces still see the bracketed
    /// form via <see cref="Models.RoomState.Title"/>.
    /// </summary>
    public string RoomTitle
    {
        get
        {
            var t = _state.Room.Title;
            if (string.IsNullOrEmpty(t)) return string.Empty;
            if (t.Length >= 2 && t[0] == '[' && t[^1] == ']') return t[1..^1].Trim();
            return t.Trim();
        }
    }

    public string RoomDescription => _state.Room.Description;

    public IReadOnlyCollection<string> Exits =>
        string.IsNullOrWhiteSpace(_state.Room.CompassExits)
            ? Array.Empty<string>()
            : _state.Room.CompassExits.Split(WhitespaceSeparators, StringSplitOptions.RemoveEmptyEntries);

    public string ServerRoomId =>
        _state.Room.RoomId > 0 ? _state.Room.RoomId.ToString() : string.Empty;

    public event Action? StateChanged;

    /// <summary>
    /// Set when a room-defining event (nav / compass / room title / room desc)
    /// arrives; cleared when we flush on the next prompt. See the constructor
    /// for why we coalesce rather than fire per-event.
    /// </summary>
    private bool _roomDirty;

    public MapperGameStateAdapter(Models.GameState state, IObservable<GameEvent> events)
    {
        _state = state;

        // COALESCE room updates to the prompt that ends the server turn, rather
        // than firing per room-component event. DR streams a room as a sequence
        // — title → desc → objs → exits/<compass> — and the TITLE arrives before
        // THIS room's compass. Firing on each event made the engine fingerprint
        // the new title against the PREVIOUS room's exits (and against the
        // transient empty-compass mid-parse), producing wrong fingerprints that
        // miss every zone — then the one coherent (title + own compass) snapshot
        // arrives too late, after the misses have already poisoned the auto-load
        // dedup. DR sends a <prompt> at the end of every turn, AFTER the full
        // room block, so by then Title / Exits / RoomId are all the new room's:
        // a single flush there gives the engine one coherent snapshot.
        //
        // Idle/keepalive prompts (no preceding room change) leave _roomDirty
        // false and are ignored, so this doesn't add spurious placements.
        _subscription = events.Subscribe(e =>
        {
            switch (e)
            {
                case NavEvent:
                case CompassEvent:
                    _roomDirty = true;
                    break;
                case ComponentEvent ce
                    when ce.ComponentId.Equals("room title", StringComparison.OrdinalIgnoreCase)
                      || ce.ComponentId.Equals("room desc",  StringComparison.OrdinalIgnoreCase):
                    _roomDirty = true;
                    break;
                case PromptEvent when _roomDirty:
                    _roomDirty = false;
                    StateChanged?.Invoke();
                    break;
            }
        });
    }

    public void Dispose() => _subscription.Dispose();
}
