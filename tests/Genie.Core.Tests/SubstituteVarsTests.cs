using System;
using System.IO;
using System.Threading.Tasks;
using Genie.Core;
using Genie.Core.Substitutes;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #246 — <c>$globals</c> in a substitute's replacement text, resolved
/// at MATCH time: replacing a name with <c>$charactername</c>, or tagging a
/// line with <c>$roomid</c>.
///
/// <para>The risk this feature carries is not the feature. Substitute
/// replacements are handed to <c>Regex.Replace</c>, where <c>$</c> already
/// means something — <c>$1</c> is a capture-group reference and existing rules
/// use it. So the tests that matter most here are the ones proving group
/// references still work.</para>
/// </summary>
public class SubstituteVarsTests : IAsyncLifetime
{
    private string    _dir  = "";
    private GenieCore _core = null!;

    public Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "genie_subvar_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _core = new GenieCore(dataDirectoryOverride: _dir, gameThreadOverride: false);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _core.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ── The regression guard, first ──────────────────────────────────────────

    /// <summary>A capture-group reference must survive expansion untouched.
    /// This is the rule shape that already exists in users' configs.</summary>
    [Fact]
    public void A_capture_group_reference_still_works()
    {
        _core.Substitutes.AddRule(@"the (\w+) orb", "THE $1 ORB");

        Assert.Equal("You see THE glowing ORB.",
                     _core.Substitutes.Apply("You see the glowing orb."));
    }

    /// <summary>A replacement with no `$` at all takes the cheap path and is
    /// byte-for-byte what it always was.</summary>
    [Fact]
    public void A_literal_replacement_is_unchanged()
    {
        _core.Substitutes.AddRule("kobold", "friend");

        Assert.Equal("A friend arrives.", _core.Substitutes.Apply("A kobold arrives."));
    }

    // ── The feature ──────────────────────────────────────────────────────────

    [Fact]
    public void A_global_variable_is_resolved_in_the_replacement()
    {
        _core.Scripts.Globals["charactername"] = "Renucci";
        _core.Substitutes.AddRule("SOMEONE", "$charactername");

        Assert.Equal("Renucci waves.", _core.Substitutes.Apply("SOMEONE waves."));
    }

    /// <summary>Resolved at match time, not at rule-add time — the whole point
    /// is that `$roomid` reads the room you are in NOW.</summary>
    [Fact]
    public void The_value_is_read_at_match_time_not_when_the_rule_was_added()
    {
        _core.Scripts.Globals["roomid"] = "100";
        _core.Substitutes.AddRule(@"^HERE$", "room $roomid");

        Assert.Equal("room 100", _core.Substitutes.Apply("HERE"));

        _core.Scripts.Globals["roomid"] = "205";
        Assert.Equal("room 205", _core.Substitutes.Apply("HERE"));
    }

    /// <summary>A bare engine — .cfg replay, a unit test — has no expansion
    /// hook, and keeps replacements literal rather than throwing.</summary>
    [Fact]
    public void A_bare_engine_leaves_variables_literal()
    {
        var engine = new SubstituteEngine();
        engine.AddRule("SOMEONE", "$charactername");

        Assert.Equal("$charactername waves.", engine.Apply("SOMEONE waves."));
    }
}
