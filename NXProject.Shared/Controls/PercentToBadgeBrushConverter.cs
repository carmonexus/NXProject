using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace NXProject.Controls
{
    public sealed class PercentToBadgeBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double pct = value is double d ? d : 0;
            if (pct <= 0)
                return new SolidColorBrush(Color.FromRgb(158, 158, 158)); // cinza — não iniciado
            if (pct >= 100)
                return new SolidColorBrush(Color.FromRgb(33, 115, 70));   // verde — concluído
            return new SolidColorBrush(Color.FromRgb(30, 100, 200));       // azul — em andamento
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }
}
