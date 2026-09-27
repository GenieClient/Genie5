using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Genie.Core.Layout;
using Genie.Core.Persistence;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Per-window "Hide Title Bar" while docked alone in a frame (public #299):
/// defaults off, survives the windows.json round-trip, and files predating the
/// toggle keep every frame header shown. Mirrors
/// <see cref="WindowSettingsFlashOnActivityTests"/>.
/// </summary>
public class WindowSettingsHideTitleBarTests
{
    [Fact]
    public void HideTitleBarWhenAlone_defaults_off()
    {
        Assert.False(new WindowSettings().HideTitleBarWhenAlone);
    }

    [Fact]
    public void HideTitleBarWhenAlone_round_trips_through_windows_json()
    {
        var store = new WindowSettingsStore();
        store.Register("room", "Room");
        store.Register("talk", "Talk");
        store.Get("room").HideTitleBarWhenAlone = true;

        var path = Path.Combine(Path.GetTempPath(), $"g5-titlebartest-{Guid.NewGuid():N}.json");
        try
        {
            new PersistenceService().SaveWindowSettings(path, store);

            var fresh = new WindowSettingsStore();
            fresh.Register("room", "Room");
            fresh.Register("talk", "Talk");
            foreach (var m in new PersistenceService().LoadWindowSettings(path))
                fresh.Apply(m);

            Assert.True(fresh.Get("room").HideTitleBarWhenAlone);
            Assert.False(fresh.Get("talk").HideTitleBarWhenAlone);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Legacy_windows_json_without_field_keeps_the_header()
    {
        var legacy = """[{"Id":"room","DisplayTitle":"","FontFamily":"","FontSize":0,"Foreground":"Default","Background":"","Timestamp":false,"NameListOnly":false,"EchoToMain":true,"WordWrap":true,"FlashOnActivity":true,"IfClosed":null,"HasIfClosed":true}]""";
        var models = JsonSerializer.Deserialize<List<WindowSettingsPersistenceModel>>(legacy)!;

        var store = new WindowSettingsStore();
        store.Register("room", "Room");
        foreach (var m in models) store.Apply(m);

        Assert.False(store.Get("room").HideTitleBarWhenAlone);
    }
}
