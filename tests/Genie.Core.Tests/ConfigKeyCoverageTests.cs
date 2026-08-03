using System;
using System.Linq;
using Genie.Core.Config;
using Genie.Core.Runtime;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Structural invariants of the config system — the contracts that keep
/// <c>#config</c> honest. <see cref="GenieConfig.ToConfigPairs"/> is the single
/// source of truth for what exists; <see cref="GenieConfig.ConfigCategories"/>
/// drives the <c>#config list</c> grouping; <see cref="GenieConfig.SettingAliases"/>
/// lets alternate names (<c>fe</c>) reach the same key from both the setter and
/// the getter. Doc-audit 2026-08-03 found both invariants violated (fe settable
/// but not gettable; four keys stranded in the "Other" bucket) — these tests
/// keep them from regressing.
/// </summary>
public class ConfigKeyCoverageTests
{
    private static GenieConfig NewConfig() =>
        new(new LocalDirectoryService("Genie5Test", AppContext.BaseDirectory));

    [Fact]
    public void EveryConfigPairKeyIsCategorized()
    {
        // The ConfigCategories doc comment promises: every ToConfigPairs key is
        // named in a section, nothing lands in the trailing "Other" bucket.
        var categorized = GenieConfig.ConfigCategories
            .SelectMany(c => c.Keys)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stranded = NewConfig().ToConfigPairs()
            .Select(p => p.Key)
            .Where(k => !categorized.Contains(k))
            .ToList();
        Assert.True(stranded.Count == 0,
            $"Keys missing from ConfigCategories (would print under \"Other\"): {string.Join(", ", stranded)}");
    }

    [Fact]
    public void EveryCategoryKeyIsALiveSetting()
    {
        // The reverse direction: a category naming a key that ToConfigPairs no
        // longer emits is a stale entry that silently prints nothing.
        var live = NewConfig().ToConfigPairs()
            .Select(p => p.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stale = GenieConfig.ConfigCategories
            .SelectMany(c => c.Keys)
            .Where(k => !live.Contains(k))
            .ToList();
        Assert.True(stale.Count == 0,
            $"ConfigCategories names keys that ToConfigPairs no longer emits: {string.Join(", ", stale)}");
    }

    [Fact]
    public void NoKeyIsCategorizedTwice()
    {
        var duplicates = GenieConfig.ConfigCategories
            .SelectMany(c => c.Keys)
            .GroupBy(k => k, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        Assert.True(duplicates.Count == 0,
            $"Keys listed in more than one category: {string.Join(", ", duplicates)}");
    }

    [Fact]
    public void AliasesResolveToLiveKeysAndStayOutOfPersistence()
    {
        var live = NewConfig().ToConfigPairs()
            .Select(p => p.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (alias, canonical) in GenieConfig.SettingAliases)
        {
            // An alias must point at a real key…
            Assert.Contains(canonical, live);
            // …and must never itself be persisted (Save writes canonical only)
            // or categorized (it would print as a duplicate line).
            Assert.DoesNotContain(alias, live);
            Assert.DoesNotContain(alias,
                GenieConfig.ConfigCategories.SelectMany(c => c.Keys),
                StringComparer.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void FeAliasIsSymmetric_SetAndGetBothWork()
    {
        // The doc-audit defect: `#config fe wrayth` set the value while
        // `#config fe` reported "Unknown setting".
        var cfg = NewConfig();
        cfg.SetSetting("fe", "wrayth", showException: false);
        Assert.Equal("WRAYTH", cfg.GetSetting("frontend"));
        Assert.Equal("WRAYTH", cfg.GetSetting("fe"));
        Assert.Equal(cfg.GetSetting("frontend"), cfg.GetSetting("fe"));
    }

    [Fact]
    public void EveryAliasReadsBackWhatItsCanonicalKeyReads()
    {
        // Generic symmetry: whatever GetSetting returns for the canonical key,
        // the alias returns too — no alias can be set-only.
        var cfg = NewConfig();
        foreach (var (alias, canonical) in GenieConfig.SettingAliases)
        {
            var viaCanonical = cfg.GetSetting(canonical);
            Assert.NotNull(viaCanonical);
            Assert.Equal(viaCanonical, cfg.GetSetting(alias));
        }
    }
}
