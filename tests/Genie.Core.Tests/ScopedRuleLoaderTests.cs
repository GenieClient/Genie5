using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Genie.Core.Persistence;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Pins the #257 two-layer precedence rule: Character entries first, then
/// Global entries whose key isn't shadowed. Order is load-bearing — the
/// pattern engines are first-match-wins and AddRule appends.
/// </summary>
public class ScopedRuleLoaderTests
{
    private sealed record R(string Key, string Value);

    private static List<(R Item, RuleScope Scope)> Layer(
        IEnumerable<R> character, IEnumerable<R> global)
        => ScopedRuleLoader.Layer(character, global, r => r.Key);

    [Fact]
    public void CharacterEntriesComeFirst_ThenUnshadowedGlobals()
    {
        var result = Layer(
            character: new[] { new R("a", "char-a") },
            global:    new[] { new R("b", "glob-b"), new R("c", "glob-c") });

        Assert.Equal(3, result.Count);
        Assert.Equal(("a", RuleScope.Character), (result[0].Item.Key, result[0].Scope));
        Assert.Equal(("b", RuleScope.Global),    (result[1].Item.Key, result[1].Scope));
        Assert.Equal(("c", RuleScope.Global),    (result[2].Item.Key, result[2].Scope));
    }

    [Fact]
    public void CharacterEntryShadowsSameKeyGlobal()
    {
        var result = Layer(
            character: new[] { new R("kill", "char-version") },
            global:    new[] { new R("kill", "glob-version"), new R("other", "g") });

        Assert.Equal(2, result.Count);
        Assert.Equal("char-version", result[0].Item.Value);
        Assert.Equal("other",        result[1].Item.Key);
    }

    [Fact]
    public void ShadowingIsCaseInsensitive()
    {
        var result = Layer(
            character: new[] { new R("Kill Rat", "c") },
            global:    new[] { new R("kill rat", "g") });

        Assert.Single(result);
        Assert.Equal(RuleScope.Character, result[0].Scope);
    }

    [Fact]
    public void DisabledCharacterEntryStillShadows_TheOptOutMechanism()
    {
        // The design's per-character opt-out: a same-key Character entry with
        // IsEnabled=false hides the Global rule. The loader only sees keys —
        // the entry's disabled state rides along and the engine skips it.
        var result = Layer(
            character: new[] { new R("noisy global", "disabled-local-copy") },
            global:    new[] { new R("noisy global", "enabled-global") });

        Assert.Single(result);
        Assert.Equal("disabled-local-copy", result[0].Item.Value);
    }

    [Fact]
    public void DuplicateKeysWithinOneLayerAreAllKept()
    {
        var result = Layer(
            character: new[] { new R("x", "c1"), new R("x", "c2") },
            global:    new[] { new R("x", "g"), new R("y", "g2") });

        Assert.Equal(3, result.Count);                       // both character x's + global y
        Assert.All(result.Take(2), r => Assert.Equal(RuleScope.Character, r.Scope));
        Assert.Equal("y", result[2].Item.Key);
    }

    [Fact]
    public void EmptyCharacterLayer_AllGlobalsPassThrough()
    {
        var result = Layer(character: Array.Empty<R>(),
                           global: new[] { new R("a", "g1"), new R("b", "g2") });
        Assert.Equal(2, result.Count);
        Assert.All(result, r => Assert.Equal(RuleScope.Global, r.Scope));
    }

    [Fact]
    public void EmptyGlobalLayer_AllCharacterPassThrough()
    {
        var result = Layer(character: new[] { new R("a", "c1") }, global: Array.Empty<R>());
        Assert.Single(result);
        Assert.Equal(RuleScope.Character, result[0].Scope);
    }

    // Built at runtime, not InlineData: drive-letter literals don't normalize
    // on Linux (broke ubuntu CI), and the case-folding expectation is
    // per-platform — Windows/macOS filesystems fold case, Linux doesn't.
    public static IEnumerable<object[]> SameDirectoryCases()
    {
        var root = OperatingSystem.IsWindows() ? @"C:\" : "/";
        var cfg  = Path.Combine(root, "cfg");
        var foldsCase = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

        yield return new object[] { Path.Combine(cfg, "Profiles", "Ren-MONIL"), cfg, false };
        yield return new object[] { cfg, cfg, true };
        yield return new object[] { cfg + Path.DirectorySeparatorChar, cfg, true };            // trailing sep
        yield return new object[] { cfg, Path.Combine(root, "CFG"), foldsCase };               // case
        yield return new object[]
            { Path.Combine(cfg, "Profiles", "..") + Path.DirectorySeparatorChar, cfg, true };  // normalized
    }

    [Theory]
    [MemberData(nameof(SameDirectoryCases))]
    public void SameDirectory_NormalizesBeforeComparing(string a, string b, bool same)
        => Assert.Equal(same, ScopedRuleLoader.SameDirectory(a, b));

    [Fact]
    public void SameDirectory_BlankProfileDirCountsAsSingleLayer()
        => Assert.True(ScopedRuleLoader.SameDirectory("", @"C:\cfg"));

    [Fact]
    public void Paths_ProfileFirst()
    {
        var (p, g) = ScopedRuleLoader.Paths(@"C:\p", @"C:\g", "highlights.json");
        Assert.Equal(Path.Combine(@"C:\p", "highlights.json"), p);
        Assert.Equal(Path.Combine(@"C:\g", "highlights.json"), g);
    }

    // ── MergeGlobalForSave — the shadowed-twin wipe guard ─────────────────────

    private static readonly string[] NoDeletes = Array.Empty<string>();

    [Fact]
    public void MergeGlobalForSave_PreservesShadowedTwinsMissingFromEngine()
    {
        // Fully-forked profile: every global rule is shadowed, so the engine's
        // Global subset is EMPTY — the save must still keep the disk set.
        var merged = ScopedRuleLoader.MergeGlobalForSave(
            engineGlobal: Array.Empty<R>(),
            diskGlobal:   new[] { new R("a", "g1"), new R("b", "g2") },
            key:          r => r.Key,
            deletedKeys:  NoDeletes);
        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void MergeGlobalForSave_EngineVersionWinsOverDisk()
    {
        var merged = ScopedRuleLoader.MergeGlobalForSave(
            engineGlobal: new[] { new R("a", "edited") },
            diskGlobal:   new[] { new R("A", "stale"), new R("b", "kept") },
            key:          r => r.Key,
            deletedKeys:  NoDeletes);
        Assert.Equal(2, merged.Count);
        Assert.Equal("edited", merged[0].Value);
        Assert.Equal("kept",   merged[1].Value);
    }

    [Fact]
    public void MergeGlobalForSave_ExplicitDeletesAreNotResurrected()
    {
        var merged = ScopedRuleLoader.MergeGlobalForSave(
            engineGlobal: Array.Empty<R>(),
            diskGlobal:   new[] { new R("gone", "g"), new R("stays", "g2") },
            key:          r => r.Key,
            deletedKeys:  new[] { "GONE" });                  // case-insensitive
        var item = Assert.Single(merged);
        Assert.Equal("stays", item.Key);
    }
}
