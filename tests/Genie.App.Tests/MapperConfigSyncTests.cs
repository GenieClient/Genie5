using System;
using System.IO;
using System.Threading.Tasks;
using Genie.App.ViewModels;
using Genie.Core;
using Genie.Core.Config;
using Xunit;

namespace Genie.App.Tests;

/// <summary>
/// The two Mapper config keys ⇄ the live mapper view state (#274 / #275).
///
/// <para>
/// Record mode is the <c>automapperrecord</c> key (it was <c>automapper</c>
/// until #401, which gave that key back its Genie 4 meaning: the mapper's
/// on/off switch). It was written by three surfaces (the AutoMapper Settings
/// dialog, <c>#mapper record</c>, <c>#config</c>) and read by none: record mode
/// seeded itself from <c>AutoMapperEngine.IsEnabled</c>'s own default, so the
/// saved preference never started the engine and the toolbar toggle never
/// persisted. <c>automapperalpha</c> was read exactly once, at attach, so the
/// dialog's ghost-floor slider didn't repaint until the next core build. Both
/// keys raise <c>ConfigFieldUpdated.AutoMapper</c>; these tests drive the real
/// <see cref="MapperViewModel.Attach"/> wiring through a real
/// <see cref="GenieCore"/> rather than calling the sync method directly, so the
/// subscription itself is covered.
/// </para>
///
/// <para>
/// The config → view hop uses <c>RxApp.MainThreadScheduler</c>, which
/// ReactiveUI resolves to an immediate/current-thread scheduler under a unit
/// test host — the same property StreamTabsViewModelTests documents — so
/// assertions can run directly after <c>SetSetting</c> with no dispatcher pump.
/// </para>
/// </summary>
public class MapperConfigSyncTests
{
    private sealed class Harness : IAsyncDisposable
    {
        public GenieCore       Core   { get; }
        public MapperViewModel Mapper { get; } = new();

        private readonly string _dir;

        /// <summary>Builds a core on an isolated data dir, optionally setting the
        /// config keys BEFORE the attach so the seeding path is exercised the way
        /// a settings.cfg load does it.</summary>
        public Harness(bool? record = null, int? alpha = null)
        {
            _dir = Path.Combine(Path.GetTempPath(), "genie_app_tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            Core = new GenieCore(dataDirectoryOverride: _dir, gameThreadOverride: false);

            if (record is not null)
                Core.Config.SetSetting("automapperrecord", record.Value.ToString(), showException: false);
            if (alpha is not null)
                Core.Config.SetSetting("automapperalpha", alpha.Value.ToString(), showException: false);

            Mapper.Attach(Core);
        }

        public async ValueTask DisposeAsync()
        {
            await Core.DisposeAsync();
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }
    }

    // ── Seeding: the saved preference decides the starting mode ─────────────

    [Fact]
    public async Task Record_mode_seeds_from_the_saved_config_key()
    {
        // Before the fix this attached with the engine's
        // own default (false) regardless of what settings.cfg said.
        await using var on = new Harness(record: true);
        Assert.True(on.Core.Config.AutoMapperRecord);
        Assert.True(on.Mapper.AutoCreateEnabled);
        Assert.True(on.Core.AutoMapper.IsEnabled);
    }

    [Fact]
    public async Task Lookup_only_profile_seeds_the_engine_off()
    {
        await using var off = new Harness(record: false);
        Assert.False(off.Mapper.AutoCreateEnabled);
        Assert.False(off.Core.AutoMapper.IsEnabled);
    }

    [Fact]
    public async Task Ghost_floor_alpha_seeds_from_the_saved_config_key()
    {
        await using var h = new Harness(alpha: 96);
        Assert.Equal(96, h.Mapper.AutoMapperAlpha);
    }

    // ── Live apply: config writes reach the view without a restart ──────────

    [Fact]
    public async Task Config_write_turns_record_mode_off_live()
    {
        // `#mapper record off`, the dialog's Record checkbox and
        // `#config automapperrecord false` all land here.
        await using var h = new Harness(record: true);

        h.Core.Config.SetSetting("automapperrecord", "False", showException: false);

        Assert.False(h.Mapper.AutoCreateEnabled);
        Assert.False(h.Core.AutoMapper.IsEnabled);
    }

    [Fact]
    public async Task Config_write_turns_record_mode_on_live()
    {
        await using var h = new Harness(record: false);

        h.Core.Config.SetSetting("automapperrecord", "True", showException: false);

        Assert.True(h.Mapper.AutoCreateEnabled);
        Assert.True(h.Core.AutoMapper.IsEnabled);
    }

    [Fact]
    public async Task Ghost_floor_alpha_applies_live()
    {
        // The AutoMapper Settings slider: persisted fine, repainted never.
        await using var h = new Harness(alpha: 255);

        h.Core.Config.SetSetting("automapperalpha", "0", showException: false);

        Assert.Equal(0, h.Mapper.AutoMapperAlpha);
    }

    // ── Write-back: the toolbar toggle persists ─────────────────────────────

    [Fact]
    public async Task Toolbar_toggle_writes_back_to_config()
    {
        // ⏺ Record / the Maps-menu checkbox bind straight to AutoCreateEnabled;
        // before the fix the choice died with the session.
        await using var h = new Harness(record: false);

        h.Mapper.AutoCreateEnabled = true;

        Assert.True(h.Core.Config.AutoMapperRecord);
        Assert.True(h.Core.AutoMapper.IsEnabled);
    }

    [Fact]
    public async Task Write_back_and_live_apply_do_not_ping_pong()
    {
        // The write-back is change-guarded and the sync sets reactive properties
        // to values they may already hold, so one user toggle must produce
        // exactly one config notification and settle.
        await using var h = new Harness(record: false);

        var notifications = 0;
        h.Core.Config.ConfigChanged += f => { if (f == ConfigFieldUpdated.AutoMapper) notifications++; };

        h.Mapper.AutoCreateEnabled = true;

        Assert.Equal(1, notifications);
        Assert.True(h.Core.Config.AutoMapperRecord);
        Assert.True(h.Mapper.AutoCreateEnabled);
    }

    // ── #401: `automapper` is the on/off switch, not Record ─────────────────

    [Fact]
    public async Task Automapper_key_does_not_touch_record_mode()
    {
        // Genie 4 scripts (hunting scripts) run `#config automapper true` at startup;
        // in Genie 4 that only made sure the mapper was on.
        await using var h = new Harness(record: false);

        h.Core.Config.SetSetting("automapper", "True", showException: false);

        Assert.False(h.Mapper.AutoCreateEnabled);
        Assert.False(h.Core.AutoMapper.IsEnabled);
        Assert.True(h.Core.AutoMapper.IsTracking);
    }

    [Fact]
    public async Task Automapper_key_switches_tracking_off_and_on()
    {
        await using var h = new Harness();
        Assert.True(h.Core.Config.AutoMapper);          // Genie 4 default: on
        Assert.True(h.Core.AutoMapper.IsTracking);

        h.Core.Config.SetSetting("automapper", "False", showException: false);
        Assert.False(h.Core.AutoMapper.IsTracking);

        h.Core.Config.SetSetting("automapper", "True", showException: false);
        Assert.True(h.Core.AutoMapper.IsTracking);
    }
}
