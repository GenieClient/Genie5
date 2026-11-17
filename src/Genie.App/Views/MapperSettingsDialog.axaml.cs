using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Genie.App.ViewModels;
using Genie.Core.Config;

namespace Genie.App.Views;

/// <summary>
/// Help ▸ … / Maps ▸ AutoMapper Settings (#147). A slim, no-view-model dialog that
/// gathers the auto-mapper / auto-walk settings that were otherwise only reachable
/// via <c>#config</c> or the mapper panel into one place. Everything applies live
/// and persists through the standard stores (settings.cfg for the config keys,
/// display.json for the map colours) — there is no OK/Cancel, just Close.
/// </summary>
public partial class MapperSettingsDialog : Window
{
    private readonly GenieConfig?      _config;
    private readonly MapperViewModel?  _mapper;
    private bool _loading;

    public MapperSettingsDialog() { InitializeComponent(); }

    public MapperSettingsDialog(GenieConfig config, MapperViewModel mapper) : this()
    {
        _config = config;
        _mapper = mapper;

        _loading = true;
        EnableCheck.IsChecked       = config.AutoMapper;
        RecordCheck.IsChecked       = config.AutoMapperRecord;
        AlphaSlider.Value           = config.AutoMapperAlpha;
        AlphaValue.Text             = config.AutoMapperAlpha.ToString();
        PauseUnfocusCheck.IsChecked = config.AutoWalkPauseOnUnfocus;
        UnfocusSecondsBox.Value     = config.AutoWalkUnfocusSeconds;
        BgPicker.Color              = mapper.MapBackground;
        TextPicker.Color            = mapper.MapTextColor;
        _loading = false;

        EnableCheck.IsCheckedChanged       += (_, _) => ApplyConfig();
        RecordCheck.IsCheckedChanged       += (_, _) => ApplyConfig();
        PauseUnfocusCheck.IsCheckedChanged += (_, _) => ApplyConfig();
        UnfocusSecondsBox.ValueChanged     += (_, _) => ApplyConfig();
        AlphaSlider.PropertyChanged        += (_, e) =>
        {
            if (e.Property != Slider.ValueProperty) return;
            AlphaValue.Text = ((int)AlphaSlider.Value).ToString();
            ApplyConfig();
        };
        BgPicker.ColorChanged   += (_, _) => ApplyColors();
        TextPicker.ColorChanged += (_, _) => ApplyColors();

        // Persist settings.cfg once on close rather than on every slider tick.
        Closed += (_, _) => { try { _config?.Save(); } catch { /* best effort */ } };
    }

    /// <summary>Push the config-backed controls to the live GenieConfig — each
    /// SetSetting notifies subscribers so the change takes effect at once. The
    /// file write is deferred to close (see the Closed handler).</summary>
    private void ApplyConfig()
    {
        if (_loading || _config is null) return;
        _config.SetSetting("automapper",             (EnableCheck.IsChecked == true).ToString(),      showException: false);
        _config.SetSetting("automapperrecord",       (RecordCheck.IsChecked == true).ToString(),      showException: false);
        _config.SetSetting("automapperalpha",        ((int)AlphaSlider.Value).ToString(),             showException: false);
        _config.SetSetting("autowalkpauseonunfocus", (PauseUnfocusCheck.IsChecked == true).ToString(), showException: false);
        _config.SetSetting("autowalkunfocusseconds", ((int)(UnfocusSecondsBox.Value ?? 60)).ToString(), showException: false);
    }

    /// <summary>Map colours live on the mapper VM, which persists them to
    /// display.json and repaints the canvas via its own subscriptions.</summary>
    private void ApplyColors()
    {
        if (_loading || _mapper is null) return;
        _mapper.MapBackground = BgPicker.Color;
        _mapper.MapTextColor  = TextPicker.Color;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
