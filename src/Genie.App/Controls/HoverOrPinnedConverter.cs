using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Genie.App.Controls;

/// <summary>
/// Decides whether the Mapper's Details flyout is open (#450). Three sources, in
/// order: the flyout is hovered, opening on hover is enabled, the panel is
/// pinned. Open when pinned, or when hovered with hover-to-open enabled — so
/// with the option off the panel opens only by clicking its strip (which pins).
/// </summary>
public sealed class HoverOrPinnedConverter : IMultiValueConverter
{
    public static readonly HoverOrPinnedConverter Instance = new();

    public object Convert(IList<object?> values, System.Type targetType, object? parameter, CultureInfo culture)
    {
        bool hovered      = values.Count > 0 && values[0] is true;
        bool hoverEnabled = values.Count > 1 && values[1] is true;
        bool pinned       = values.Count > 2 && values[2] is true;
        return pinned || (hovered && hoverEnabled);
    }
}
