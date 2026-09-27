using System;
using System.IO;
using System.Linq;
using System.Text;
using Genie.App.ScriptEditing;
using Xunit;

namespace Genie.App.Tests;

/// <summary>
/// Public #243 — the built-in script editor's disk side. A save must hand the
/// file back the way it was found (same encoding, same BOM or none, same line
/// endings), must never leave a half-written file behind, and an edit made by
/// another program must be told apart from our own save.
/// </summary>
public class ScriptTextFileTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "genie_scriptedit_" + Guid.NewGuid().ToString("N"));

    public ScriptTextFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    private string Write(string name, byte[] bytes)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllBytes(p, bytes);
        return p;
    }

    private static byte[] Bytes(Encoding enc, string text, bool bom)
        => (bom ? enc.GetPreamble() : []).Concat(enc.GetBytes(text)).ToArray();

    // ── encoding ─────────────────────────────────────────────────────────────

    [Fact]
    public void Utf8_without_bom_stays_without_bom()
    {
        var p = Write("a.cmd", Encoding.UTF8.GetBytes("put look\necho café\n"));
        var f = ScriptTextFile.Open(p, out var text);
        Assert.Equal("put look\necho café\n", text);
        Assert.Equal("UTF-8", f.EncodingLabel);

        f.Save(text + "pause\n");

        Assert.Equal(Encoding.UTF8.GetBytes("put look\necho café\npause\n"), File.ReadAllBytes(p));
    }

    [Fact]
    public void Utf8_with_bom_keeps_its_bom()
    {
        var p = Write("b.cmd", Bytes(new UTF8Encoding(true), "echo ü\r\n", bom: true));
        var f = ScriptTextFile.Open(p, out var text);
        Assert.Equal("echo ü\r\n", text);      // BOM is not part of the text
        Assert.Equal("UTF-8 BOM", f.EncodingLabel);

        f.Save("echo ü\r\nexit\r\n");

        Assert.Equal(Bytes(new UTF8Encoding(true), "echo ü\r\nexit\r\n", bom: true), File.ReadAllBytes(p));
    }

    [Fact]
    public void Utf16_le_round_trips()
    {
        var enc = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
        var p = Write("c.cmd", Bytes(enc, "put look\r\n", bom: true));
        var f = ScriptTextFile.Open(p, out var text);
        Assert.Equal("put look\r\n", text);
        Assert.Equal("UTF-16 LE", f.EncodingLabel);

        f.Save("put north\r\n");

        Assert.Equal(Bytes(enc, "put north\r\n", bom: true), File.ReadAllBytes(p));
    }

    [Fact]
    public void Legacy_codepage_bytes_survive_an_edit_elsewhere_in_the_file()
    {
        // 0xE9 = é in Latin-1 / Windows-1252; not valid UTF-8 on its own.
        var original = new byte[] { (byte)'e', (byte)'c', (byte)'h', (byte)'o', (byte)' ', 0xE9, (byte)'\r', (byte)'\n' };
        var p = Write("d.cmd", original);
        var f = ScriptTextFile.Open(p, out var text);

        f.Save(text + "exit\r\n");

        Assert.Equal(original.Concat("exit\r\n"u8.ToArray()).ToArray(), File.ReadAllBytes(p));
        Assert.False(f.EncodingChangedOnLastSave);
    }

    [Fact]
    public void A_character_the_legacy_encoding_cannot_hold_falls_back_to_utf8_and_says_so()
    {
        var p = Write("e.cmd", new byte[] { (byte)'x', 0xE9, (byte)'\n' });
        var f = ScriptTextFile.Open(p, out var text);

        f.Save(text + "echo ✓\n");

        Assert.True(f.EncodingChangedOnLastSave);
        Assert.Equal("xé\necho ✓\n", Encoding.UTF8.GetString(File.ReadAllBytes(p)));
    }

    // ── line endings ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("\r\n", "CRLF")]
    [InlineData("\n", "LF")]
    [InlineData("\r", "CR")]
    public void Line_endings_are_preserved_even_when_the_editor_hands_back_others(string eol, string label)
    {
        var p = Write("f.cmd", Encoding.UTF8.GetBytes($"a:{eol}goto a{eol}"));
        var f = ScriptTextFile.Open(p, out _);
        Assert.Equal(label, f.LineEndingLabel);

        // Mixed breaks from a paste: every one comes back in the file's style.
        f.Save("a:\ngoto a\r\npause\rexit");

        Assert.Equal($"a:{eol}goto a{eol}pause{eol}exit", File.ReadAllText(p));
    }

    // ── atomic save ──────────────────────────────────────────────────────────

    [Fact]
    public void Save_leaves_no_temp_file_behind()
    {
        var p = Write("g.cmd", "echo 1\n"u8.ToArray());
        var f = ScriptTextFile.Open(p, out _);

        f.Save("echo 2\n");

        Assert.Equal(new[] { "g.cmd" }, Directory.GetFiles(_dir).Select(Path.GetFileName));
        Assert.Equal("echo 2\n", File.ReadAllText(p));
    }

    [Fact]
    public void A_failed_save_leaves_the_original_whole_and_no_temp_file()
    {
        if (!OperatingSystem.IsWindows()) return;   // needs mandatory file locks
        var p = Write("h.cmd", "echo original\n"u8.ToArray());
        var f = ScriptTextFile.Open(p, out _);

        using (new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.ThrowsAny<Exception>(() => f.Save("echo replaced\n"));

        Assert.Equal("echo original\n", File.ReadAllText(p));
        Assert.Equal(new[] { "h.cmd" }, Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public void Save_creates_the_file_again_if_it_was_deleted()
    {
        var p = Write("i.cmd", "echo 1\n"u8.ToArray());
        var f = ScriptTextFile.Open(p, out _);
        File.Delete(p);

        f.Save("echo 2\n");

        Assert.Equal("echo 2\n", File.ReadAllText(p));
    }

    // ── outside changes ──────────────────────────────────────────────────────

    [Fact]
    public void Our_own_save_is_not_an_outside_change()
    {
        var p = Write("j.cmd", "echo 1\n"u8.ToArray());
        var f = ScriptTextFile.Open(p, out _);
        f.Save("echo 2\n");
        Assert.False(f.HasChangedOnDisk());
    }

    [Fact]
    public void Another_program_writing_the_file_is_an_outside_change()
    {
        var p = Write("k.cmd", "echo 1\n"u8.ToArray());
        var f = ScriptTextFile.Open(p, out _);

        File.WriteAllText(p, "echo from elsewhere\n");
        File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddMinutes(1));

        Assert.True(f.HasChangedOnDisk());
        Assert.Equal("echo from elsewhere\n", f.Reload());
        Assert.False(f.HasChangedOnDisk());
    }

    [Fact]
    public void A_touch_with_the_same_bytes_is_not_a_change()
    {
        var p = Write("l.cmd", "echo 1\n"u8.ToArray());
        var f = ScriptTextFile.Open(p, out _);
        File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddMinutes(1));
        Assert.False(f.HasChangedOnDisk());
    }

    [Fact]
    public void Deletion_is_a_change_and_acknowledging_it_quiets_it()
    {
        var p = Write("m.cmd", "echo 1\n"u8.ToArray());
        var f = ScriptTextFile.Open(p, out _);
        File.Delete(p);

        Assert.True(f.HasChangedOnDisk());
        Assert.True(f.IsMissingOnDisk);
        f.AcknowledgeDiskState();
        Assert.False(f.HasChangedOnDisk());
    }

    [Fact]
    public void Keeping_my_version_quiets_that_change_until_the_next_one()
    {
        var p = Write("n.cmd", "echo 1\n"u8.ToArray());
        var f = ScriptTextFile.Open(p, out _);
        File.WriteAllText(p, "echo 2\n");
        File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddMinutes(1));
        Assert.True(f.HasChangedOnDisk());

        f.AcknowledgeDiskState();
        Assert.False(f.HasChangedOnDisk());

        File.WriteAllText(p, "echo 3\n");
        File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddMinutes(2));
        Assert.True(f.HasChangedOnDisk());
    }
}
