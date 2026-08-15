using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Linq;
using Genie.Core;
using Genie.Core.Events;
using Genie.Core.Highlights;
using Genie.Core.Layout;
using ReactiveUI;

namespace Genie.App.ViewModels;

public class StreamTabsViewModel : ReactiveObject
{
    public StreamBuffer Logons   { get; } = new("Logons");
    public StreamBuffer Talk     { get; } = new("Talk");
    public StreamBuffer Whispers { get; } = new("Whispers");
    public StreamBuffer Thoughts { get; } = new("Thoughts");
    public StreamBuffer Combat   { get; } = new("Combat");

    /// <summary>Familiar / creature-watching feed — the server's
    /// <c>familiar</c> stream (declared <c>styleIfClosed="watching"</c>).</summary>
    public StreamBuffer Familiar { get; } = new("Familiar");

    /// <summary>Death log — the server's <c>death</c> stream
    /// ("* X was just struck down!"), declared with <c>timestamp="on"</c>.
    /// Server title is "Deaths"; the buffer/tool id stays <c>death</c> to match
    /// the stream id the parser emits.</summary>
    public StreamBuffer Death    { get; } = new("Death");

    /// <summary>Assess feed — the server's <c>assess</c> stream
    /// (declared <c>ifClosed="main"</c>).</summary>
    public StreamBuffer Assess   { get; } = new("Assess");

    /// <summary>Atmospherics / ambient feed — the server's <c>atmospherics</c>
    /// stream (weather + ambient room flavour). Genie 4 "Atmo window" parity
    /// (#85); hidden by default, re-open via Window → Atmospherics.</summary>
    public StreamBuffer Atmospherics { get; } = new("Atmospherics");

    /// <summary>Out-of-character chat — the server's <c>ooc</c> stream, declared
    /// <c>&lt;streamWindow id='ooc' title='OOC' … timestamp='on' ifClosed=''/&gt;</c>
    /// (public #260). Its own system, distinct from Whispers and from Gweth /
    /// Thoughts, even though DR renders an <c>OOC:</c> message in whisper form.
    /// <para>
    /// DR sends each OOC line THREE times — on <c>whispers</c>, again here, and
    /// a third time bare on <c>main</c> (public #256). The bare copy IS the main
    /// -window rendering, which is why this window ships with
    /// <c>EchoToMain</c> off and <c>IfClosed = ""</c> (drop) — DR's own declared
    /// values. Turning the echo on puts every OOC line in Main twice.
    /// </para></summary>
    public StreamBuffer Ooc      { get; } = new("OOC");

    /// <summary>Consolidated conversation log — mirrors the speech streams
    /// (talk / whispers), Genie 4 "Log" window parity. Also an <c>#echo &gt;log</c>
    /// target (wired in MainWindowViewModel).</summary>
    public StreamBuffer Log      { get; } = new("Log");

    /// <summary>Item / loot log. Fed by the <c>itemLog</c> server stream and by
    /// <c>#echo &gt;itemlog</c> from scripts.</summary>
    public StreamBuffer ItemLog  { get; } = new("ItemLog");

    public IReadOnlyList<StreamBuffer> All =>
        [Logons, Talk, Whispers, Thoughts, Combat, Familiar, Death, Assess, Atmospherics, Ooc, Log, ItemLog];

    /// <summary>Main game-window sink, handed in by <see cref="Attach"/> so a
    /// stream with its <c>EchoToMain</c> toggle on can also post into Main.</summary>
    private GameTextViewModel? _main;

    /// <summary>
    /// Is this stream's dock panel currently open? Supplied by
    /// <see cref="MainWindowViewModel"/> (which owns the per-tool visibility
    /// flags) so the closed-panel fallback can be decided here, alongside the
    /// <c>EchoToMain</c> echo, rather than from a second subscription racing
    /// over the same event. Absent (or unknown stream) ⇒ treated as open, i.e.
    /// no fallback.
    /// </summary>
    private Func<string, bool>? _isPanelVisible;

    /// <summary>Live per-window settings store, handed in by <see cref="Attach"/>
    /// so <see cref="RouteToMain"/> can honour <see cref="WindowSettings.IfClosed"/>
    /// (public #211) — resolving a closed stream's redirect target and following
    /// the chain when that target is itself closed. Absent ⇒ the pre-#211
    /// behaviour (closed panel → Main).</summary>
    private WindowSettingsStore? _store;

    public void Attach(GenieCore core,
                       GameTextViewModel? main = null,
                       Func<string, bool>? isPanelVisible = null,
                       WindowSettingsStore? store = null)
    {
        _main           = main;
        _isPanelVisible = isPanelVisible;
        _store          = store;

        // Hand each buffer the live Names engine so the per-window "Name List
        // Only" right-click toggle can filter to lines mentioning a tracked
        // name. NameHighlights survives reconnect (persistent core), so this is
        // a one-time wire-up.
        foreach (var buf in All)
            buf.Names = core.NameHighlights;

        core.GameEvents.OfType<TextEvent>()
            .Where(e => e.Stream != "main")
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(e =>
            {
                var buf = e.Stream switch
                {
                    "logons"               => Logons,
                    "talk"                 => Talk,
                    "whispers"             => Whispers,
                    "thoughts"             => Thoughts,
                    "combat"               => Combat,
                    "familiar"             => Familiar,
                    "death"                => Death,
                    "assess"               => Assess,
                    "atmospherics"         => Atmospherics,
                    "ooc"                  => Ooc,
                    "itemlog" or "itemLog" => ItemLog,
                    // No buffer. Two very different reasons, neither of which
                    // may be "route it to Main" — see public #260:
                    //   • consumed elsewhere — `inv` (InventoryViewModel),
                    //     `percWindow` (SpellTimerExtension), `room`,
                    //     `experience`. Echoing these into Main would dump
                    //     inventory and room descriptions over the game text.
                    //   • declared by DR but not built yet — `conversation`,
                    //     `group`. These DO silently vanish today; harmless
                    //     only because DR also sends a bare `main` copy.
                    _                      => null
                };
                // #187: pass the parser's span metadata (bold / link / preset)
                // through so the stream panel renders monster-bold, clickable
                // links and preset colours just like the main window does.
                buf?.Add(e.Text, e.BoldSpans, e.Links, e.PresetSpans);

                if (buf is not null)
                    RouteToMain(buf, e);

                // The Log window is a consolidated conversation feed: mirror
                // the speech streams into it (matches the Genie 4 / dylb0t
                // prototype "Log" window).
                if (e.Stream is "talk" or "whispers")
                    Log.Add(e.Text, e.BoldSpans, e.Links, e.PresetSpans);
            });
    }

    /// <summary>
    /// Decide whether a side-stream line also lands in the main game window,
    /// and in which form. The two routes are mutually exclusive — a line is
    /// mirrored into Main at most once:
    /// <list type="bullet">
    /// <item><b>EchoToMain on</b> (the default) — Genie 4's per-stream "also
    /// show in Main". Echoed plain, no prefix, whether the stream's own panel
    /// is open or closed.</item>
    /// <item><b>EchoToMain off, panel closed</b> — the visibility fallback, so
    /// text isn't silently lost while its window is hidden. Prefixed
    /// <c>[stream]</c> to mark where it came from.</item>
    /// <item><b>EchoToMain off, panel open</b> — nothing; the panel has it.</item>
    /// </list>
    /// <para>
    /// Both routes are settled here, from the one subscription that already
    /// resolved the buffer, so they cannot both fire for the same event — the
    /// bug that showed up as every combat line appearing twice in Main (once
    /// plain, once <c>[combat] …</c>) the moment the Combat panel was closed.
    /// Reading <c>buf.Settings</c> rather than the settings store also keeps
    /// the decision on the same instance the echo uses; a buffer with no
    /// settings yet (pre dock-build) reads as "not echoing" and falls through
    /// to the fallback, so a line can never vanish.
    /// </para>
    /// <para>
    /// Where a closed panel's text goes is resolved from
    /// <see cref="WindowSettings.IfClosed"/> via <see cref="IfClosedResolver"/>
    /// (public #211): a redirect can name another stream window (talk → log,
    /// and so on), the resolver follows the chain when that target is itself
    /// closed, and an unknown target falls back to Main rather than dropping.
    /// The sentinels are <c>null</c> = Main (the default) and <c>""</c> = drop.
    /// With no store wired (pre dock-build) this degrades to the historical
    /// closed-panel → Main behaviour, so a line can never vanish.
    /// </para>
    /// </summary>
    private void RouteToMain(StreamBuffer buf, TextEvent e)
    {
        // buf.Settings is the same WindowSettings instance the Layout tab
        // mutates, so the toggle takes effect live with no re-subscribe —
        // exactly like the Timestamp / NameListOnly toggles. Carry the spans so
        // the echoed combat hit is gold in Main too (#187).
        if (buf.Settings?.EchoToMain == true)
        {
            _main?.EchoStreamToMain(e.Text, e.BoldSpans, e.Links, e.PresetSpans);
            return;
        }

        // EchoToMain off: the stream shows only in its own panel. If that panel
        // is open (or visibility is unknown), there's nothing more to do.
        if (_isPanelVisible?.Invoke(e.Stream) != false)
            return;

        // Panel closed → honour IfClosed. Without a store, keep the old default.
        if (_store is null)
        {
            _main?.AddStreamLine(e.Stream, e.Text);
            return;
        }

        var decision = IfClosedResolver.Resolve(
            e.Stream, _store, id => _isPanelVisible?.Invoke(id) == true);

        switch (decision.Kind)
        {
            case IfClosedSinkKind.Drop:
                return;
            case IfClosedSinkKind.Stream when TryGetBuffer(decision.StreamId!) is { } sink:
                // talk/whispers are already unconditionally mirrored into Log
                // above (the "Log window" consolidated-feed behaviour) — their
                // default IfClosed target is also "log" (WindowSettingsStore),
                // so without this guard a closed Talk/Whispers panel with
                // EchoToMain off double-added every line into Log.
                if (decision.StreamId!.Equals("log", StringComparison.OrdinalIgnoreCase) &&
                    e.Stream is "talk" or "whispers")
                    return;
                sink.Add(e.Text, e.BoldSpans, e.Links, e.PresetSpans);
                return;
            default:
                // Main, or a stream target with no backing buffer → never lose it.
                _main?.AddStreamLine(e.Stream, e.Text);
                return;
        }
    }

    /// <summary>Map a registered window id to its <see cref="StreamBuffer"/>, or
    /// null if the id is the main window / a non-stream dockable. Mirrors the
    /// inbound stream→buffer switch in <see cref="Attach"/>.</summary>
    private StreamBuffer? TryGetBuffer(string id) => id switch
    {
        "logons"               => Logons,
        "talk"                 => Talk,
        "whispers"             => Whispers,
        "thoughts"             => Thoughts,
        "combat"               => Combat,
        "familiar"             => Familiar,
        "death"                => Death,
        "assess"               => Assess,
        "atmospherics"         => Atmospherics,
        "ooc"                  => Ooc,
        "log"                  => Log,
        "itemlog" or "itemLog" => ItemLog,
        _                      => null,
    };
}

public class StreamBuffer(string name) : ReactiveObject
{
    private const int Max = 500;

    public string Name { get; } = name;

    /// <summary>
    /// Lines as <see cref="TextLine"/> records so the template can use the
    /// same <c>InlinesBehavior</c> + <c>DefaultHighlights.Tokenize</c> pipeline
    /// the main game window uses — user-defined highlights apply to side
    /// streams (logons, talk, whispers, thoughts, combat) as well as main.
    /// </summary>
    public ObservableCollection<TextLine> Lines { get; } = [];

    /// <summary>Live per-window settings (font / colour / timestamp / title),
    /// assigned by <see cref="Genie.App.Docking.StreamTool"/> from the
    /// WindowSettingsStore. The instance is mutated in place by the Layout tab,
    /// so reading <see cref="WindowSettings.Timestamp"/> at <see cref="Add"/>
    /// time always reflects the latest toggle — no re-subscription needed.</summary>
    public WindowSettings? Settings { get; set; }

    /// <summary>Live Names engine (assigned in <see cref="StreamTabsViewModel.Attach"/>),
    /// used by the <see cref="NameListOnly"/> filter.</summary>
    public NameHighlightEngine? Names { get; set; }

    /// <summary>Genie 4 "Name List Only" — when true, <see cref="Add"/> drops any
    /// line that doesn't mention a name in the player's Names list. Toggled from
    /// the window right-click menu; mirrors <see cref="WindowSettings.NameListOnly"/>.</summary>
    public bool NameListOnly { get; set; }

    public void Add(string line,
                    IReadOnlyList<BoldSpan>? bolds = null,
                    IReadOnlyList<LinkSpan>? links = null,
                    IReadOnlyList<PresetSpan>? presets = null)
    {
        // #90 Name List Only: skip lines that don't reference a tracked name.
        // No names configured (Names null / empty regex) → Match returns null
        // for everything, which would blank the window; treat "no name list" as
        // "show all" so the toggle never hides everything by surprise.
        if (NameListOnly && Names is { Rules.Count: > 0 } && Names.Match(line) is null)
            return;

        // #187: carry the parser's span metadata (bold / link / preset) through
        // so side streams render with the same styling as the main window. The
        // combat stream in particular wraps the hit result in <pushBold> — that's
        // the gold "monster bold" Genie 4 / Wrayth shows; dropping the span here
        // was why combat text rendered plain white.
        //
        // #90: per-window timestamp. When the Layout-tab "prepend timestamp to
        // each line" toggle is on for this window, stamp each line as it arrives
        // (going forward — existing scrollback is not retro-stamped, matching
        // Genie 4). The spans are ABSOLUTE offsets into the raw text, so shift
        // them right by the prefix length or the styling lands on the wrong
        // characters (mirrors GameTextViewModel.AddLine).
        if (Settings?.Timestamp == true)
        {
            var prefix = WindowTimestamp.Prefix();
            var shift  = prefix.Length;
            line    = prefix + line;
            bolds   = bolds?.Select(s   => s with { Start = s.Start + shift }).ToList();
            links   = links?.Select(s   => s with { Start = s.Start + shift }).ToList();
            presets = presets?.Select(s => s with { Start = s.Start + shift }).ToList();
        }
        Lines.Add(new TextLine(line, StreamColor.Main, links, bolds, presets, Window: Name));
        while (Lines.Count > Max)
            Lines.RemoveAt(0);
    }
}

/// <summary>Shared per-window timestamp prefix (#90). Fixed 24-hour
/// <c>[HH:mm:ss]</c> format for now; a per-window format string is a future
/// enhancement (WindowSettings carries only the on/off bool today).</summary>
internal static class WindowTimestamp
{
    public static string Prefix() => $"[{System.DateTime.Now:HH:mm:ss}] ";
}
