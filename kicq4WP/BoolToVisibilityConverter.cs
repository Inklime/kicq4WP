using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Data;

namespace kicq4WP
{
    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            bool visible = (value is bool && (bool)value);
            return visible ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            return (value is Visibility && (Visibility)value == Visibility.Visible);
        }
    }
}
