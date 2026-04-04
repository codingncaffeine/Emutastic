using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Emutastic.Services;

namespace Emutastic.Converters
{
    public class InputTypeToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is InputType inputType)
            {
                return inputType switch
                {
                    InputType.Keyboard => new SolidColorBrush(Color.FromRgb(76, 175, 80)), // Green
                    InputType.Controller => new SolidColorBrush(Color.FromRgb(33, 150, 243)), // Blue
                    _ => new SolidColorBrush(Color.FromRgb(158, 158, 158)) // Gray
                };
            }
            return new SolidColorBrush(Color.FromRgb(158, 158, 158));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class InputTypeToSymbolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is InputType inputType)
            {
                return inputType switch
                {
                    InputType.Keyboard => "⌨",
                    InputType.Controller => "🎮",
                    _ => "?"
                };
            }
            return "?";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
