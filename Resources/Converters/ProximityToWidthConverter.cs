using System;
using System.Globalization;
using System.Windows.Data;

namespace VISOR.Resources.Converters
{
    public class ProximityToWidthConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length < 2 || !(values[0] is double ratio) || !(values[1] is double containerWidth))
            {
                return 0.0;
            }

            return ratio * containerWidth;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}