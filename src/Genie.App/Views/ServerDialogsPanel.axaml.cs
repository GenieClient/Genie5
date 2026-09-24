using Avalonia.Controls;
using Avalonia.Interactivity;
using Genie.Core.Config;
using Genie.Core.Dialogs;

namespace Genie.App.Views;

/// <summary>
/// Server Dialogs settings grid (#156) — review, change or forget the answers
/// the first-seen chooser recorded in <c>dialogmappings.json</c>, plus the
/// <c>serverdialogs</c> master switch.
///
/// <para>Edits go straight to the <see cref="ServerDialogMappings"/> it is
/// handed (the live table while editing the connected profile, a draft loaded
/// from that profile's file otherwise); the live table raises
/// <see cref="ServerDialogMappings.Changed"/>, which is how an open window
/// picks up an edit without waiting for DR.</para>
/// </summary>
public partial class ServerDialogsPanel : UserControl
{
    public sealed record MappingRow(string Id, string Title, string Where, string AutoOpen);

    private sealed record ModeChoice(ServerDialogMode Mode, string Label)
    {
        public override string ToString() => Label;
    }

    /// <summary>What the chooser offers, in the chooser's own words.
    /// <see cref="ServerDialogMode.ExistingWindow"/> is not offered: the
    /// renderer does not yet draw a dialog inside another window, so picking
    /// it would change nothing on screen. A hand-edited one still shows (see
    /// <see cref="ChoicesFor"/>) so saving doesn't silently rewrite it.</summary>
    private static readonly ModeChoice[] Choices =
    {
        new(ServerDialogMode.NewWindow,       "Its own window"),
        new(ServerDialogMode.WhereDrProposes, "Where DR suggests"),
        new(ServerDialogMode.Ignore,          "Never show it"),
    };

    private ServerDialogMappings? _mappings;
    private Action?               _onChanged;
    private GenieConfig?          _config;
    private Action?               _onConfigChanged;
    private bool                  _loadingMaster;

    public ServerDialogsPanel()
    {
        InitializeComponent();
        ModeBox.ItemsSource = Choices;
    }

    public void Initialize(ServerDialogMappings? mappings, Action? onChanged,
                           GenieConfig? config, Action? onConfigChanged)
    {
        _mappings        = mappings;
        _onChanged       = onChanged;
        _config          = config;
        _onConfigChanged = onConfigChanged;

        _loadingMaster          = true;
        MasterCheck.IsEnabled   = config is not null;
        MasterCheck.IsChecked   = config?.ServerDialogs ?? true;
        MasterHint.IsVisible    = config is null;
        _loadingMaster          = false;

        ClearForm();
        Refresh();
    }

    public static string DescribeMode(ServerDialogMapping m) => m.Mode switch
    {
        ServerDialogMode.NewWindow       => "Its own window",
        ServerDialogMode.WhereDrProposes => "Where DR suggests",
        ServerDialogMode.ExistingWindow  => string.IsNullOrWhiteSpace(m.Target)
                                                ? "Existing window"
                                                : $"Existing window: {m.Target}",
        ServerDialogMode.Ignore          => "Never show it",
        _                                => m.Mode.ToString(),
    };

    public static MappingRow ToRow(ServerDialogMapping m) => new(
        m.Id,
        string.IsNullOrWhiteSpace(m.Title) ? m.Id : m.Title!,
        DescribeMode(m),
        m.Mode == ServerDialogMode.Ignore ? "—" : m.AutoOpen ? "✓" : "✗");

    private void Refresh()
    {
        var keep = (ItemsList.SelectedItem as MappingRow)?.Id;
        var rows = _mappings?.All().Select(ToRow).ToList() ?? new List<MappingRow>();
        ItemsList.ItemsSource = rows;

        if (keep is not null)
        {
            var restored = rows.FirstOrDefault(r => string.Equals(r.Id, keep, StringComparison.OrdinalIgnoreCase));
            if (restored is not null) ItemsList.SelectedItem = restored;
            else ClearForm();
        }
        if (rows.Count == 0)
            StatusText.Text = "No answers saved for this profile yet.";
    }

    private static IReadOnlyList<ModeChoice> ChoicesFor(ServerDialogMapping m) =>
        m.Mode == ServerDialogMode.ExistingWindow
            ? Choices.Append(new ModeChoice(ServerDialogMode.ExistingWindow,
                  $"{DescribeMode(m)} (set by hand; shows as its own window for now)")).ToArray()
            : Choices;

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_mappings is null || ItemsList.SelectedItem is not MappingRow row) return;
        var m = _mappings.Find(row.Id);
        if (m is null) return;

        var choices = ChoicesFor(m);
        ModeBox.ItemsSource    = choices;
        ModeBox.SelectedItem   = choices.First(c => c.Mode == m.Mode);
        ModeBox.IsEnabled      = true;
        AutoOpenCheck.IsChecked = m.AutoOpen;
        AutoOpenCheck.IsEnabled = true;
        StatusText.Text        = $"Dialog id: {m.Id}";
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (_mappings is null) return;
        if (ItemsList.SelectedItem is not MappingRow row) { StatusText.Text = "Select a dialog first."; return; }
        if (ModeBox.SelectedItem is not ModeChoice choice) return;

        var m = _mappings.Find(row.Id);
        if (m is null) { Refresh(); return; }

        m.Mode     = choice.Mode;
        m.AutoOpen = AutoOpenCheck.IsChecked == true;
        _mappings.Set(m);
        _onChanged?.Invoke();
        Refresh();
        StatusText.Text = "Saved.";
    }

    private void OnForget(object? sender, RoutedEventArgs e)
    {
        if (_mappings is null) return;
        if (ItemsList.SelectedItem is not MappingRow row) { StatusText.Text = "Select a dialog first."; return; }

        _mappings.Remove(row.Id);
        _onChanged?.Invoke();
        ClearForm();
        Refresh();
        StatusText.Text = $"Forgot {row.Title}. Genie will ask again the next time DragonRealms sends it.";
    }

    private void OnMasterChanged(object? sender, RoutedEventArgs e)
    {
        if (_loadingMaster || _config is null) return;
        // Through SetSetting so ConfigChanged fires — the host hides or
        // re-renders the dialog windows on it, same as a typed #config.
        _config.SetSetting("serverdialogs", MasterCheck.IsChecked == true ? "on" : "off",
                           showException: false);
        _onConfigChanged?.Invoke();
    }

    private void ClearForm()
    {
        ItemsList.SelectedItem  = null;
        ModeBox.ItemsSource     = Choices;
        ModeBox.SelectedItem    = null;
        ModeBox.IsEnabled       = false;
        AutoOpenCheck.IsChecked = true;
        AutoOpenCheck.IsEnabled = false;
        StatusText.Text         = string.Empty;
    }
}
