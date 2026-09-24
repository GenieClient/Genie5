using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Genie.App.Views;
using Genie.Core.Config;
using Xunit;

namespace Genie.App.HeadlessTests;

/// <summary>
/// Public #369 — the Text-to-Speech tab pointed at commands for both of its setup
/// steps (<c>#tts install</c>, <c>#config ttsvoicedir</c>) while offering a control
/// for neither, and explained its disabled offline state with one grey line under
/// the sliders.
/// </summary>
public sealed class TtsPanelHeadlessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "genie_ttspanel_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }

    private GenieConfig NewConfig()
    {
        var lds = new Genie.Core.Runtime.LocalDirectoryService("GenieTtsPanelTest", _root);
        lds.UseExplicitRoot(_root);
        return new GenieConfig(lds);
    }

    private static (TtsPanel Panel, Window Window) Host()
    {
        var panel = new TtsPanel();
        var window = new Window { Width = 600, Height = 800, Content = panel };
        window.Show();
        return (panel, window);
    }

    private static T Find<T>(TtsPanel p, string name) where T : Control => p.FindControl<T>(name)!;

    private static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [AvaloniaFact]
    public void Offline_the_tab_says_why_at_the_top_not_in_the_bottom_status_line()
    {
        var (panel, window) = Host();
        panel.Initialize(null);

        Assert.False(panel.IsEnabled);
        Assert.True(Find<Border>(panel, "OfflineBanner").IsVisible);
        Assert.Equal("", Find<TextBlock>(panel, "StatusText").Text ?? "");
        window.Close();
    }

    [AvaloniaFact]
    public void Connected_the_banner_hides_and_the_voice_folder_is_shown()
    {
        var cfg = NewConfig();
        var (panel, window) = Host();
        panel.Initialize(cfg);

        Assert.True(panel.IsEnabled);
        Assert.False(Find<Border>(panel, "OfflineBanner").IsVisible);
        Assert.Equal(cfg.TtsVoiceDir, Find<TextBox>(panel, "VoiceDirBox").Text);
        Assert.False(Find<Button>(panel, "InstallButton").IsEnabled);   // no installer wired
        Assert.Contains("Install voice", Find<TextBlock>(panel, "VoiceHint").Text);
        window.Close();
    }

    [AvaloniaFact]
    public void Default_button_resets_the_voice_folder_through_the_config_key()
    {
        var cfg = NewConfig();
        var custom = Path.Combine(_root, "MyVoices");
        Directory.CreateDirectory(custom);
        cfg.SetSetting("ttsvoicedir", custom);
        int changed = 0, voiceChanged = 0;
        var (panel, window) = Host();
        panel.Initialize(cfg, onChanged: () => changed++, voiceChanged: () => voiceChanged++);
        Assert.Equal(custom, Find<TextBox>(panel, "VoiceDirBox").Text);

        Click(Find<Button>(panel, "DefaultVoiceDirButton"));

        Assert.Equal("Voices", cfg.TtsVoiceDirRaw);
        Assert.Equal(cfg.TtsVoiceDir, Find<TextBox>(panel, "VoiceDirBox").Text);
        Assert.Equal(1, changed);        // persisted, like every other tab edit
        Assert.Equal(1, voiceChanged);   // cached synth engine dropped
        window.Close();
    }

    [AvaloniaFact]
    public async Task Install_button_runs_the_installer_and_reports_the_outcome()
    {
        var cfg = NewConfig();
        int calls = 0;
        var (panel, window) = Host();
        panel.Initialize(cfg, installVoice: () => { calls++; return Task.FromResult(true); });
        var install = Find<Button>(panel, "InstallButton");
        Assert.True(install.IsEnabled);

        Click(install);
        for (int i = 0; i < 20 && calls == 0; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, calls);
        Assert.StartsWith("Installed", Find<TextBlock>(panel, "StatusText").Text);
        Assert.True(install.IsEnabled);   // re-enabled once the download settles
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_failed_install_says_where_to_look()
    {
        var cfg = NewConfig();
        var (panel, window) = Host();
        panel.Initialize(cfg, installVoice: () => Task.FromResult(false));

        Click(Find<Button>(panel, "InstallButton"));
        for (int i = 0; i < 20; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(5); }

        Assert.Contains("game window", Find<TextBlock>(panel, "StatusText").Text);
        window.Close();
    }
}
