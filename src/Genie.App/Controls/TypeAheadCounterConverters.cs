using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Genie.App.Controls;

/// <summary>
/// Renders the command-bar type-ahead counter as a keyboard glyph followed by
/// one pip per available slot: filled (●) for in-flight commands, hollow (○)
/// for free slots — e.g. "⌨ ●●○" for 2 of 3 used. Bind multi: [InFlight, Limit].
/// Reference from XAML via <c>{x:Static controls:TypeAheadPipsConverter.Instance}</c>.
/// </summary>
public sealed class TypeAheadPipsConverter : IMultiValueConverter
{
    public static readonly TypeAheadPipsConverter Instance = new();

    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        int inFlight = values.Count > 0 && values[0] is int a ? a : 0;
        int limit    = values.Count > 1 && values[1] is int b ? b : 0;
        if (limit < 1)    limit = 1;
        if (inFlight < 0) inFlight = 0;
        if (inFlight > limit) inFlight = limit;

        var sb = new StringBuilder("⌨ ");
        for (int i = 0; i < limit; i++)
            sb.Append(i < inFlight ? '●' : '○');
        return sb.ToString();
    }
}

/// <summary>
/// Colours the type-ahead counter by state: dim grey when idle (nothing typed
/// ahead), normal when partially full, amber at the cap. Bind multi:
/// [InFlight, Limit]. Reference via <c>{x:Static controls:TypeAheadBrushConverter.Instance}</c>.
/// </summary>
public sealed class TypeAheadBrushConverter : IMultiValueConverter
{
    public static readonly TypeAheadBrushConverter Instance = new();

    private static readonly IBrush Idle    = new SolidColorBrush(Color.Parse("#5a5a5a"));
    private static readonly IBrush Partial = new SolidColorBrush(Color.Parse("#cfcfcf"));
    private static readonly IBrush Full     = new SolidColorBrush(Color.Parse("#ffc107"));

    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        int inFlight = values.Count > 0 && values[0] is int a ? a : 0;
        int limit    = values.Count > 1 && values[1] is int b ? b : 0;
        if (limit < 1) limit = 1;

        if (inFlight <= 0)      return Idle;
        if (inFlight >= limit)  return Full;
        return Partial;
    }
}
