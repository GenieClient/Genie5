using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Genie.Core.Layout;
using Genie.Core.Persistence;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Per-window "Flash on Activity" toggle for the unread-tab blink: defaults
/// on, survives the windows.json round-trip, and files predating the toggle
/// (no FlashOnActivity field) keep the shipped always-flash behaviour. Mirrors
/// <see cref="WindowSettingsWordWrapTests"/>.
/// </summary>
public class WindowSettingsFlashOnActivityTests
{
    [Fact]
    public void FlashOnActivity_defaults_on()
    {
        Assert.True(new WindowSettings().FlashOnActivity);
    }

    [Fact]
    public void FlashOnActivity_round_trips_through_windows_json()
    {
        var store = new WindowSettingsStore();
        store.Register("thoughts", "Thoughts");
        store.Get("thoughts").FlashOnActivity = false;

        var path = Path.Combine(Path.GetTempPath(), $"g5-flashtest-{Guid.NewGuid():N}.json");
        try
        {
            new PersistenceService().SaveWindowSettings(path, store);

            var fresh = new WindowSettingsStore();
            fresh.Register("thoughts", "Thoughts");
            foreach (var m in new PersistenceService().LoadWindowSettings(path))
                fresh.Apply(m);

            Assert.False(fresh.Get("thoughts").FlashOnActivity);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Legacy_windows_json_without_field_keeps_flashing()
    {
        // An entry from before the toggle existed — no FlashOnActivity property.
        var legacy = """[{"Id":"talk","DisplayTitle":"","FontFamily":"","FontSize":0,"Foreground":"Default","Background":"","Timestamp":false,"NameListOnly":false,"EchoToMain":true,"WordWrap":true,"IfClosed":null,"HasIfClosed":true}]""";
        var models = JsonSerializer.Deserialize<List<WindowSettingsPersistenceModel>>(legacy)!;

        var store = new WindowSettingsStore();
        store.Register("talk", "Talk");
        foreach (var m in models) store.Apply(m);

        Assert.True(store.Get("talk").FlashOnActivity);
    }
}
