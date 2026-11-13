using System;
using System.IO;
using System.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Genie.App.Controls;
using Genie.App.Settings;
using Genie.App.ViewModels;
using Xunit;

namespace Genie.App.HeadlessTests;

/// <summary>
/// Public #395: DR's <c>&lt;output class="mono"&gt;</c> blocks (EXP, maps,
/// appraisals) were drawn in a hardcoded "Consolas,Courier New,monospace", which
/// on Linux fell back to the proportional system font, so their columns broke.
/// Font settings now resolve to the first installed font (<see cref="FontChain"/>;
/// checked against real fonts on Linux by hand, the headless platform has none),
/// and the mono font is Genie 4's Mono Font setting again.
/// </summary>
public class MonoFontHeadlessTests : IDisposable
{
    private readonly string _dir;
    private readonly FontFamily _prevMono = TextLine.MonoFont;
    private readonly double? _prevMonoSize = TextLine.MonoFontSize;

    public MonoFontHeadlessTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "genie_monofont_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        TextLine.MonoFont     = _prevMono;
        TextLine.MonoFontSize = _prevMonoSize;
        try { Directory.Delete(_dir, true); } catch { }
    }

    [AvaloniaFact]
    public void Mono_font_defaults_and_round_trips_through_display_json()
    {
        var d = new DisplaySettings();
        Assert.Equal("Consolas,Courier New,monospace", d.MonoFontFamily);
        Assert.Equal(0, d.MonoFontSize);

        d.MonoFontFamily = "DejaVu Sans Mono";
        d.MonoFontSize   = 11;
        var path = Path.Combine(_dir, "display.json");
        d.Save(path);

        var back = DisplaySettings.Load(path);
        Assert.Equal("DejaVu Sans Mono", back.MonoFontFamily);
        Assert.Equal(11, back.MonoFontSize);
    }

    [AvaloniaFact]
    public void Mono_blocks_use_the_mono_font_setting_and_its_size()
    {
        var vm = new MainWindowViewModel(startup: null, dataDirectoryOverride: _dir);
        vm.Display.MonoFontFamily = "Some Mono";
        vm.Display.MonoFontSize   = 11;

        var mono  = new TextLine("Shield Usage:   750 07%", StreamColor.Main, Mono: true);
        var plain = new TextLine("You run west.", StreamColor.Main);

        Assert.All(mono.Inlines, i =>
        {
            Assert.Equal("Some Mono", i.FontFamily.Name);
            Assert.Equal(11, i.FontSize);
        });
        // Ordinary lines keep inheriting the window's font.
        Assert.All(plain.Inlines, i => Assert.False(i.IsSet(Avalonia.Controls.Documents.TextElement.FontFamilyProperty)));

        vm.Display.MonoFontSize = 0;    // 0 = the window's size
        Assert.All(mono.Inlines, i => Assert.False(i.IsSet(Avalonia.Controls.Documents.TextElement.FontSizeProperty)));
    }

    [AvaloniaFact]
    public void Font_picker_converter_turns_a_name_into_a_font()
    {
        var f = FontChain.NameToFamily.Convert("DejaVu Sans Mono", typeof(FontFamily), null,
                                               System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal("DejaVu Sans Mono", Assert.IsType<FontFamily>(f).Name);
    }

    [AvaloniaFact]
    public void Display_dialog_OK_keeps_the_font_lists_unless_a_different_font_is_picked()
    {
        var live = new DisplaySettings
        {
            FontFamily     = "No Such Font,Consolas,Courier New,monospace",
            MonoFontFamily = "No Such Mono,Courier New,monospace",
        };

        // OK without touching the fonts: the full lists survive (they used to be
        // cut down to the first name, losing every fallback).
        var dlg = new DisplaySettingsViewModel(live);
        dlg.OkCommand.Execute().Subscribe();
        Assert.Equal("No Such Font,Consolas,Courier New,monospace", live.FontFamily);
        Assert.Equal("No Such Mono,Courier New,monospace", live.MonoFontFamily);

        // Picking a font saves that font.
        dlg = new DisplaySettingsViewModel(live);
        dlg.MonoFontFamily = "DejaVu Sans Mono";
        dlg.MonoFontSize   = 10;
        dlg.OkCommand.Execute().Subscribe();
        Assert.Equal("DejaVu Sans Mono", live.MonoFontFamily);
        Assert.Equal(10, live.MonoFontSize);

        // Reset brings back the default lists, not just their first names.
        dlg = new DisplaySettingsViewModel(live);
        dlg.ResetCommand.Execute().Subscribe();
        dlg.OkCommand.Execute().Subscribe();
        Assert.Equal(new DisplaySettings().FontFamily, live.FontFamily);
        Assert.Equal(DisplaySettings.DefaultMonoFontFamily, live.MonoFontFamily);
        Assert.Equal(0, live.MonoFontSize);
    }
}
