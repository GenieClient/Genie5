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

    private sealed record TargetChoice(string Id, string Title)
    {
        public override string ToString() => Title;
    }

    /// <summary>What the grid offers. "Beside another window" is the
    /// <see cref="ServerDialogMode.ExistingWindow"/> answer: the dialog opens as
    /// a tab in the same group as the window picked in the second box. The
    /// first-seen chooser does not offer it — it has no room for a picker.</summary>
    private static readonly ModeChoice[] Choices =
    {
        new(ServerDialogMode.NewWindow,       "Its own window"),
        new(ServerDialogMode.WhereDrProposes, "Where DR suggests"),
        new(ServerDialogMode.ExistingWindow,  "Beside another window"),
        new(ServerDialogMode.Ignore,          "Never show it"),
    };

    private ServerDialogMappings? _mappings;
    private Action?               _onChanged;
    private GenieConfig?          _config;
    private Action?               _onConfigChanged;
    private Func<string?, IReadOnlyList<(string Id, string Title)>>? _targets;
    private bool                  _loadingMaster;

    public ServerDialogsPanel()
    {
        InitializeComponent();
        ModeBox.ItemsSource = Choices;
    }

    public void Initialize(ServerDialogMappings? mappings, Action? onChanged,
                           GenieConfig? config, Action? onConfigChanged,
                           Func<string?, IReadOnlyList<(string Id, string Title)>>? targetWindows = null)
    {
        _mappings        = mappings;
        _onChanged       = onChanged;
        _config          = config;
        _onConfigChanged = onConfigChanged;
        _targets         = targetWindows;

        _loadingMaster          = true;
        MasterCheck.IsEnabled   = config is not null;
        MasterCheck.IsChecked   = config?.ServerDialogs ?? true;
        MasterHint.IsVisible    = config is null;
        _loadingMaster          = false;

        ClearForm();
        Refresh();
    }

    /// <summary>The Where column. <paramref name="titleOf"/> turns a target's
    /// dock id into its window title; an id it doesn't know is shown as-is (a
    /// plugin window that hasn't opened this session, say).</summary>
    public static string DescribeMode(ServerDialogMapping m, Func<string, string?>? titleOf = null) => m.Mode switch
    {
        ServerDialogMode.NewWindow       => "Its own window",
        ServerDialogMode.WhereDrProposes => "Where DR suggests",
        ServerDialogMode.ExistingWindow  => string.IsNullOrWhiteSpace(m.Target)
                                                ? "Beside another window"
                                                : $"Beside {titleOf?.Invoke(m.Target!) ?? m.Target}",
        ServerDialogMode.Ignore          => "Never show it",
        _                                => m.Mode.ToString(),
    };

    public static MappingRow ToRow(ServerDialogMapping m, Func<string, string?>? titleOf = null) => new(
        m.Id,
        string.IsNullOrWhiteSpace(m.Title) ? m.Id : m.Title!,
        DescribeMode(m, titleOf),
        m.Mode == ServerDialogMode.Ignore ? "—" : m.AutoOpen ? "✓" : "✗");

    private IReadOnlyList<TargetChoice> TargetsFor(string? dialogId) =>
        (_targets?.Invoke(dialogId) ?? Array.Empty<(string, string)>())
            .Select(t => new TargetChoice(t.Id, t.Title))
            .ToList();

    private string? TitleOf(string targetId) =>
        TargetsFor(null).FirstOrDefault(t => string.Equals(t.Id, targetId, StringComparison.OrdinalIgnoreCase))?.Title;

    private void Refresh()
    {
        var keep = (ItemsList.SelectedItem as MappingRow)?.Id;
        var rows = _mappings?.All().Select(m => ToRow(m, TitleOf)).ToList() ?? new List<MappingRow>();
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

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_mappings is null || ItemsList.SelectedItem is not MappingRow row) return;
        var m = _mappings.Find(row.Id);
        if (m is null) return;

        var targets = TargetsFor(m.Id).ToList();
        // Keep a saved target the list doesn't know (a window not created this
        // session) selectable, so Save doesn't silently drop it.
        if (!string.IsNullOrWhiteSpace(m.Target)
            && !targets.Any(t => string.Equals(t.Id, m.Target, StringComparison.OrdinalIgnoreCase)))
            targets.Insert(0, new TargetChoice(m.Target!, m.Target!));
        TargetBox.ItemsSource  = targets;
        TargetBox.SelectedItem = targets.FirstOrDefault(t =>
            string.Equals(t.Id, m.Target, StringComparison.OrdinalIgnoreCase));

        ModeBox.SelectedItem    = Choices.First(c => c.Mode == m.Mode);
        ModeBox.IsEnabled       = true;
        AutoOpenCheck.IsChecked = m.AutoOpen;
        AutoOpenCheck.IsEnabled = true;
        StatusText.Text         = $"Dialog id: {m.Id}";
    }

    private void OnModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        TargetBox.IsVisible = ModeBox.SelectedItem is ModeChoice { Mode: ServerDialogMode.ExistingWindow };
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (_mappings is null) return;
        if (ItemsList.SelectedItem is not MappingRow row) { StatusText.Text = "Select a dialog first."; return; }
        if (ModeBox.SelectedItem is not ModeChoice choice) return;

        string? target = null;
        if (choice.Mode == ServerDialogMode.ExistingWindow)
        {
            if (TargetBox.SelectedItem is not TargetChoice t)
            {
                StatusText.Text = "Pick the window to put it beside.";
                return;
            }
            target = t.Id;
        }

        var m = _mappings.Find(row.Id);
        if (m is null) { Refresh(); return; }

        m.Mode     = choice.Mode;
        m.Target   = target;
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
        ModeBox.SelectedItem    = null;
        ModeBox.IsEnabled       = false;
        TargetBox.ItemsSource   = null;
        TargetBox.IsVisible     = false;
        AutoOpenCheck.IsChecked = true;
        AutoOpenCheck.IsEnabled = false;
        StatusText.Text         = string.Empty;
    }
}
