using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Threading;
using Genie.Core.Update;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace Genie.App.ViewModels;

/// <summary>
/// One row in the Updates dialog's Maps or Plugins tab. Wraps a
/// <see cref="FeedEntry"/> with per-row UI state (status text, busy flag,
/// enabled toggle) and the three actions the user can take on it from the
/// row's own buttons: Check (cheap availability poll), Update (download +
/// apply), and Remove (delete from feed config).
///
/// The actual update / check / persistence logic lives in the parent
/// <see cref="UpdatesDialogViewModel"/>; this row receives delegates instead
/// of services so it stays free of feed-kind dispatch (github-releases vs.
/// github-contents) and config I/O.
/// </summary>
public sealed class UpdateFeedRow : ReactiveObject
{
    private readonly FeedEntry                                                _feed;
    private readonly Func<FeedEntry, CancellationToken, Task<(string Status, string? Details)>> _check;
    private readonly Func<FeedEntry, IProgress<UpdateProgress>?, CancellationToken, Task<string>> _apply;
    private readonly Action<FeedEntry, bool>                                  _toggle;
    private readonly Action<FeedEntry>                                        _remove;

    public string  Name         => _feed.Name;
    public string  Kind         => _feed.Kind;
    /// <summary>Owner/Repo for github-* sources, the URL for http-manifest.</summary>
    public string  Source       => string.IsNullOrEmpty(_feed.Owner)
                                       ? _feed.ManifestUrl
                                       : $"{_feed.Owner}/{_feed.Repo}";
    public string  AssetPattern => string.IsNullOrEmpty(_feed.AssetPattern)
                                       ? "(all files)"
                                       : _feed.AssetPattern;

    /// <summary>
    /// Persists immediately when the user toggles the row's checkbox — the
    /// setter calls into the toggle delegate which writes the config and
    /// updates the underlying FeedEntry.
    /// </summary>
    [Reactive] public bool   Enabled  { get; set; }

    [Reactive] public string Status   { get; private set; } = "";
    [Reactive] public bool   IsBusy   { get; private set; }

    /// <summary>Hover text for the status line — the per-file list from the
    /// last Check (which files are new / changed). Null hides the tooltip;
    /// cleared on Update since the list is stale once files are pulled.</summary>
    [Reactive] public string? StatusDetails { get; private set; }

    /// <summary>0–100 fill for the row's inline progress bar.</summary>
    [Reactive] public double Progress              { get; private set; }

    /// <summary>True for unmeasured phases (plugin download, maps file-listing,
    /// or a Check poll) — the bar renders as a marquee instead of a fill.</summary>
    [Reactive] public bool   ProgressIndeterminate { get; private set; }

    /// <summary>Gates the inline bar's visibility — shown only while this row is
    /// checking or updating, hidden at rest.</summary>
    [Reactive] public bool   ProgressActive        { get; private set; }

    public ReactiveCommand<Unit, Unit> CheckCommand  { get; }
    public ReactiveCommand<Unit, Unit> UpdateCommand { get; }
    public ReactiveCommand<Unit, Unit> RemoveCommand { get; }

    /// <summary>"Files…" — pick which files to update / ignore. Only Scripts
    /// rows supply a chooser; the button is hidden otherwise.</summary>
    public ReactiveCommand<Unit, Unit> FilesCommand  { get; }
    public bool                        HasFileChooser => _chooseFiles is not null;

    private readonly Func<FeedEntry, IProgress<UpdateProgress>?, Task<string>>? _chooseFiles;

    // ── Maps rows only: repo subfolder toggles ───────────────────────────

    /// <summary>Maps rows carry two extra checkboxes — pull the repo's
    /// festival-maps subfolder, pull its quest-maps subfolder — both off by
    /// default (quest maps are spoilers). Hidden on Plugins / Scripts rows.</summary>
    public bool HasMapFolderOptions => _saveMapFolders is not null;

    /// <summary>Bound to the "Festivals" checkbox; persists on change via
    /// <see cref="FeedEntry.IncludeFestivalMaps"/>.</summary>
    [Reactive] public bool IncludeFestivalMaps { get; set; }

    /// <summary>Bound to the "Quests (spoilers)" checkbox; persists on change via
    /// <see cref="FeedEntry.IncludeQuestMaps"/>.</summary>
    [Reactive] public bool IncludeQuestMaps    { get; set; }

    private readonly Action<FeedEntry>? _saveMapFolders;

    public UpdateFeedRow(
        FeedEntry                                                                feed,
        Func<FeedEntry, CancellationToken, Task<(string Status, string? Details)>> check,
        Func<FeedEntry, IProgress<UpdateProgress>?, CancellationToken, Task<string>> apply,
        Action<FeedEntry, bool>                                                  toggle,
        Action<FeedEntry>                                                        remove,
        Func<FeedEntry, IProgress<UpdateProgress>?, Task<string>>?               chooseFiles    = null,
        Action<FeedEntry>?                                                       saveMapFolders = null)
    {
        _feed    = feed;
        _check   = check;
        _apply   = apply;
        _toggle  = toggle;
        _remove  = remove;
        _chooseFiles    = chooseFiles;
        _saveMapFolders = saveMapFolders;
        Enabled  = feed.Enabled;
        IncludeFestivalMaps = feed.IncludeFestivalMaps;
        IncludeQuestMaps    = feed.IncludeQuestMaps;

        // Toggle-on-change: persist whenever the checkbox flips. Skip the
        // initial value (just set above) by sub-after-construct via Skip(1).
        this.WhenAnyValue(x => x.Enabled)
            .Skip(1)
            .Subscribe(v => _toggle(_feed, v));

        // Same shape for the Maps-only subfolder checkboxes: write the flag
        // onto the FeedEntry, then let the parent persist the whole config.
        if (_saveMapFolders is not null)
        {
            this.WhenAnyValue(x => x.IncludeFestivalMaps)
                .Skip(1)
                .Subscribe(v => { _feed.IncludeFestivalMaps = v; _saveMapFolders(_feed); });
            this.WhenAnyValue(x => x.IncludeQuestMaps)
                .Skip(1)
                .Subscribe(v => { _feed.IncludeQuestMaps = v; _saveMapFolders(_feed); });
        }

        var notBusy = this.WhenAnyValue(x => x.IsBusy).Select(b => !b);

        CheckCommand  = ReactiveCommand.CreateFromTask(RunCheck,  notBusy);
        UpdateCommand = ReactiveCommand.CreateFromTask(RunUpdate, notBusy);
        RemoveCommand = ReactiveCommand.Create(() => _remove(_feed), notBusy);
        FilesCommand  = ReactiveCommand.CreateFromTask(RunChooseFiles, notBusy);
    }

    /// <summary>Open the file chooser; if the user hits "Update Selected" the
    /// chooser applies just those files and we show its progress like a
    /// normal Update.</summary>
    private async Task RunChooseFiles()
    {
        if (_chooseFiles is null) return;
        IsBusy         = true;
        ProgressActive = true;
        ProgressIndeterminate = true;
        try
        {
            Status        = "Loading file list…";
            StatusDetails = null;
            var progress = new Progress<UpdateProgress>(p =>
                Dispatcher.UIThread.Post(() =>
                {
                    ProgressIndeterminate = p.Indeterminate || p.Total <= 0;
                    Progress              = p.Total > 0
                        ? Math.Clamp(p.Current * 100.0 / p.Total, 0, 100)
                        : 0;
                    Status = p.Indeterminate || p.Total <= 1
                        ? $"{p.Item} — {p.Status}"
                        : $"[{p.Current}/{p.Total}] {p.Item} — {p.Status}";
                }));
            Status = await _chooseFiles(_feed, progress);
        }
        catch (Exception ex) { Status = $"Error: {ex.Message}"; }
        finally
        {
            IsBusy         = false;
            ProgressActive = false;
        }
    }

    private async Task RunCheck()
    {
        IsBusy = true;
        // A check is a single cheap poll with no fraction — show a marquee.
        ProgressActive        = true;
        ProgressIndeterminate = true;
        try
        {
            Status        = "Checking…";
            StatusDetails = null;
            (Status, StatusDetails) = await _check(_feed, CancellationToken.None);
        }
        catch (Exception ex) { Status = $"Error: {ex.Message}"; }
        finally
        {
            IsBusy         = false;
            ProgressActive = false;
        }
    }

    private async Task RunUpdate()
    {
        IsBusy         = true;
        ProgressActive = true;
        StatusDetails  = null;
        try
        {
            var progress = new Progress<UpdateProgress>(p =>
                Dispatcher.UIThread.Post(() =>
                {
                    ProgressIndeterminate = p.Indeterminate || p.Total <= 0;
                    Progress              = p.Total > 0
                        ? Math.Clamp(p.Current * 100.0 / p.Total, 0, 100)
                        : 0;
                    // Maps report a real N/M; show it. Plugins run unmeasured
                    // steps where the counter is just 0/1 → omit the noise.
                    Status = p.Indeterminate || p.Total <= 1
                        ? $"{p.Item} — {p.Status}"
                        : $"[{p.Current}/{p.Total}] {p.Item} — {p.Status}";
                }));

            Status = await _apply(_feed, progress, CancellationToken.None);
        }
        catch (Exception ex) { Status = $"Error: {ex.Message}"; }
        finally
        {
            IsBusy         = false;
            ProgressActive = false;
        }
    }
}
