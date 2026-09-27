using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Genie.App.Services;
using Genie.Core.Commanding;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace Genie.App.ViewModels;

/// <summary>
/// The picture on a <c>#img</c> line (public #361), carried by
/// <see cref="TextLine.Image"/>. The line itself stays a text line whose
/// <see cref="TextLine.Text"/> is the <c>[image: name.png]</c> placeholder — that
/// is what scrollback, the session log, Copy / Copy All and Find all see — and
/// only the renderer swaps the placeholder for this picture.
///
/// <para>Decoding starts on construction, off the UI thread, through the shared
/// <see cref="InlineImageCache"/>; the line is added immediately so it keeps its
/// place among the lines around it, and the <see cref="Bitmap"/> binding fills it
/// in when the decode lands. A file that fails to decode reports once through the
/// caller's callback and the line keeps showing nothing — never a crash.</para>
/// </summary>
public sealed class InlineImage : ReactiveObject
{
    public InlineImage(ImageRequest request, Action<string>? onFailed = null)
    {
        Request = request;
        // Known up front when the script gave both edges, so the line doesn't
        // change height when the bitmap arrives.
        if (request.Width > 0 && request.Height > 0)
            (DisplayWidth, DisplayHeight) = ImageCommand.FitSize(request.Width, request.Height,
                                                                 request.Width, request.Height);
        Loaded = LoadAsync(onFailed);
    }

    public ImageRequest Request { get; }

    /// <summary>The decoded picture; null until the decode lands (or if it failed).</summary>
    [Reactive] public Bitmap? Bitmap { get; private set; }

    /// <summary>Rendered size in DIPs (<see cref="ImageCommand.FitSize"/>);
    /// NaN until known, which lets the control size itself.</summary>
    [Reactive] public double DisplayWidth  { get; private set; } = double.NaN;
    [Reactive] public double DisplayHeight { get; private set; } = double.NaN;

    /// <summary>True once the decode finished without a picture.</summary>
    public bool Failed { get; private set; }

    /// <summary>Completes when the bitmap (or the failure) has been applied.</summary>
    public Task Loaded { get; }

    private async Task LoadAsync(Action<string>? onFailed)
    {
        // Never finish inside the constructor: a failure report must land AFTER
        // the caller has added this picture's line, not above it.
        await Task.Yield();
        var bmp = await InlineImageCache.GetAsync(Request.Path).ConfigureAwait(false);

        void Apply()
        {
            if (bmp is null)
            {
                Failed = true;
                onFailed?.Invoke($"#img: could not decode {Request.DisplayName}.");
                return;
            }
            var (w, h) = ImageCommand.FitSize(bmp.PixelSize.Width, bmp.PixelSize.Height,
                                              Request.Width, Request.Height);
            DisplayWidth  = w;
            DisplayHeight = h;
            Bitmap        = bmp;
        }

        try
        {
            if (Dispatcher.UIThread.CheckAccess()) Apply();
            else await Dispatcher.UIThread.InvokeAsync(Apply);
        }
        catch (Exception ex)
        {
            Diagnostics.ErrorLog.Log("InlineImage.Apply", ex);
        }
    }

    /// <summary>A fresh control showing the picture. <see cref="TextLine.Inlines"/>
    /// is rebuilt on every access (an inline has one parent), so each call gets its
    /// own <see cref="Image"/> bound to this shared model.</summary>
    internal Control CreateControl()
    {
        var img = new Image
        {
            Stretch = Stretch.Fill,   // FitSize already chose the exact box, incl. a w:+h: stretch
            [!Image.SourceProperty]  = new Binding(nameof(Bitmap))        { Source = this },
            [!Control.WidthProperty] = new Binding(nameof(DisplayWidth))  { Source = this },
            [!Control.HeightProperty]= new Binding(nameof(DisplayHeight)) { Source = this },
        };
        ToolTip.SetTip(img, Request.DisplayName);
        return img;
    }
}
