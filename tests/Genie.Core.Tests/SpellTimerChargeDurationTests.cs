using System;
using System.Collections.Generic;
using System.IO;
using Genie.Core.Events;
using Genie.Core.Extensions;
using Genie.Core.Extensions.Builtin;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #301 — a charged spell's time left. DR sends the charge and the duration
/// together on the Active Spells (percWindow) line; the charge rule kept only the
/// percentage, so the window read "Stellar Collector  5% charged" with no time.
/// The line shapes are the ones Lich's DR active-spell tracker documents
/// (lib/common/xmlparser.rb): "Stellar Collector  (0%, 4 anlaen)" and
/// "Stellar Collector  (0%, fading)".
/// </summary>
public class SpellTimerChargeDurationTests
{
    private sealed class Host : IExtensionHost
    {
        public IDictionary<string, string> Globals { get; } = new Dictionary<string, string>();
        public string Window { get; private set; } = "";
        public void Echo(string text) { }
        public void SendCommand(string command) { }
        public void SetWindow(string window, string content) => Window = content;
        public string ConfigDir { get; } = Path.Combine(Path.GetTempPath(), "genie_spelltimer_" + Guid.NewGuid().ToString("N"));
        public void Log(string message) { }
        public string? GetUserVar(string name) => null;
    }

    private static Host Refresh(params string[] lines)
    {
        var host = new Host();
        var ext = new SpellTimerExtension();
        ext.Initialize(host);
        ext.OnGameEvent(new ClearStreamEvent("percWindow"));
        foreach (var l in lines) ext.OnGameEvent(new TextEvent("percWindow", l));
        ext.OnPrompt();
        return host;
    }

    [Fact]
    public void Charge_and_roisaen_are_both_kept()
    {
        var h = Refresh("Stellar Collector  (5%, 12 roisaen)");

        Assert.Equal("5",  h.Globals["SpellTimer.StellarCollector.charge"]);
        Assert.Equal("12", h.Globals["SpellTimer.StellarCollector.duration"]);
        Assert.Contains("5% charged, 12 roisaen", h.Window);
    }

    [Fact]
    public void Anlaen_convert_to_roisaen()
    {
        var h = Refresh("Stellar Collector  (0%, 4 anlaen)");

        Assert.Equal("120", h.Globals["SpellTimer.StellarCollector.duration"]);   // 4 × 30
        Assert.Contains("0% charged, 120 roisaen", h.Window);
    }

    [Fact]
    public void A_fading_collector_shows_as_fading()
    {
        var h = Refresh("Stellar Collector  (0%, fading)");

        Assert.Equal("1", h.Globals["SpellTimer.StellarCollector.active"]);
        Assert.Contains("0% charged, fading", h.Window);
    }

    [Fact]
    public void A_percentage_alone_still_reads_as_the_charge()
    {
        var h = Refresh("Stellar Collector  (75%)");

        Assert.Equal("75", h.Globals["SpellTimer.StellarCollector.charge"]);
        Assert.Contains("75% charged", h.Window);
    }

    [Fact]
    public void Ordinary_spells_are_unchanged_and_also_understand_anlaen()
    {
        var h = Refresh("Noumena  (33 roisaen)", "Khri Sagacity  (1 roisan)", "Persistence of Mana  (2 anlaen)");

        Assert.Equal("33", h.Globals["SpellTimer.Noumena.duration"]);
        Assert.Equal("1",  h.Globals["SpellTimer.KhriSagacity.duration"]);
        Assert.Equal("60", h.Globals["SpellTimer.PersistenceofMana.duration"]);
        Assert.False(h.Globals.ContainsKey("SpellTimer.Noumena.charge"));
    }
}
