using System;
using System.Collections.Generic;
using System.IO;
using Genie.Core.Commanding;
using Genie.Core.Config;
using Genie.Core.Queue;
using Genie.Core.Runtime;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Public #361 — Genie 4 <c>#img</c> / <c>#image</c> (Core/Command.cs:397):
/// <c>#img [&gt;window] &lt;file&gt; [w:N] [h:N]</c>. Core parses and validates;
/// the App draws. These pin the parse (both spellings, the redirect, the size
/// options), the art-dir path resolution, and the clear error for a bad file.
/// </summary>
public class ImageCommandTests : IDisposable
{
    // A real 1×1 PNG, so the header check sees a genuine signature.
    internal static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    private readonly string _root;
    private readonly GenieConfig _config;

    public ImageCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "genie_img_tests_" + Guid.NewGuid().ToString("N"));
        var lds = new LocalDirectoryService("GenieImgTest", _root);
        lds.UseExplicitRoot(_root);
        _config = new GenieConfig(lds);
        Directory.CreateDirectory(Path.Combine(_config.ArtDir, "icons"));
        File.WriteAllBytes(Path.Combine(_config.ArtDir, "icons", "sword.png"), TinyPng);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    private FakeCommandHost Run(string input)
    {
        var host = new FakeCommandHost();
        new CommandEngine(_config, new CommandQueue(), new EventQueue(), host).ProcessInput(input);
        return host;
    }

    private string Art(params string[] parts) => Path.GetFullPath(Path.Combine(_config.ArtDir, Path.Combine(parts)));

    [Theory]
    [InlineData("#img icons/sword.png")]
    [InlineData("#image icons/sword.png")]
    [InlineData("#IMG icons/sword.png")]
    public void Both_spellings_resolve_against_the_art_directory(string input)
    {
        var host = Run(input);

        var req = Assert.Single(host.Images);
        Assert.Equal(Art("icons", "sword.png"), req.Path);
        Assert.Null(req.Window);
        Assert.Equal((0, 0), (req.Width, req.Height));
        Assert.Empty(host.Echoes);
    }

    [Fact]
    public void A_genie4_backslash_path_works_on_every_platform()
    {
        var req = Assert.Single(Run(@"#img icons\sword.png").Images);
        Assert.Equal(Art("icons", "sword.png"), req.Path);
    }

    [Fact]
    public void An_absolute_path_is_used_as_is()
    {
        var abs = Path.Combine(_root, "elsewhere.png");
        File.WriteAllBytes(abs, TinyPng);

        var req = Assert.Single(Run($"#img \"{abs}\"").Images);
        Assert.Equal(Path.GetFullPath(abs), req.Path);
    }

    [Fact]
    public void The_redirect_and_size_options_may_come_in_any_order()
    {
        var req = Assert.Single(Run("#img w:32 >Hotbar icons/sword.png height:24").Images);

        Assert.Equal("Hotbar", req.Window);
        Assert.Equal((32, 24), (req.Width, req.Height));
        Assert.Equal(Art("icons", "sword.png"), req.Path);
    }

    [Fact]
    public void A_quoted_multi_word_window_is_one_target()
    {
        var req = Assert.Single(Run("#img \">Combat Bar\" icons/sword.png").Images);
        Assert.Equal("Combat Bar", req.Window);
    }

    [Fact]
    public void A_bad_size_warns_like_genie4_and_still_shows_the_image()
    {
        var host = Run("#img icons/sword.png w:big");

        Assert.Contains("Invalid Width Specified: w:big", host.Echoes);
        var req = Assert.Single(host.Images);
        Assert.Equal(0, req.Width);
    }

    [Fact]
    public void No_file_echoes_the_genie4_message()
    {
        var host = Run("#img >Hotbar w:16");

        Assert.Empty(host.Images);
        Assert.Contains("No File Name was specified for the Image Command.", host.Echoes);
    }

    [Fact]
    public void A_missing_file_echoes_a_clear_error_naming_the_resolved_path()
    {
        var host = Run("#img icons/nope.png");

        Assert.Empty(host.Images);
        var msg = Assert.Single(host.Echoes);
        Assert.StartsWith("#img: image not found:", msg);
        Assert.Contains(Art("icons", "nope.png"), msg);
    }

    [Fact]
    public void An_unsupported_type_is_refused()
    {
        File.WriteAllText(Path.Combine(_config.ArtDir, "notes.txt"), "hello");
        var host = Run("#img notes.txt");

        Assert.Empty(host.Images);
        Assert.Contains(".txt", Assert.Single(host.Echoes));
    }

    [Fact]
    public void A_misnamed_file_fails_the_header_check()
    {
        File.WriteAllText(Path.Combine(_config.ArtDir, "fake.png"), "not really a png");
        var host = Run("#img fake.png");

        Assert.Empty(host.Images);
        Assert.Contains("not a readable", Assert.Single(host.Echoes));
    }

    [Fact]
    public void Every_supported_signature_passes()
    {
        var files = new Dictionary<string, byte[]>
        {
            ["a.jpg"]  = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0 },
            ["a.jpeg"] = new byte[] { 0xFF, 0xD8, 0xFF, 0xE1, 0, 0 },
            ["a.gif"]  = "GIF89a"u8.ToArray(),
            ["a.bmp"]  = "BM\0\0\0\0"u8.ToArray(),
            ["a.png"]  = TinyPng,
        };
        foreach (var (name, bytes) in files)
        {
            File.WriteAllBytes(Path.Combine(_config.ArtDir, name), bytes);
            Assert.True(ImageCommand.Validate(Art(name), out var err), $"{name}: {err}");
        }
    }

    [Fact]
    public void Unprefixed_img_is_game_input()
    {
        var host = Run("image icons/sword.png");
        Assert.Empty(host.Images);
        Assert.Single(host.Sent);
    }

    [Fact]
    public void The_placeholder_is_the_file_name()
    {
        var req = new ImageRequest(Art("icons", "sword.png"), null, 0, 0);
        Assert.Equal("[image: sword.png]", req.Placeholder);
    }

    // ── FitSize: requested size, aspect ratio and the render cap ─────────────

    [Theory]
    [InlineData(64, 32,   0,  0,   64,  32)]   // natural
    [InlineData(64, 32,  16, 16,   16,  16)]   // both given: stretch (Genie 4)
    [InlineData(64, 32,  32,  0,   32,  16)]   // width only: keep the aspect
    [InlineData(64, 32,   0,  8,   16,   8)]   // height only: keep the aspect
    [InlineData(4000, 2000, 0, 0, 1024, 512)]  // capped, aspect kept
    [InlineData(10, 10, 5000, 50, 1024,  10)]  // a stretched request is capped too
    public void FitSize_honours_the_request_and_caps_the_result(int nw, int nh, int rw, int rh, int ew, int eh)
        => Assert.Equal((ew, eh), ImageCommand.FitSize(nw, nh, rw, rh));

    private sealed class FakeCommandHost : ICommandHost
    {
        public List<ImageRequest> Images { get; } = new();
        public List<string> Echoes { get; } = new();
        public List<string> Sent { get; } = new();
        public Dictionary<string, string> Globals { get; } = new();

        public void EchoImage(ImageRequest request) => Images.Add(request);
        public void Echo(string text) => Echoes.Add(text);

        public IReadOnlyDictionary<string, string> GetGlobalVariables() => Globals;
        public string ExpandVariables(string text) => text;
        public void EchoTo(string text, string? window, string? color) { }
        public void EchoMain(string text, string? color, bool mono) { }
        public void EchoLink(string text, string command, string? window) { }
        public void EchoClear(string? window) { }
        public void WindowCommand(string sub, string window) { }
        public void SetStatusBar(string text, int index) { }
        public void SendToGame(string text, bool userInput = false, string origin = "", string? echoOverride = null) => Sent.Add(text);
        public void RunScript(string text) { }
        public void InjectParsedLine(string line) { }
        public void StopScript(string? name) { }
        public void PauseScript(string? name) { }
        public void ResumeScript(string? name) { }
        public void StopAllScripts() { }
        public void PauseAllScripts() { }
        public void ResumeAllScripts() { }
        public void SetTraceLevelAll(int level) { }
        public IReadOnlyList<string> RunningScripts() => Array.Empty<string>();
        public void SetGlobalVariable(string name, string value) => Globals[name] = value;
        public void RemoveGlobalVariable(string name) => Globals.Remove(name);
        public string SetLiveAudit(Genie.Core.Diagnostics.AuditMode mode) => string.Empty;
        public void EditScript(string name) { }
        public void LayoutCommand(string args) { }
        public void PluginCommand(string args) { }
        public void ConfigCommand(string args) { }
        public void MapperGoto(string args) { }
        public void MapperCommand(string args) { }
        public void MapperReset() { }
        public void PlaySound(string soundName) { }
        public void Speak(string text, bool urgent = false) { }
        public void TtsCommand(string args) { }
        public void FlashWindow() { }
        public void Connect(ConnectRequest request) { }
    }
}
