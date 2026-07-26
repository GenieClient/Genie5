using System;
using System.Collections.Generic;
using System.Linq;
using Genie.Core.Events;
using Genie.Core.Parser;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #178 — text between <c>&lt;output class="mono"/&gt;</c> and
/// <c>&lt;output class=""/&gt;</c> (maps, stat tables) is tagged Mono on its
/// TextEvent so the display can render it in the monospace font while normal
/// prose uses the configured game font. The bracket opens on "mono" and closes
/// on the empty class.
/// </summary>
public class MonoOutputClassTests
{
    private static List<TextEvent> Feed(params string[] chunks)
    {
        var parser = new DrXmlParser(NullLogger<DrXmlParser>.Instance);
        var events = new List<GameEvent>();
        using var _ = parser.GameEvents.Subscribe(new C(events));
        foreach (var c in chunks) parser.Feed(c);
        return events.OfType<TextEvent>().ToList();
    }
    private sealed class C : IObserver<GameEvent>
    { private readonly List<GameEvent> s; public C(List<GameEvent> x)=>s=x;
      public void OnNext(GameEvent e)=>s.Add(e); public void OnError(Exception e){} public void OnCompleted(){} }



    [Fact]
    public void Lines_inside_the_mono_bracket_are_tagged_mono()
    {
        var texts = Feed(
            "normal line\n" +
            "<output class=\"mono\"/>" +
            "  Strength :  38   Reflex :  27\n" +
            "  Agility  :  30   Charisma: 25\n" +
            "<output class=\"\"/>" +
            "back to normal\n");

        TextEvent Line(string startsWith) =>
            texts.First(t => t.Text.TrimStart().StartsWith(startsWith));

        Assert.False(Line("normal").Mono);
        Assert.True(Line("Strength").Mono);
        Assert.True(Line("Agility").Mono);
        Assert.False(Line("back to normal").Mono);
    }

    [Fact]
    public void Single_quoted_mono_bracket_also_toggles()
    {
        // DR sends both "mono" and 'mono' quoting across sessions.
        var texts = Feed("<output class='mono'/>tabled\n<output class=''/>plain\n");
        Assert.True(texts.First(t => t.Text == "tabled").Mono);
        Assert.False(texts.First(t => t.Text == "plain").Mono);
    }
}
