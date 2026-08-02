using System;
using System.Collections.Generic;
using Genie.Core.Layout;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// IfClosed routing resolver (public #211). Verifies the sentinels, the
/// chain-follow when a redirect target is itself closed, the cycle guard, and
/// the "unknown target never drops" anti-rot safety.
/// </summary>
public class IfClosedResolverTests
{
    // Build a store with the given windows registered, then apply IfClosed
    // overrides. Registering seeds the shipped DefaultIfClosed (talk/whispers →
    // log); overrides win so each test controls exactly what it needs.
    private static WindowSettingsStore Store(params string[] ids)
    {
        var s = new WindowSettingsStore();
        foreach (var id in ids) s.Register(id, id);
        return s;
    }

    private static Func<string, bool> Open(params string[] openIds)
    {
        var set = new HashSet<string>(openIds, StringComparer.OrdinalIgnoreCase);
        return id => set.Contains(id);
    }

    [Fact]
    public void Null_routes_to_main()
    {
        var store = Store("combat");
        store.Get("combat").IfClosed = null;

        var d = IfClosedResolver.Resolve("combat", store, Open());

        Assert.Equal(IfClosedSinkKind.Main, d.Kind);
    }

    [Fact]
    public void Empty_string_drops()
    {
        var store = Store("combat");
        store.Get("combat").IfClosed = "";

        var d = IfClosedResolver.Resolve("combat", store, Open());

        Assert.Equal(IfClosedSinkKind.Drop, d.Kind);
    }

    [Theory]
    [InlineData("game-text")]
    [InlineData("main")]
    [InlineData("MAIN")]
    public void Main_id_and_alias_route_to_main(string value)
    {
        var store = Store("combat");
        store.Get("combat").IfClosed = value;

        var d = IfClosedResolver.Resolve("combat", store, Open());

        Assert.Equal(IfClosedSinkKind.Main, d.Kind);
    }

    [Fact]
    public void Open_target_delivers_to_that_stream()
    {
        var store = Store("talk", "log");
        store.Get("talk").IfClosed = "log";

        var d = IfClosedResolver.Resolve("talk", store, Open("log"));

        Assert.Equal(IfClosedSinkKind.Stream, d.Kind);
        Assert.Equal("log", d.StreamId);
    }

    [Fact]
    public void Closed_target_with_null_follows_chain_to_main()
    {
        var store = Store("talk", "log");
        store.Get("talk").IfClosed = "log";
        store.Get("log").IfClosed  = null;   // log closed, default → Main

        var d = IfClosedResolver.Resolve("talk", store, Open(/* log closed */));

        Assert.Equal(IfClosedSinkKind.Main, d.Kind);
    }

    [Fact]
    public void Closed_target_that_drops_follows_chain_to_drop()
    {
        var store = Store("talk", "log");
        store.Get("talk").IfClosed = "log";
        store.Get("log").IfClosed  = "";     // log closed, explicitly disabled

        var d = IfClosedResolver.Resolve("talk", store, Open(/* log closed */));

        Assert.Equal(IfClosedSinkKind.Drop, d.Kind);
    }

    [Fact]
    public void Unknown_target_routes_to_main_never_drops()
    {
        var store = Store("combat");
        store.Get("combat").IfClosed = "conversation";  // dangling id (the #211 rot)

        var d = IfClosedResolver.Resolve("combat", store, Open());

        Assert.Equal(IfClosedSinkKind.Main, d.Kind);
    }

    [Fact]
    public void Cycle_falls_back_to_main()
    {
        var store = Store("a", "b");
        store.Get("a").IfClosed = "b";
        store.Get("b").IfClosed = "a";       // both closed → a→b→a cycle

        var d = IfClosedResolver.Resolve("a", store, Open(/* both closed */));

        Assert.Equal(IfClosedSinkKind.Main, d.Kind);
    }

    [Fact]
    public void Self_reference_falls_back_to_main()
    {
        var store = Store("a");
        store.Get("a").IfClosed = "a";       // closed, points at itself

        var d = IfClosedResolver.Resolve("a", store, Open());

        Assert.Equal(IfClosedSinkKind.Main, d.Kind);
    }

    [Fact]
    public void Two_hop_chain_delivers_to_first_open_window()
    {
        var store = Store("talk", "log", "combat");
        store.Get("talk").IfClosed = "log";
        store.Get("log").IfClosed  = "combat";   // log closed → follow to combat
        // combat is open

        var d = IfClosedResolver.Resolve("talk", store, Open("combat"));

        Assert.Equal(IfClosedSinkKind.Stream, d.Kind);
        Assert.Equal("combat", d.StreamId);
    }

    [Fact]
    public void Shipped_default_maps_talk_to_log()
    {
        // No override — exercises the reconciled DefaultIfClosed table.
        var store = Store("talk", "log");

        var d = IfClosedResolver.Resolve("talk", store, Open("log"));

        Assert.Equal(IfClosedSinkKind.Stream, d.Kind);
        Assert.Equal("log", d.StreamId);
    }
}
