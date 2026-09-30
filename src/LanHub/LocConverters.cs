using System.Globalization;
using System.Windows.Data;
using LanHub.Core.Localization;

namespace LanHub;

public sealed class ReadyHostConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var ready = values.Length > 0 && values[0] is true;
        var host = values.Length > 1 && values[1] is true;
        return Loc.Tf("fmt.ready_host", Loc.T(ready ? "yes" : "no"), Loc.T(host ? "yes" : "no"));
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class ReadyOnlyConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var ready = value is true;
        return Loc.Tf("fmt.ready", Loc.T(ready ? "yes" : "no"));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class HostLineConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var ip = values.Length > 0 ? values[0]?.ToString() ?? "" : "";
        var port = values.Length > 1 ? values[1]?.ToString() ?? "" : "";
        var joinable = values.Length > 2 && values[2] is true;
        return $"{ip}:{port}{Loc.T("client.joinable")}{joinable}";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class LibraryMetaConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var id = value?.ToString() ?? "";
        return $"ID {id}{Loc.T("library.dblclick")}";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
