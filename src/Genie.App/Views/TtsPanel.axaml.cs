using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Genie.Core.Config;

namespace Genie.App.Views;

/// <summary>
/// Text-to-speech settings editor (the "Text-to-Speech" tab of the
/// Configuration dialog). Binds the TTS fields of the global
/// <see cref="GenieConfig"/> — the same <c>ttsread</c> / <c>ttsreadstreams</c> /
/// <c>ttsstreampriority</c> / voice / rate / volume keys that <c>#tts</c> and
/// <c>#config</c> edit — so the tab is profile-independent. Edits apply live
/// (the read-aloud dispatch reads config per utterance) and persist via the
/// callback, matching how <c>#tts</c> behaves.
/// </summary>
public partial class TtsPanel : UserControl
{
    private GenieConfig?    _config;
    private Action?         _onChanged;
    private Action<string>? _speakSample;
    private Action?         _voiceChanged;
    private bool            _loading;

    private readonly List<(string Id, CheckBox Check, ComboBox Priority)> _rows = new();
    private readonly Avalonia.Threading.DispatcherTimer _sliderSave;

    /// <summary>Streams offered as first-class rows — the DR text streams the
    /// parser routes, plus the main game window. Custom/plugin stream ids live
    /// in the free-text box instead so they survive round-trips.</summary>
    private static readonly (string Id, string Label, string? Hint)[] KnownStreams =
    {
        ("main",         "Main (game window)", "Speech and whispers are duplicated into main — reading main plus talk/whispers speaks them twice."),
        ("talk",         "Talk",         null),
        ("whispers",     "Whispers",     null),
        ("thoughts",     "Thoughts",     null),
        ("combat",       "Combat",       null),
        ("logons",       "Logons",       null),
        ("death",        "Deaths",       null),
        ("familiar",     "Familiar",     null),
        ("assess",       "Assess",       null),
        ("atmospherics", "Atmospherics", null),
        ("ooc",          "OOC",          "An OOC message is also sent on whispers — reading OOC plus whispers speaks it twice."),
        ("log",          "Log",          null),
        ("itemlog",      "ItemLog",      null),
    };

    private static readonly string[] PriorityChoices = { "Default", "Low", "Normal", "High" };

    public TtsPanel()
    {
        InitializeComponent();
        BuildStreamRows();

        // Sliders fire per-pixel while dragging — update the live config each
        // tick (cheap) but debounce the settings.cfg write.
        _sliderSave = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _sliderSave.Tick += (_, _) => { _sliderSave.Stop(); _onChanged?.Invoke(); };

        ReadCheck.IsCheckedChanged += (_, _) =>
        {
            if (_loading || _config is null) return;
            _config.TtsRead = ReadCheck.IsChecked == true;
            _onChanged?.Invoke();
        };

        OtherStreamsBox.LostFocus += (_, _) => CommitStreamSet();
        OtherStreamsBox.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter) { CommitStreamSet(); e.Handled = true; }
        };

        VoiceCombo.SelectionChanged += (_, _) =>
        {
            if (_loading || _config is null || VoiceCombo.SelectedItem is not VoiceItem v) return;
            _config.TtsVoice = v.Id;
            _voiceChanged?.Invoke();     // drop the cached synth engine
            _onChanged?.Invoke();
            StatusText.Text = $"Voice: {v.Display}. Applies from the next spoken line.";
        };

        RateSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property != Slider.ValueProperty || _loading || _config is null) return;
            _config.SetSetting("ttsrate",
                RateSlider.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
                showException: false);
            RateValue.Text = FormatRate(_config.TtsRate);
            _sliderSave.Stop(); _sliderSave.Start();
        };

        VolumeSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property != Slider.ValueProperty || _loading || _config is null) return;
            _config.SetSetting("ttsvolume",
                ((int)Math.Round(VolumeSlider.Value)).ToString(),
                showException: false);
            VolumeValue.Text = $"{_config.TtsVolume}%";
            _sliderSave.Stop(); _sliderSave.Start();
        };
    }

    /// <summary>
    /// Hand the panel the live <see cref="GenieConfig"/> plus persistence and
    /// TTS hooks. A null config means no connected core yet — the form disables
    /// itself with a hint, mirroring the Scripts tab. <paramref name="speakSample"/>
    /// and <paramref name="voiceChanged"/> come from the main window (which owns
    /// the TtsService); null just disables the Test button.
    /// </summary>
    public void Initialize(GenieConfig? config, Action? onChanged = null,
                           Action<string>? speakSample = null, Action? voiceChanged = null)
    {
        _config       = config;
        _onChanged    = onChanged;
        _speakSample  = speakSample;
        _voiceChanged = voiceChanged;

        IsEnabled = config is not null;
        if (config is null)
        {
            StatusText.Text = "Connect to a game first — TTS settings load with the session.";
            return;
        }

        LoadForm(config);
        StatusText.Text = string.Empty;
    }

    private void BuildStreamRows()
    {
        foreach (var (id, label, hint) in KnownStreams)
        {
            var check = new CheckBox { Content = label, Width = 220 };
            if (hint is not null) ToolTip.SetTip(check, hint);
            AutomationProperties.SetName(check, $"Read {label} aloud");
            check.IsCheckedChanged += (_, _) =>
            {
                if (_loading || _config is null) return;
                CommitStreamSet();
            };

            var combo = new ComboBox { Width = 110, ItemsSource = PriorityChoices, SelectedIndex = 0 };
            ToolTip.SetTip(combo,
                $"Read-aloud urgency (default = {GenieConfig.TtsDefaultUrgencyFor(id).ToString().ToLowerInvariant()}).");
            AutomationProperties.SetName(combo, $"{label} priority");
            combo.SelectionChanged += (_, _) =>
            {
                if (_loading || _config is null) return;
                OnStreamPriorityChanged(id, combo.SelectedIndex);
            };

            var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(check);
            row.Children.Add(combo);
            StreamRows.Children.Add(row);
            _rows.Add((id, check, combo));
        }
    }

    private void LoadForm(GenieConfig c)
    {
        _loading = true;
        try
        {
            ReadCheck.IsChecked = c.TtsRead;

            var read      = SplitStreams(c.TtsReadStreamsRaw);
            var overrides = c.TtsStreamPriorityOverrides();
            foreach (var (id, check, combo) in _rows)
            {
                check.IsChecked = read.Contains(id);
                combo.SelectedIndex = overrides.TryGetValue(id, out var u)
                    ? u switch { TtsUrgency.Low => 1, TtsUrgency.Normal => 2, _ => 3 }
                    : 0;
            }

            var known = _rows.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
            OtherStreamsBox.Text = string.Join(",", read.Where(s => !known.Contains(s)));

            LoadVoices(c);

            RateSlider.Value   = c.TtsRate;
            VolumeSlider.Value = c.TtsVolume;
            RateValue.Text     = FormatRate(c.TtsRate);
            VolumeValue.Text   = $"{c.TtsVolume}%";
        }
        finally { _loading = false; }
    }

    private void LoadVoices(GenieConfig c)
    {
        var items = new List<VoiceItem>();
        string voiceDir = c.TtsVoiceDir;

        foreach (var v in Services.VoiceCatalog.All)
            if (Services.VoiceInstaller.IsInstalled(System.IO.Path.Combine(voiceDir, v.Id)))
                items.Add(new VoiceItem(v.Id, v.DisplayName));

        // Raw voice folders installed by hand (matches #tts use accepting a
        // folder name that isn't in the catalog).
        try
        {
            if (System.IO.Directory.Exists(voiceDir))
                foreach (var sub in System.IO.Directory.GetDirectories(voiceDir))
                {
                    string name = System.IO.Path.GetFileName(sub);
                    if (Services.VoiceInstaller.IsInstalled(sub) &&
                        !items.Any(i => string.Equals(i.Id, name, StringComparison.OrdinalIgnoreCase)))
                        items.Add(new VoiceItem(name, name));
                }
        }
        catch { /* unreadable voice dir — the catalog list still stands */ }

        VoiceCombo.ItemsSource = items;
        VoiceCombo.SelectedItem =
            items.FirstOrDefault(i => string.Equals(i.Id, c.TtsVoice, StringComparison.OrdinalIgnoreCase))
            ?? items.FirstOrDefault();   // empty TtsVoice = first installed, like TtsService

        bool any = items.Count > 0;
        VoiceCombo.IsEnabled = any;
        TestButton.IsEnabled = any && _speakSample is not null;
        VoiceHint.Text = any
            ? ""
            : "No voices installed — run #tts install to download a free offline voice.";
    }

    /// <summary>Rebuild <c>ttsreadstreams</c> from the checked known rows plus
    /// the free-text box — a merge, never a regenerate, so custom stream ids
    /// the panel doesn't know about survive a visit to this tab.</summary>
    private void CommitStreamSet()
    {
        if (_loading || _config is null) return;
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (id, check, _) in _rows)
            if (check.IsChecked == true) set.Add(id);
        foreach (var s in SplitStreams(OtherStreamsBox.Text ?? ""))
            set.Add(s);
        _config.TtsReadStreamsRaw = string.Join(",", set);
        _onChanged?.Invoke();
    }

    private void OnStreamPriorityChanged(string id, int index)
    {
        if (_config is null) return;
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in _config.TtsStreamPriorityOverrides())
            map[kv.Key] = kv.Value.ToString().ToLowerInvariant();
        if (index <= 0) map.Remove(id);
        else map[id] = index switch { 1 => "low", 2 => "normal", _ => "high" };
        _config.TtsStreamPriorityRaw = string.Join(",", map.Select(kv => $"{kv.Key}:{kv.Value}"));
        _onChanged?.Invoke();
    }

    private void OnTest(object? sender, RoutedEventArgs e)
    {
        if (_speakSample is null)
        {
            StatusText.Text = "TTS isn't available — open Configuration from a running session.";
            return;
        }
        _speakSample("Genie text to speech. Whispers barge in; background streams yield.");
    }

    private static SortedSet<string> SplitStreams(string csv) =>
        new(csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
               .Select(s => s.ToLowerInvariant()),
            StringComparer.Ordinal);

    private static string FormatRate(double rate) =>
        rate.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    private sealed record VoiceItem(string Id, string Display)
    {
        public override string ToString() => Display;
    }
}
