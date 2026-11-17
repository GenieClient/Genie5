using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Genie.Core.Health;
using Genie.Core.Mapper;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// A room's exits can be read on one thread while another changes them
/// (public #398: "Collection was modified" in the map panel's refresh while
/// the automapper added exits during fast travel).
/// </summary>
public class MapExitListTests
{
    private static MapExit Exit(Direction d) => new() { Direction = d, MoveCommand = d.ToString().ToLowerInvariant() };

    [Fact]
    public void Adding_during_a_foreach_does_not_throw_and_the_loop_sees_the_old_list()
    {
        var node = new MapNode();
        node.Exits.Add(Exit(Direction.North));
        node.Exits.Add(Exit(Direction.South));

        var seen = 0;
        foreach (var _ in node.Exits)
        {
            node.Exits.Add(Exit(Direction.East));   // threw with a plain List
            seen++;
        }

        Assert.Equal(2, seen);
        Assert.Equal(4, node.Exits.Count);
    }

    [Fact]
    public void Removing_during_a_foreach_does_not_throw()
    {
        var node = new MapNode();
        node.Exits.Add(Exit(Direction.North));
        node.Exits.Add(Exit(Direction.South));

        foreach (var _ in node.Exits) node.Exits.RemoveAll(e => e.Direction == Direction.South);

        Assert.Equal(Direction.North, Assert.Single(node.Exits).Direction);
    }

    [Fact]
    public void RemoveAll_reports_how_many_went_and_keeps_order()
    {
        var list = new MapExitList();
        list.Add(Exit(Direction.North));
        list.Add(Exit(Direction.East));
        list.Add(Exit(Direction.South));

        Assert.Equal(1, list.RemoveAll(e => e.Direction == Direction.East));
        Assert.Equal(0, list.RemoveAll(e => e.Direction == Direction.East));
        Assert.Equal(new[] { Direction.North, Direction.South }, list.Select(e => e.Direction));
    }

    [Fact]
    public void List_operations_behave_like_a_list()
    {
        var list = new MapExitList();
        var n = Exit(Direction.North);
        var s = Exit(Direction.South);
        var e = Exit(Direction.East);

        list.Add(n);
        list.AddRange(new[] { s });
        list.Insert(1, e);
        Assert.Equal(new[] { n, e, s }, list);
        Assert.Equal(1, list.IndexOf(e));
        Assert.True(list.Contains(s));

        list[1] = s;
        list.RemoveAt(2);
        Assert.Equal(new[] { n, s }, list);

        Assert.True(list.Remove(n));
        Assert.False(list.Remove(n));
        list.Clear();
        Assert.Empty(list);
    }

    [Fact]
    public async Task A_reader_and_a_writer_on_two_threads_never_collide()
    {
        var node = new MapNode();
        using var stop = new CancellationTokenSource();

        // The automapper's side: exits come and go.
        var writer = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                node.Exits.Add(Exit(Direction.North));
                node.Exits.Add(Exit(Direction.None));
                if (node.Exits.Count > 16) node.Exits.RemoveAll(_ => true);
            }
        });

        // The map panel's side: the two loops MapperViewModel.Refresh runs.
        var reader = Task.Run(() =>
        {
            for (var i = 0; i < 20_000; i++)
            {
                var compass = 0;
                foreach (var exit in node.Exits) if (exit.Direction != Direction.None) compass++;
                foreach (var exit in node.Exits) if (exit.Direction == Direction.None) compass--;
                _ = node.Exits.FirstOrDefault(x => x.Direction == Direction.South);
            }
        });

        await reader;            // faults here if an enumeration ever throws
        stop.Cancel();
        await writer;
    }
}

/// <summary>
/// The Healing panel's touch-pattern check runs on every game line; a regex
/// timeout there must read as "no patient", never as a crash (public #399).
/// </summary>
public class HealingTouchTimeoutTests
{
    [Fact]
    public void A_match_that_times_out_is_no_match_instead_of_an_exception()
    {
        // Catastrophic backtracking with a 1 ms limit: certain to time out.
        var slow = new Regex("^(a+)+$", RegexOptions.None, TimeSpan.FromMilliseconds(1));
        var text = new string('a', 40) + "!";
        Assert.Throws<RegexMatchTimeoutException>(() => slow.Match(text));

        Assert.False(HealingSettings.TryMatch(slow, text, out _));
    }

    [Fact]
    public void The_touch_timeout_is_long_enough_to_ride_out_a_stall()
    {
        Assert.True(HealingSettings.TouchMatchTimeout >= TimeSpan.FromSeconds(1));
    }
}
