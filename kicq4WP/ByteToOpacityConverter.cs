using System;
using Windows.UI.Xaml.Data;

namespace kicq4WP
{
    public class ByteToOpacityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is byte)
            {
                byte b = (byte)value;
                return (double)b / 255.0;
            }

            return 1.0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}