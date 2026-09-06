using System.Text.Json;
using Genie.Core.Connection;

namespace Genie.Core.Profiles;

public sealed class ProfileStore
{
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };
    private readonly List<ConnectionProfile> _profiles = new();

    public IReadOnlyList<ConnectionProfile> Profiles => _profiles;

    /// <summary>Set when the last <see cref="Load"/> could not read the file:
    /// the reason, plus the path the unreadable original was quarantined to.
    /// Null after a clean load (including "no file yet"). The host surfaces
    /// this — starting with zero profiles when the user had ten must never be
    /// silent, or it reads as the app having eaten their accounts.</summary>
    public string? LastLoadError { get; private set; }

    /// <summary>Read the profile list, tolerating a damaged file.
    ///
    /// <para>This was the only config loader in the app with no error handling,
    /// and it runs from the MainWindowViewModel constructor by way of
    /// <c>App.OnFrameworkInitializationCompleted</c> — an <c>async void</c>
    /// framework override, where a JsonException is unhandled and the process
    /// exits. One torn write meant the app could never start again until the
    /// user found and hand-deleted profiles.json, which also destroyed every
    /// saved account and encrypted password (2026-08-31 stability review).</para>
    ///
    /// <para>A damaged file is moved aside rather than deleted, so the bytes
    /// holding those encrypted passwords survive for hand-recovery — the next
    /// <see cref="Save"/> would otherwise overwrite them for good.</para>
    /// </summary>
    public void Load(string path)
    {
        _profiles.Clear();
        LastLoadError = null;
        if (!File.Exists(path)) return;

        try
        {
            var loaded = JsonSerializer.Deserialize<List<ConnectionProfile>>(
                File.ReadAllText(path), _json) ?? new();
            _profiles.AddRange(loaded);
        }
        catch (Exception ex)
        {
            // Anything the file can throw is the same story: unreadable, and
            // starting is more important than reading it. Includes IO errors,
            // not just malformed JSON — a locked or unreadable file must not
            // block startup either.
            _profiles.Clear();   // a partial AddRange before the throw would be worse than none
            var quarantine = path + ".corrupt";
            try
            {
                File.Copy(path, quarantine, overwrite: true);
                LastLoadError = $"{ex.Message} (unreadable copy kept at {quarantine})";
            }
            catch
            {
                // Could not even copy it. Say so rather than implying a backup exists.
                LastLoadError = $"{ex.Message} (the original could not be copied aside)";
            }
        }
    }

    /// <summary>Write the profile list atomically. Returns false on failure
    /// instead of throwing — callers run from UI handlers.
    ///
    /// <para>Writes a sibling temp file and swaps it in, so an interrupted write
    /// (crash, power loss, full disk) leaves the previous good file untouched
    /// rather than a half-serialized one. <c>File.Replace</c> also carries the
    /// destination's permissions across, which matters for a file holding
    /// encrypted passwords. Same idiom as the analytics rollup.</para>
    /// </summary>
    public bool Save(string path)
    {
        var tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(_profiles, _json));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else                   File.Move(tmp, path);
            return true;
        }
        catch
        {
            // Never leave the temp behind to be mistaken for real state.
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
            return false;
        }
    }

    public ConnectionProfile Add(string name, string host, int port,
                                  string accountName, string plainPassword,
                                  bool isSimutronics = false, string gameCode = "",
                                  string characterName = "", bool autoConnect = false,
                                  ConnectionMode mode = ConnectionMode.DirectSGE)
    {
        if (autoConnect)
            foreach (var other in _profiles) other.AutoConnect = false;

        var profile = new ConnectionProfile
        {
            Name              = name,
            IsSimutronics     = isSimutronics,
            GameCode          = gameCode,
            CharacterName     = characterName,
            Host              = host,
            Port              = port,
            AccountName       = accountName,
            AutoConnect       = autoConnect,
            Mode              = mode,
            EncryptedPassword = ProfileCrypto.Encrypt(plainPassword)
        };
        _profiles.Add(profile);
        return profile;
    }

    public void Update(Guid id, string name, bool isSimutronics,
                       string gameCode, string characterName,
                       string host, int port,
                       string accountName, string plainPassword,
                       bool autoConnect = false,
                       ConnectionMode mode = ConnectionMode.DirectSGE)
    {
        var p = _profiles.FirstOrDefault(x => x.Id == id);
        if (p is null) return;
        p.Name          = name;
        p.IsSimutronics = isSimutronics;
        p.GameCode      = gameCode;
        p.CharacterName = characterName;
        p.Host          = host;
        p.Port          = port;
        p.AccountName   = accountName;
        p.Mode          = mode;
        if (!string.IsNullOrEmpty(plainPassword))
            p.EncryptedPassword = ProfileCrypto.Encrypt(plainPassword);
        if (autoConnect)
            foreach (var other in _profiles)
                if (other.Id != id) other.AutoConnect = false;
        p.AutoConnect = autoConnect;
    }

    public ConnectionProfile? GetAutoConnectProfile()
        => _profiles.FirstOrDefault(p => p.AutoConnect);

    public void Remove(Guid id) => _profiles.RemoveAll(p => p.Id == id);

    public string GetPassword(ConnectionProfile profile)
        => ProfileCrypto.Decrypt(profile.EncryptedPassword);
}
