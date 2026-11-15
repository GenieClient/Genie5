
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Genie.App.Controls;

/// <summary>
/// Black or white text for a label drawn over a coloured fill, whichever reads
/// better (WCAG relative luminance). Used by the roundtime countdown bar, whose
/// fill is the user's <c>roundtime</c> preset (#418 review): a light fill gets
/// black text, a dark one white. No fill (the preset is Default) keeps white.
/// </summary>
public static class ContrastText
{
    public static readonly IValueConverter Converter =
        new FuncValueConverter<IBrush?, IBrush>(fill =>
            fill is ISolidColorBrush { Color: var c } && Luminance(c) > 0.179
                ? Brushes.Black
                : Brushes.White);

    /// <summary>WCAG 2 relative luminance of <paramref name="c"/> (0 = black, 1 = white).</summary>
    public static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }
}
