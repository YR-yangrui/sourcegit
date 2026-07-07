using System;
using System.Collections.Generic;
using System.Globalization;

using Avalonia.Data.Converters;
using Avalonia.Media;

namespace SourceGit.Converters
{
    public static class AuthorNameBorderConverters
    {
        public class ToBrushConverter : IMultiValueConverter
        {
            public object Convert(IList<object> values, Type targetType, object parameter, CultureInfo culture)
            {
                if (values.Count < 3 ||
                    values[0] is not bool isCurrentGitUserAuthor ||
                    values[1] is not bool isEnabled ||
                    values[2] is not uint color ||
                    !isCurrentGitUserAuthor ||
                    !isEnabled)
                {
                    return Brushes.Transparent;
                }

                return new SolidColorBrush(Color.FromUInt32(color));
            }
        }

        public static readonly ToBrushConverter ToBrush = new();

        public static readonly FuncValueConverter<double, double> ToMaxWidth =
            new(v => Math.Max(0, v - 4));
    }
}
