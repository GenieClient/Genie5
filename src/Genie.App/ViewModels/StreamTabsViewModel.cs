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

    /// <summary>Consolidated conversation log — mirrors the speech streams
    /// (talk / whispers), Genie 4 "Log" window parity. Also an <c>#echo &gt;log</c>
    /// target (wired in MainWindowViewModel).</summary>
    public StreamBuffer Log      { get; } = new("Log");

    /// <summary>Item / loot log. Fed by the <c>itemLog</c> server stream and by
    /// <c>#echo &gt;itemlog</c> from scripts.</summary>
    public StreamBuffer ItemLog  { get; } = new("ItemLog");

    public IReadOnlyList<StreamBuffer> All =>
        [Logons, Talk, Whispers, Thoughts, Combat, Familiar, Death, Assess, Atmospherics, Log, ItemLog];

    /// <summary>Main game-window sink, handed in by <see cref="Attach"/> so a
    /// stream with its <c>EchoToMain</c> toggle on can also post into Main.</summary>
    private GameTextViewModel? _main;

    public void Attach(GenieCore core, GameTextViewModel? main = null)
    {
        _main = main;

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
                    "itemlog" or "itemLog" => ItemLog,
                    _                      => null
                };
                // #187: pass the parser's span metadata (bold / link / preset)
                // through so the stream panel renders monster-bold, clickable
                // links and preset colours just like the main window does.
                buf?.Add(e.Text, e.BoldSpans, e.Links, e.PresetSpans);

                // Per-stream "Also show in Main" (Layout tab). When on, echo the
                // line into the main game window in addition to its own panel.
                // buf.Settings is the same WindowSettings instance the Layout tab
                // mutates, so the toggle takes effect live with no re-subscribe —
                // exactly like the Timestamp / NameListOnly toggles above. Carry
                // the spans so the echoed combat hit is gold in Main too (#187).
                if (buf?.Settings?.EchoToMain == true)
                    _main?.EchoStreamToMain(e.Text, e.BoldSpans, e.Links, e.PresetSpans);

                // The Log window is a consolidated conversation feed: mirror
                // the speech streams into it (matches the Genie 4 / dylb0t
                // prototype "Log" window).
                if (e.Stream is "talk" or "whispers")
                    Log.Add(e.Text, e.BoldSpans, e.Links, e.PresetSpans);
            });
    }
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
