using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Genie.App.Controls;
using Genie.App.Diagnostics;
using Genie.App.ViewModels;
using Genie.Core.Commanding;
using Genie.Core.Layout;
using Xunit;

namespace Genie.App.HeadlessTests;

/// <summary>
/// Public #361 — a Genie 4 <c>#img</c> picture line. The line stays a text line
/// whose <see cref="TextLine.Text"/> is the <c>[image: name.png]</c> placeholder
/// (so scrollback, timestamps, the session log and copy treat it like text),
/// and only the renderer swaps that placeholder for the decoded picture.
/// </summary>
public sealed class ImageLineHeadlessTests : IDisposable
{
    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "genie_imgline_" + Guid.NewGuid().ToString("N"));

    public ImageLineHeadlessTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private ImageRequest Request(string name = "sword.png", string? window = null, int w = 0, int h = 0)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, TinyPng);
        return new ImageRequest(path, window, w, h);
    }

    // The decode runs on the thread pool and lands via the UI dispatcher, which
    // this test thread owns — so pump it rather than block on the task.
    private static void PumpUntil(Task task)
    {
        var sw = Stopwatch.StartNew();
        while (!task.IsCompleted && sw.Elapsed < TimeSpan.FromSeconds(10))
            Dispatcher.UIThread.RunJobs();
        Assert.True(task.IsCompleted, "image decode never completed");
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void An_image_line_is_one_line_whose_text_is_the_placeholder()
    {
        var vm = new GameTextViewModel();
        vm.AddImage(Request());

        var line = vm.Lines.Single();
        Assert.Equal("[image: sword.png]", line.Text);
        Assert.NotNull(line.Image);
        Assert.Equal((0, line.Text.Length), line.ImageSpan);

        var inline = Assert.Single(line.Inlines);
        var host = Assert.IsType<InlineUIContainer>(inline);
        Assert.IsType<Image>(host.Child);
    }

    [AvaloniaFact]
    public void A_timestamped_image_line_keeps_the_prefix_as_text()
    {
        var vm = new GameTextViewModel { Settings = new WindowSettings { Timestamp = true } };
        vm.AddImage(Request());

        var line = vm.Lines.Single();
        Assert.Matches(@"^\[\d\d:\d\d:\d\d\] \[image: sword\.png\]$", line.Text);
        Assert.Equal(line.Text.IndexOf("[image:", StringComparison.Ordinal), line.ImageSpan!.Value.Start);

        var inlines = line.Inlines;
        Assert.Equal(2, inlines.Count);
        Assert.Equal(line.Text[..line.ImageSpan.Value.Start], Assert.IsType<Run>(inlines[0]).Text);
        Assert.IsType<InlineUIContainer>(inlines[1]);
    }

    [AvaloniaFact]
    public void Scrollback_trims_an_image_line_like_any_other()
    {
        var vm = new GameTextViewModel();   // default cap 2000 (no core attached)
        vm.AddImage(Request());
        for (var i = 0; i < 2000; i++) vm.AddSystemLine($"line {i}");

        Assert.Equal(2000, vm.Lines.Count);
        Assert.DoesNotContain(vm.Lines, l => l.Image is not null);
    }

    [AvaloniaFact]
    public void The_session_log_writes_the_placeholder()
    {
        var vm = new GameTextViewModel();
        var logs = Path.Combine(_dir, "logs");
        using (var logger = new SessionTextLogger(logs))
        {
            logger.Start(vm, "Renucci", "DR");
            vm.AddSystemLine("before");
            vm.AddImage(Request());
            vm.AddSystemLine("after");
        }

        var text = File.ReadAllLines(Directory.GetFiles(logs).Single());
        Assert.Equal(new[] { "before", "[image: sword.png]", "after" }, text);
    }

    [AvaloniaFact]
    public void The_picture_renders_at_the_requested_size_once_decoded()
    {
        var vm = new GameTextViewModel();
        vm.AddImage(Request(w: 32, h: 24));
        var line = vm.Lines.Single();

        var block = new SelectableTextBlock();
        InlinesBehavior.SetSource(block, line.Inlines);
        var window = new Window { Width = 400, Height = 200, Content = block };
        window.Show();
        try
        {
            PumpUntil(line.Image!.Loaded);
            window.UpdateLayout();

            Assert.False(line.Image.Failed);
            // In the live visual tree, laid out — not just present in the model.
            var img = block.GetVisualDescendants().OfType<Image>().Single();
            Assert.True(img.IsArrangeValid);
            Assert.NotNull(img.Source);
            Assert.Equal((32.0, 24.0), (img.Width, img.Height));
            Assert.Equal("sword.png", ToolTip.GetTip(img));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void A_file_gone_by_decode_time_reports_once_and_never_throws()
    {
        var vm = new GameTextViewModel();
        var req = Request("gone.png");
        File.Delete(req.Path);

        vm.AddImage(req);
        var line = vm.Lines[0];
        PumpUntil(line.Image!.Loaded);

        Assert.True(line.Image.Failed);
        Assert.Null(line.Image.Bitmap);
        Assert.Equal("#img: could not decode gone.png.", vm.Lines.Last().Text);
        Assert.NotEmpty(line.Inlines);   // still renders (an empty picture), no exception
    }

    [AvaloniaFact]
    public void A_named_window_gets_the_same_picture_line()
    {
        var panel = new PluginWindowViewModel("Hotbar");
        panel.AppendLine("Actions:");
        panel.AppendImage(Request(window: "Hotbar"));

        var line = panel.Lines.Last();
        Assert.Equal("[image: sword.png]", line.Text);
        Assert.Equal("Hotbar", line.Window);
        Assert.IsType<InlineUIContainer>(Assert.Single(line.Inlines));
    }

    [AvaloniaFact]
    public void The_editor_renderer_shows_the_placeholder_text()
    {
        // The experimental AvaloniaEdit renderer (useeditorgamewindow) draws the
        // line's text; for an image line that is the placeholder.
        var vm = new GameTextViewModel();
        vm.AddImage(Request());
        var entry = new GameLineEntry(vm.Lines.Single());

        Assert.Equal("[image: sword.png]", entry.Line.Text);
        Assert.Empty(entry.Links);
    }
}
