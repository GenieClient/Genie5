using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Genie.Core.Connection;
using Genie.Core.Profiles;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// profiles.json durability (2026-08-31 stability review).
///
/// <para>This was the only config loader in the app with no error handling, and it
/// runs from the MainWindowViewModel constructor by way of an <c>async void</c>
/// framework override — so a JsonException was unhandled and the process exited.
/// One torn write and the client could never start again until the user found and
/// hand-deleted the file, which also destroyed every saved account and encrypted
/// password.</para>
///
/// <para>Two independent properties are pinned here: a damaged file must not stop
/// startup (and its bytes must survive for hand-recovery), and a write must never
/// be able to produce a damaged file in the first place.</para>
/// </summary>
public class ProfileStoreResilienceTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "gc_profiles_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    // ── tolerant load ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("{ this is not json at all")]                 // torn mid-write
    [InlineData("")]                                          // zero-length (classic power-loss result)
    [InlineData("{\"unexpected\":\"shape\"}")]                // valid JSON, wrong type
    [InlineData("[{\"Name\":\"ok\"},{\"Port\":\"not-a-number\"}]")]  // valid shape, bad field
    public void A_damaged_file_does_not_throw(string content)
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "profiles.json");
            File.WriteAllText(path, content);
            var store = new ProfileStore();

            var ex = Record.Exception(() => store.Load(path));

            Assert.Null(ex);
            Assert.Empty(store.Profiles);
            Assert.NotNull(store.LastLoadError);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void A_damaged_file_is_kept_aside_rather_than_lost()
    {
        // The bytes hold the user's encrypted passwords. The next Save would
        // overwrite them, so the only chance to preserve them is here.
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "profiles.json");
            const string damaged = "{ half a profile list";
            File.WriteAllText(path, damaged);

            var store = new ProfileStore();
            store.Load(path);

            var quarantine = path + ".corrupt";
            Assert.True(File.Exists(quarantine), "the unreadable file was not kept aside");
            Assert.Equal(damaged, File.ReadAllText(quarantine));
            Assert.Contains(quarantine, store.LastLoadError);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void A_healthy_file_loads_with_no_reported_fault()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "profiles.json");
            var store = new ProfileStore();
            store.Add("Renucci", "dr.simutronics.net", 11024, "MONIL", "secret",
                      isSimutronics: true, gameCode: "DR", characterName: "Renucci");
            Assert.True(store.Save(path));

            var reloaded = new ProfileStore();
            reloaded.Load(path);

            Assert.Null(reloaded.LastLoadError);
            Assert.Single(reloaded.Profiles);
            Assert.Equal("Renucci", reloaded.Profiles[0].Name);
            Assert.False(File.Exists(path + ".corrupt"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void A_missing_file_is_not_a_fault()
    {
        var dir = TempDir();
        try
        {
            var store = new ProfileStore();
            store.Load(Path.Combine(dir, "does-not-exist.json"));

            Assert.Empty(store.Profiles);
            Assert.Null(store.LastLoadError);   // a fresh install is not an error
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void A_second_load_clears_a_previous_fault()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "profiles.json");
            File.WriteAllText(path, "{ broken");
            var store = new ProfileStore();
            store.Load(path);
            Assert.NotNull(store.LastLoadError);

            // Recovering (the app re-saves) must not leave the fault latched.
            store.Add("Fresh", "h", 1, "a", "p");
            Assert.True(store.Save(path));
            store.Load(path);

            Assert.Null(store.LastLoadError);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ── atomic save ──────────────────────────────────────────────────────────

    [Fact]
    public void Save_leaves_no_temp_file_behind()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "profiles.json");
            var store = new ProfileStore();
            store.Add("A", "h", 1, "acct", "pw");

            Assert.True(store.Save(path));
            Assert.True(store.Save(path));   // and again, over an existing file

            Assert.False(File.Exists(path + ".tmp"), "a temp file survived the write");
            Assert.Single(Directory.GetFiles(dir));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void An_overwriting_save_keeps_the_file_readable()
    {
        // The property the whole fix exists for: after a re-save the file on disk
        // is a complete document, never a half-written one.
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "profiles.json");
            var store = new ProfileStore();
            store.Add("First", "h", 1, "acct", "pw");
            store.Save(path);

            for (int i = 0; i < 5; i++)
            {
                store.Add($"P{i}", "h", 1, "acct", "pw");
                Assert.True(store.Save(path));

                var check = new ProfileStore();
                check.Load(path);
                Assert.Null(check.LastLoadError);
                Assert.Equal(store.Profiles.Count, check.Profiles.Count);
            }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void A_failed_save_reports_false_instead_of_throwing()
    {
        // Callers run from UI handlers, where an escaping IOException is a crash.
        // A directory standing where the file should be is a reliable way to make
        // the write fail on every platform.
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "profiles.json");
            Directory.CreateDirectory(path);

            var store = new ProfileStore();
            store.Add("A", "h", 1, "acct", "pw");

            bool result = true;
            var ex = Record.Exception(() => result = store.Save(path));

            Assert.Null(ex);
            Assert.False(result);
            Assert.False(File.Exists(path + ".tmp"), "the temp file was left behind after a failure");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Encrypted_passwords_survive_a_save_and_reload()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "profiles.json");
            var store = new ProfileStore();
            store.Add("Renucci", "h", 11024, "MONIL", "hunter2");
            var written = store.Profiles[0].EncryptedPassword;
            Assert.True(store.Save(path));

            var reloaded = new ProfileStore();
            reloaded.Load(path);

            Assert.Equal(written, reloaded.Profiles.Single().EncryptedPassword);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
