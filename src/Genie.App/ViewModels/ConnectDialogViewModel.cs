using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Genie.Core.Connection;
using Genie.Core.Profiles;
using Microsoft.Extensions.Logging.Abstractions;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace Genie.App.ViewModels;

/// <summary>
/// One of the four DragonRealms server instances visible on the SGE site.
/// <see cref="GameCode"/> is the protocol token sent to eaccess.play.net.
/// </summary>
public sealed record GameInstance(string GameCode, string DisplayName)
{
    public override string ToString() => DisplayName;
}

/// <summary>
/// A selectable connection method for the dialog's mode dropdown. Wraps a
/// <see cref="ConnectionMode"/> with a human-readable label.
/// </summary>
public sealed record ConnectionModeOption(ConnectionMode Mode, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public class ConnectDialogViewModel : ReactiveObject
{
    private readonly ProfileStore? _store;
    private readonly Action?       _onStoreChanged;
    /// <summary>Optional sink for auth/connection failures during character
    /// fetch, so the host can mirror the reason into the persistent Game window
    /// (the dialog's own status line vanishes when it closes).</summary>
    private readonly Action<string>? _onAuthFailure;

    /// <summary>The four DR instances offered by the SGE landing page.</summary>
    public static readonly GameInstance[] Instances =
    [
        new("DR",  "DragonRealms (Prime)"),
        new("DRX", "DragonRealms Platinum"),
        new("DRF", "DragonRealms: The Fallen"),
        new("DRT", "DragonRealms Test"),
    ];

    /// <summary>The connection methods offered by the mode dropdown.</summary>
    public static readonly ConnectionModeOption[] ConnectionModes =
    [
        new(ConnectionMode.DirectSGE, "Direct (SGE login)"),
        new(ConnectionMode.LichProxy, "Lich proxy (local)"),
    ];

    // ── Profile picker ────────────────────────────────────────────────────────

    public ObservableCollection<ConnectionProfile> Profiles { get; } = [];

    [Reactive] public ConnectionProfile? SelectedProfile { get; set; }

    public ReactiveCommand<Unit, Unit> SaveProfileCommand   { get; }
    public ReactiveCommand<Unit, Unit> DeleteProfileCommand { get; }

    /// <summary>
    /// Optional per-profile data folder. Empty = use the default root (AppData,
    /// or beside the exe in portable mode). When set, this profile's data lives
    /// under this folder. See <see cref="ConnectionProfile.DataDirectory"/>.
    /// </summary>
    [Reactive] public string DataDirectory { get; set; } = "";

    // ── Startup layout (per profile) ───────────────────────────────────────
    /// <summary>Sentinel first entry of <see cref="AvailableLayouts"/> —
    /// selecting it stores an empty <see cref="ConnectionProfile.DefaultLayoutName"/>
    /// (keep whatever layout is showing, then the global default rules apply).</summary>
    public const string KeepCurrentLayout = "(keep current layout)";

    /// <summary>Saved-layout names offered for the Layout dropdown: the
    /// sentinel plus the global store's layouts and (when a profile is
    /// selected) that profile's own layouts. Provided by the host window via
    /// the <c>listLayouts</c> constructor callback.</summary>
    public System.Collections.ObjectModel.ObservableCollection<string> AvailableLayouts { get; } = new();

    /// <summary>The chosen startup layout for this profile — applied
    /// automatically on connect (<c>ApplyDefaultLayoutForConnect</c>).</summary>
    [Reactive] public string SelectedLayout { get; set; } = KeepCurrentLayout;

    private readonly Func<ConnectionProfile?, IReadOnlyList<string>>? _listLayouts;

    private void RefreshLayouts(ConnectionProfile? profile)
    {
        AvailableLayouts.Clear();
        AvailableLayouts.Add(KeepCurrentLayout);
        if (_listLayouts is null) return;
        foreach (var n in _listLayouts(profile))
            if (!AvailableLayouts.Contains(n)) AvailableLayouts.Add(n);
    }

    /// <summary>Opens the OS folder picker for <see cref="DataDirectory"/>.
    /// The command raises <see cref="BrowseDataDirRequested"/>; the view does
    /// the actual pick (it needs a TopLevel) and calls
    /// <see cref="SetDataDirectoryFromBrowse"/>.</summary>
    public ReactiveCommand<Unit, Unit> BrowseDataDirCommand { get; }

    /// <summary>Raised when the Browse button next to the data folder is clicked.</summary>
    public event Action? BrowseDataDirRequested;

    // ── Editable fields ───────────────────────────────────────────────────────

    [Reactive] public string        ProfileName { get; set; } = "";
    [Reactive] public GameInstance  Instance    { get; set; } = Instances[0];
    [Reactive] public string        Account     { get; set; } = "";
    [Reactive] public string        Password    { get; set; } = "";

    /// <summary>The preferred character to log in as. May be edited freely;
    /// also bound as the <see cref="ComboBox.SelectedItem"/> of the dropdown.</summary>
    [Reactive] public string        Character   { get; set; } = "";

    // ── Connection mode ───────────────────────────────────────────────────────

    /// <summary>The selected connection method (Direct SGE vs Lich proxy).</summary>
    [Reactive] public ConnectionModeOption SelectedMode { get; set; } = ConnectionModes[0];

    /// <summary>Lich proxy host — the address the local Lich 5 process listens on.
    /// Defaults to loopback; Lich almost always runs on the same machine.</summary>
    [Reactive] public string        LichHost    { get; set; } = "127.0.0.1";

    /// <summary>Lich proxy port (string-bound so the TextBox stays simple; parsed
    /// on connect). Lich prints the actual port in its launch window — it is not
    /// fixed, so this must be editable rather than a baked-in constant.</summary>
    [Reactive] public string        LichPort    { get; set; } = "8000";

    /// <summary>True when the Direct (SGE) fields should be shown / required.</summary>
    public extern bool IsDirectMode { [ObservableAsProperty] get; }

    /// <summary>True when the Lich proxy host/port fields should be shown / required.</summary>
    public extern bool IsLichMode   { [ObservableAsProperty] get; }

    /// <summary>
    /// Connect to SGE over TLS (port 7910) instead of plaintext (7900).
    /// Default ON — safe now because the client auto-falls-back to plain 7900 if
    /// the TLS handshake fails or stalls. See <see cref="ConnectionConfig.UseTls"/>.
    /// </summary>
    [Reactive] public bool          UseTls      { get; set; } = true;

    // UseStormFrontEnd was removed May 25, 2026 after A/B testing showed no
    // observable difference between FE:GENIE and FE:STORM for the info-verb
    // surface. The underlying ConnectionConfig.FrontEndId field still exists
    // (defaults to "GENIE") in case future testing of other surfaces reveals
    // FE-gated markup. See backlog.md → "FE:STORM hypothesis — disconfirmed"
    // for the experiment writeup and the FE_DIFF Console mode for re-running
    // the comparison.

    // ── Character fetcher ─────────────────────────────────────────────────────

    public ObservableCollection<string> AvailableCharacters { get; } = [];

    [Reactive] public bool   IsFetching   { get; private set; }
    [Reactive] public string FetchStatus  { get; private set; } = "";

    /// <summary>
    /// Label shown on the fetch/change button. Reads <c>Fetch</c> when no
    /// character is selected, <c>Change…</c> when one is — same command,
    /// different intent.
    /// </summary>
    public extern string FetchButtonLabel { [ObservableAsProperty] get; }

    public ReactiveCommand<Unit, Unit> FetchCharactersCommand { get; }

    // ── Dialog result commands ────────────────────────────────────────────────

    public ReactiveCommand<Unit, ConnectResult?> OkCommand     { get; }
    public ReactiveCommand<Unit, ConnectResult?> CancelCommand { get; }

    /// <summary>Designer-friendly parameterless constructor.</summary>
    public ConnectDialogViewModel() : this(null, null) { }

    public ConnectDialogViewModel(ProfileStore? store, Action? onStoreChanged)
        : this(store, onStoreChanged, lastConnection: null) { }

    /// <summary>
    /// Full constructor. <paramref name="lastConnection"/> carries the
    /// just-disconnected session's actual config so the reopened dialog
    /// shows what the user actually used. If a saved profile's stored
    /// values match the config field-for-field, the profile is re-selected;
    /// otherwise the bare credentials are filled and the profile dropdown
    /// stays empty (because the connection isn't associated with any saved
    /// profile). This avoids the previous bug where a saved profile would
    /// "stick" even after the user connected with edited credentials.
    /// </summary>
    public ConnectDialogViewModel(
        ProfileStore? store,
        Action? onStoreChanged,
        ConnectionConfig? lastConnection,
        Action<string>? onAuthFailure = null,
        Func<ConnectionProfile?, IReadOnlyList<string>>? listLayouts = null)
    {
        _store          = store;
        _onStoreChanged = onStoreChanged;
        _onAuthFailure  = onAuthFailure;
        _listLayouts    = listLayouts;
        RefreshLayouts(null);

        // ── Load profiles into the dropdown ────────────────────────────────
        if (_store is not null)
            foreach (var p in _store.Profiles)
                Profiles.Add(p);

        // ── Auto-populate fields when a profile is selected ────────────────
        this.WhenAnyValue(x => x.SelectedProfile)
            .Where(p => p is not null)
            .Subscribe(p => PopulateFrom(p!));

        // ── Mode-driven visibility flags ───────────────────────────────────
        this.WhenAnyValue(x => x.SelectedMode)
            .Select(m => m.Mode == ConnectionMode.DirectSGE)
            .ToPropertyEx(this, x => x.IsDirectMode);
        this.WhenAnyValue(x => x.SelectedMode)
            .Select(m => m.Mode == ConnectionMode.LichProxy)
            .ToPropertyEx(this, x => x.IsLichMode);

        // ── Field validity gate for OK / Save / Fetch ──────────────────────
        // Lich mode only needs a host + a valid port (Lich has already
        // authenticated, so no account/password/character are required).
        // Direct mode keeps the full credential requirement.
        var canOk = this.WhenAnyValue(
            x => x.SelectedMode, x => x.Account, x => x.Password, x => x.Character,
            x => x.LichHost, x => x.LichPort,
            (mode, a, p, c, host, port) => mode.Mode == ConnectionMode.LichProxy
                ? !string.IsNullOrWhiteSpace(host) && IsValidPort(port)
                : !string.IsNullOrWhiteSpace(a)
                  && !string.IsNullOrWhiteSpace(p)
                  && !string.IsNullOrWhiteSpace(c));

        var canSave = this.WhenAnyValue(
            x => x.ProfileName, x => x.SelectedMode, x => x.Account, x => x.Character, x => x.LichHost,
            (n, mode, a, c, host) => _store is not null
                      && !string.IsNullOrWhiteSpace(n)
                      && (mode.Mode == ConnectionMode.LichProxy
                            ? !string.IsNullOrWhiteSpace(host)
                            : !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(c)));

        var canDelete = this.WhenAnyValue(x => x.SelectedProfile)
            .Select(p => _store is not null && p is not null);

        // Active whenever the credentials are valid and we're not already mid-fetch.
        // The Character field no longer gates the button — instead, the click
        // action clears Character first (effectively "swap to a different one").
        var canFetch = this.WhenAnyValue(
                x => x.Account, x => x.Password, x => x.IsFetching,
                (a, p, busy) => !busy
                            && !string.IsNullOrWhiteSpace(a)
                            && !string.IsNullOrWhiteSpace(p));

        // Button label tracks Character: "Fetch" when empty, "Change…" when set.
        this.WhenAnyValue(x => x.Character)
            .Select(c => string.IsNullOrWhiteSpace(c) ? "Fetch" : "Change…")
            .ToPropertyEx(this, x => x.FetchButtonLabel);

        // ── Commands ───────────────────────────────────────────────────────
        // OK returns both the assembled ConnectionConfig AND the selected
        // saved profile (if any) so callers can attach per-profile state.
        OkCommand     = ReactiveCommand.Create(
            () =>
            {
                // A changed layout choice on a saved profile sticks even when
                // the user hits Connect without Save — the dropdown reads as a
                // profile setting, so silently discarding it would surprise.
                PersistLayoutChoice();
                return (ConnectResult?)new ConnectResult(BuildConfig(), ResolveConnectProfile());
            }, canOk);
        CancelCommand = ReactiveCommand.Create(() => (ConnectResult?)null);
        SaveProfileCommand     = ReactiveCommand.Create(SaveProfile,   canSave);
        BrowseDataDirCommand   = ReactiveCommand.Create(() => BrowseDataDirRequested?.Invoke());
        DeleteProfileCommand   = ReactiveCommand.Create(DeleteProfile, canDelete);
        FetchCharactersCommand = ReactiveCommand.CreateFromTask(FetchCharactersAsync, canFetch);

        // ── Initial population ─────────────────────────────────────────────
        // 1. If we have a remembered connection: find a saved profile whose
        //    stored credentials match it field-for-field. If one exists,
        //    select it (PopulateFrom fires with identical values — no
        //    overwrite damage). Otherwise the connection wasn't associated
        //    with a saved profile — fill the fields directly from the
        //    config and leave the profile dropdown blank.
        // 2. Else if there's exactly one saved profile (fresh app start,
        //    no prior connection this session): auto-select it.
        if (lastConnection is not null
            && lastConnection.Mode == ConnectionMode.LichProxy)
        {
            // Lich connects carry no account name, so they never match a saved
            // SGE profile field-for-field — just restore the host/port directly.
            PopulateFromConfig(lastConnection);
        }
        else if (lastConnection is not null
            && !string.IsNullOrWhiteSpace(lastConnection.AccountName))
        {
            var match = _store is null ? null : Profiles.FirstOrDefault(p =>
                string.Equals(p.AccountName,   lastConnection.AccountName,   StringComparison.OrdinalIgnoreCase)
             && string.Equals(_store.GetPassword(p), lastConnection.AccountPassword, StringComparison.Ordinal)
             && string.Equals(p.CharacterName, lastConnection.CharacterName, StringComparison.OrdinalIgnoreCase)
             && string.Equals(p.GameCode,      lastConnection.GameCode,      StringComparison.OrdinalIgnoreCase));

            if (match is not null) SelectedProfile = match;
            else                   PopulateFromConfig(lastConnection);
        }
        else if (Profiles.Count == 1)
        {
            SelectedProfile = Profiles[0];
        }
    }

    /// <summary>Fill the editable fields from a <see cref="ConnectionConfig"/> —
    /// the bare-credential equivalent of <see cref="PopulateFrom"/>. Leaves
    /// <see cref="ProfileName"/> blank because a config without a profile is,
    /// by definition, not associated with a saved profile.</summary>
    private void PopulateFromConfig(ConnectionConfig cfg)
    {
        ProfileName  = "";
        SelectedMode = ModeOptionFor(cfg.Mode);
        Instance     = Instances.FirstOrDefault(i =>
            string.Equals(i.GameCode, cfg.GameCode, StringComparison.OrdinalIgnoreCase))
            ?? Instances[0];
        Account      = cfg.AccountName;
        Password     = cfg.AccountPassword;
        LichHost     = cfg.LichProxyHost;
        LichPort     = cfg.LichProxyPort.ToString();

        AvailableCharacters.Clear();
        if (!string.IsNullOrEmpty(cfg.CharacterName))
            AvailableCharacters.Add(cfg.CharacterName);
        Character   = cfg.CharacterName;
        DataDirectory = "";
        RefreshLayouts(null);
        SelectedLayout = KeepCurrentLayout;
        FetchStatus = "";
    }

    private void PopulateFrom(ConnectionProfile p)
    {
        ProfileName  = p.Name;
        SelectedMode = ModeOptionFor(p.Mode);
        Instance     = Instances.FirstOrDefault(i =>
            string.Equals(i.GameCode, p.GameCode, StringComparison.OrdinalIgnoreCase))
            ?? Instances[0];
        Account      = p.AccountName;
        Password     = _store?.GetPassword(p) ?? "";

        // For Lich profiles the stored Host/Port are the proxy endpoint. For
        // SGE profiles Host/Port are eaccess values we don't surface, so only
        // adopt them when the profile is actually a Lich profile.
        if (p.Mode == ConnectionMode.LichProxy)
        {
            LichHost = string.IsNullOrWhiteSpace(p.Host) ? "127.0.0.1" : p.Host;
            LichPort = p.Port > 0 ? p.Port.ToString() : "8000";
        }

        // Switching profiles must drop the previous account's character list — it
        // belonged to a different account and showing it here is both confusing and
        // (mildly) a privacy leak. Rebuild the dropdown to contain only this
        // profile's stored character so the binding can still display it.
        AvailableCharacters.Clear();
        if (!string.IsNullOrEmpty(p.CharacterName))
            AvailableCharacters.Add(p.CharacterName);
        Character        = p.CharacterName;
        DataDirectory    = p.DataDirectory;

        // Startup layout: offer this profile's layouts too, and pre-select its
        // stored choice. A stored name whose file is gone is still shown (so
        // the choice isn't silently discarded by opening the dialog).
        RefreshLayouts(p);
        if (!string.IsNullOrWhiteSpace(p.DefaultLayoutName))
        {
            if (!AvailableLayouts.Contains(p.DefaultLayoutName))
                AvailableLayouts.Add(p.DefaultLayoutName);
            SelectedLayout = p.DefaultLayoutName;
        }
        else
        {
            SelectedLayout = KeepCurrentLayout;
        }

        FetchStatus      = "";
    }

    /// <summary>Set the data folder from the view's folder picker.</summary>
    public void SetDataDirectoryFromBrowse(string path) => DataDirectory = path ?? "";

    /// <summary>Maps a raw <see cref="ConnectionMode"/> back to its dropdown
    /// option, falling back to Direct if the mode isn't offered (e.g. a
    /// DevReplay config never reaches this dialog).</summary>
    private static ConnectionModeOption ModeOptionFor(ConnectionMode mode)
        => ConnectionModes.FirstOrDefault(m => m.Mode == mode) ?? ConnectionModes[0];

    /// <summary>True for a parseable TCP port in the legal 1–65535 range.</summary>
    private static bool IsValidPort(string? port)
        => int.TryParse(port, out var p) && p is > 0 and <= 65535;

    private ConnectionConfig BuildConfig() => SelectedMode.Mode == ConnectionMode.LichProxy
        ? new ConnectionConfig
          {
              Mode          = ConnectionMode.LichProxy,
              LichProxyHost = string.IsNullOrWhiteSpace(LichHost) ? "127.0.0.1" : LichHost.Trim(),
              LichProxyPort = int.TryParse(LichPort, out var lp) ? lp : 8000,
              // Carried only for title-bar / profile labeling — Lich selects the
              // character itself; the server's pc-name push fills this in live too.
              CharacterName = Character,
              GameCode      = Instance.GameCode,
          }
        : new ConnectionConfig
          {
              SgeHost         = "eaccess.play.net",
              SgePort         = 7900,
              AccountName     = Account,
              AccountPassword = Password,
              CharacterName   = Character,
              GameCode        = Instance.GameCode,
              Mode            = ConnectionMode.DirectSGE,
              UseTls          = UseTls,
              // FrontEndId left at default "GENIE" — A/B testing showed no FE
              // difference for our probed surfaces; ConnectionConfig's default
              // is "GENIE" so we don't need to set it explicitly here.
          };

    private async Task FetchCharactersAsync()
    {
        // Always start with a clean slate. If the user already had a character
        // selected (e.g. from a profile), this is the "Change…" case — the
        // user wants to see the full list and pick a different name. Clearing
        // also keeps any stale entry from the previous account out of view.
        Character = "";
        AvailableCharacters.Clear();

        IsFetching  = true;
        FetchStatus = "Connecting to SGE…";
        try
        {
            var sge = new SgeAuthClient(NullLogger<SgeAuthClient>.Instance);
            var cfg = BuildConfig();
            var chars = await sge.ListCharactersAsync(cfg);

            foreach (var c in chars)
                AvailableCharacters.Add(c.Name);

            FetchStatus = $"Found {chars.Count} character{(chars.Count == 1 ? "" : "s")} — pick one.";
        }
        catch (Exception ex)
        {
            FetchStatus = $"Failed: {ex.Message}";
            // Mirror the reason into the persistent Game window — the dialog's
            // status line disappears when the user closes the dialog, leaving
            // them with no record of why the login failed.
            _onAuthFailure?.Invoke(ex.Message);
        }
        finally
        {
            IsFetching = false;
        }
    }

    private void SaveProfile()
    {
        if (_store is null) return;

        var existing = _store.Profiles
            .FirstOrDefault(p => string.Equals(p.Name, ProfileName, StringComparison.OrdinalIgnoreCase));

        var isLich = SelectedMode.Mode == ConnectionMode.LichProxy;
        // Lich profiles store the proxy endpoint and carry no SGE credentials;
        // SGE profiles store the fixed eaccess endpoint and the account creds.
        var host        = isLich ? (string.IsNullOrWhiteSpace(LichHost) ? "127.0.0.1" : LichHost.Trim())
                                 : "eaccess.play.net";
        var port        = isLich ? (int.TryParse(LichPort, out var lp) ? lp : 8000) : 7900;
        var account     = isLich ? "" : Account;
        var password    = isLich ? "" : Password;

        ConnectionProfile? target;
        if (existing is not null)
        {
            _store.Update(
                existing.Id, ProfileName,
                isSimutronics: !isLich,
                gameCode: Instance.GameCode,
                characterName: Character,
                host: host, port: port,
                accountName: account, plainPassword: password,
                mode: SelectedMode.Mode);
            target = existing;
        }
        else
        {
            target = _store.Add(
                ProfileName, host, port, account, password,
                isSimutronics: !isLich,
                gameCode: Instance.GameCode,
                characterName: Character,
                mode: SelectedMode.Mode);
            Profiles.Add(target);
            SelectedProfile = target;
        }

        // Per-profile data folder. Set directly on the stored instance (the
        // ProfileStore.Add/Update signatures don't carry it); the subsequent
        // store save (_onStoreChanged) persists it.
        target.DataDirectory = (DataDirectory ?? "").Trim();

        // Per-profile startup layout — same direct-set pattern as DataDirectory.
        target.DefaultLayoutName = SelectedLayout == KeepCurrentLayout ? "" : SelectedLayout;

        // FE:STORM checkbox was removed (May 25, 2026); we no longer write
        // the FrontEndId from the dialog. Any pre-existing profile keeps
        // whatever value it had, but new profiles will use the
        // ConnectionProfile.FrontEndId default ("GENIE").

        _onStoreChanged?.Invoke();
    }

    /// <summary>Persist the Layout dropdown's choice onto the selected saved
    /// profile if it changed — used by Connect so the choice survives without
    /// an explicit Save (SaveProfile writes it as part of the full save).</summary>
    private void PersistLayoutChoice()
    {
        // Resolve, don't trust the raw dropdown: after the user retypes the
        // fields for a different character, SelectedProfile still points at
        // the old pick, and writing the layout there would corrupt it.
        var target = ResolveConnectProfile();
        if (_store is null || target is null) return;
        var chosen = SelectedLayout == KeepCurrentLayout ? "" : SelectedLayout;
        if (string.Equals(target.DefaultLayoutName, chosen, StringComparison.Ordinal))
            return;
        target.DefaultLayoutName = chosen;
        _onStoreChanged?.Invoke();
    }

    private void DeleteProfile()
    {
        if (_store is null || SelectedProfile is null) return;

        var toRemove = SelectedProfile;
        _store.Remove(toRemove.Id);
        Profiles.Remove(toRemove);
        SelectedProfile = null;

        _onStoreChanged?.Invoke();
    }

    /// <summary>Returns the saved profile whose name matches the currently-entered
    /// <see cref="ProfileName"/> (case-insensitive), or <c>null</c> if no
    /// store is wired, the name is blank, or no match exists. The dialog uses
    /// this to decide whether OK should prompt to save edits.</summary>
    public ConnectionProfile? FindProfileByEnteredName()
    {
        if (_store is null || string.IsNullOrWhiteSpace(ProfileName)) return null;
        return _store.Profiles.FirstOrDefault(p =>
            string.Equals(p.Name, ProfileName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The saved profile this connect should be attributed to. The dropdown's
    /// <see cref="SelectedProfile"/> counts only while the entered
    /// <see cref="ProfileName"/> still agrees with it (or is blank) — selecting
    /// a profile and then retyping the fields for a different character must
    /// NOT scope the new session to the old profile. Once the name diverges,
    /// resolve by the entered name instead (a profile created moments ago by
    /// the OK-time save prompt resolves here); null means a bare-credential
    /// connect with no profile attached.
    /// </summary>
    public ConnectionProfile? ResolveConnectProfile()
    {
        if (SelectedProfile is not null
            && (string.IsNullOrWhiteSpace(ProfileName)
                || string.Equals(ProfileName.Trim(), SelectedProfile.Name,
                                 StringComparison.OrdinalIgnoreCase)))
            return SelectedProfile;
        return FindProfileByEnteredName();
    }

    /// <summary>True when an existing profile with the entered <see cref="ProfileName"/>
    /// is present AND the entered account name or password differs from what's
    /// stored. Character / game-code changes are NOT considered "unsaved" — those
    /// are routine per-session choices on the same profile. Returns false when
    /// <see cref="ProfileName"/> is blank (bare-credential connects have no
    /// profile to update).</summary>
    public bool EnteredCredentialsDifferFromStored()
    {
        var existing = FindProfileByEnteredName();
        if (existing is null || _store is null) return false;

        // A mode switch is itself a meaningful change worth offering to persist.
        if (existing.Mode != SelectedMode.Mode) return true;

        // Lich profiles have no account/password — compare the proxy endpoint.
        if (SelectedMode.Mode == ConnectionMode.LichProxy)
        {
            var port = int.TryParse(LichPort, out var lp) ? lp : 8000;
            return !string.Equals(existing.Host, LichHost?.Trim(), StringComparison.OrdinalIgnoreCase)
                || existing.Port != port;
        }

        var storedPassword = _store.GetPassword(existing);
        return !string.Equals(existing.AccountName, Account, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(storedPassword,        Password, StringComparison.Ordinal);
    }

    /// <summary>Public surface for the OK-time save prompt: writes the
    /// dialog's current field values to the matched profile (update path
    /// inside <see cref="SaveProfile"/>). No-op if no store is wired.</summary>
    public void PersistCurrentEdits() => SaveProfile();
}
