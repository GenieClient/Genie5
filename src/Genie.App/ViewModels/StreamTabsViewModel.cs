using System.Collections.ObjectModel;
using System.Reactive.Linq;
using Genie.Core;
using Genie.Core.Events;
using ReactiveUI;

namespace Genie.App.ViewModels;

public class StreamTabsViewModel : ReactiveObject
{
    public StreamBuffer Logons   { get; } = new("Logons");
    public StreamBuffer Talk     { get; } = new("Talk");
    public StreamBuffer Whispers { get; } = new("Whispers");
    public StreamBuffer Thoughts { get; } = new("Thoughts");
    public StreamBuffer Combat   { get; } = new("Combat");

    /// <summary>Consolidated conversation log — mirrors the speech streams
    /// (talk / whispers), Genie 4 "Log" window parity. Also an <c>#echo &gt;log</c>
    /// target (wired in MainWindowViewModel).</summary>
    public StreamBuffer Log      { get; } = new("Log");

    /// <summary>Item / loot log. Fed by the <c>itemLog</c> server stream and by
    /// <c>#echo &gt;itemlog</c> from scripts.</summary>
    public StreamBuffer ItemLog  { get; } = new("ItemLog");

    public IReadOnlyList<StreamBuffer> All => [Logons, Talk, Whispers, Thoughts, Combat, Log, ItemLog];

    public void Attach(GenieCore core)
    {
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
                    "itemlog" or "itemLog" => ItemLog,
                    _                      => null
                };
                buf?.Add(e.Text);

                // The Log window is a consolidated conversation feed: mirror
                // the speech streams into it (matches the Genie 4 / dylb0t
                // prototype "Log" window).
                if (e.Stream is "talk" or "whispers")
                    Log.Add(e.Text);
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

    public void Add(string line)
    {
        Lines.Add(new TextLine(line, StreamColor.Main));
        while (Lines.Count > Max)
            Lines.RemoveAt(0);
    }
}
