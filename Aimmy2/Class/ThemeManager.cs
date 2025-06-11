using System.Windows;
using System.Windows.Media;

namespace Aimmy2.Class
{
    public static class ThemeManager
    {
        public static void SetThemeColor(string colorString)
        {
            if (Application.Current == null) return;

            try
            {
                var color = (Color)ColorConverter.ConvertFromString(colorString);
                var brush = new SolidColorBrush(color);
                brush.Freeze(); // Freeze for performance

                Application.Current.Resources["ThemeColor"] = color;
                Application.Current.Resources["ThemeColorBrush"] = brush;
            }
            catch (FormatException)
            {
                // Fallback to a default color if the string is invalid
                var defaultColor = (Color)ColorConverter.ConvertFromString("#FF722ED1");
                var defaultBrush = new SolidColorBrush(defaultColor);
                defaultBrush.Freeze();

                Application.Current.Resources["ThemeColor"] = defaultColor;
                Application.Current.Resources["ThemeColorBrush"] = defaultBrush;
            }
        }
    }
} 