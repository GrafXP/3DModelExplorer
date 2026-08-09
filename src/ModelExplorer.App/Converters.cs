using System.Globalization;
using System.Windows;
using System.Windows.Data;
using MaterialDesignThemes.Wpf;
using ModelExplorer.App.ViewModels;

namespace ModelExplorer.App;

/// <summary>Visible when the bound flag is <c>false</c>. Used for empty states.</summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Visible only when the bound string has content.</summary>
public sealed class NonEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Byte count to a short human-readable size.
/// </summary>
/// <remarks>
/// Binary units, so the numbers agree with what Explorer shows for the same file
/// rather than being off by 2.4% against it.
/// </remarks>
public sealed class FileSizeConverter : IValueConverter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not long bytes || bytes < 0)
        {
            return string.Empty;
        }

        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < Units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        // Whole bytes read oddly as "512.0 B"; everything else gets one decimal.
        return unit == 0
            ? $"{bytes:N0} {Units[unit]}"
            : $"{size:N1} {Units[unit]}";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Picks the icon for a node in the library tree. Network roots are marked
/// because they behave differently — slower, and scanned under their own
/// concurrency limit — so it is worth seeing at a glance which is which.
/// </summary>
public sealed class FolderIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is FolderNode { Root.IsNetwork: true }
            ? PackIconKind.FolderNetworkOutline
            : PackIconKind.FolderOutline;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>A UTC timestamp shown in the user's own time zone.</summary>
public sealed class LocalTimeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DateTime utc
            ? utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", culture)
            : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
