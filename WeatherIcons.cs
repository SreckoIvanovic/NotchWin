using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using NotchWin.Services;

namespace NotchWin;

internal static class WeatherIcons
{
    private static readonly Color Sun = Color.FromRgb(0xFF, 0xD6, 0x0A);
    private static readonly Color Moon = Color.FromRgb(0xE5, 0xE5, 0xEA);
    private static readonly Color Cloud = Color.FromRgb(0xF2, 0xF2, 0xF7);
    private static readonly Color Rain = Color.FromRgb(0x64, 0xD2, 0xFF);
    private static readonly Color Snow = Color.FromRgb(0xBF, 0xE9, 0xFF);
    private static readonly Color Fog = Color.FromRgb(0xC7, 0xC7, 0xCC);

    private const string SunBig =
        "M16.5,12 A4.5,4.5 0 1 1 7.5,12 A4.5,4.5 0 1 1 16.5,12 Z M12,2 V4 M12,20 V22 M2,12 H4 M20,12 H22 " +
        "M4.9,4.9 L6.3,6.3 M17.7,17.7 L19.1,19.1 M4.9,19.1 L6.3,17.7 M17.7,6.3 L19.1,4.9";
    private const string MoonBig = "M20,14.5 A8,8 0 1 1 9.5,4 A6.5,6.5 0 0 0 20,14.5 Z";
    private const string SunSmall =
        "M11,8 A3,3 0 1 1 5,8 A3,3 0 1 1 11,8 Z M8,2.2 V3.4 M2.2,8 H3.4 M3.9,3.9 L4.7,4.7 M12.1,3.9 L11.3,4.7";
    private const string MoonSmall = "M11.5,9.5 A4.5,4.5 0 1 1 6,3.5 A3.6,3.6 0 0 0 11.5,9.5 Z";
    private const string CloudFull = "M7,19 H17 A4,4 0 0 0 17,11 A5.5,5.5 0 0 0 6.6,12.5 A3.3,3.3 0 0 0 7,19 Z";
    private const string CloudSide = "M9.5,20 H18 A3,3 0 0 0 18,14 A4.5,4.5 0 0 0 9.8,13.4 A3.3,3.3 0 0 0 9.5,20 Z";
    private const string CloudHigh = "M7,15 H17 A3.5,3.5 0 0 0 17,8 A5,5 0 0 0 7.2,8.8 A3.2,3.2 0 0 0 7,15 Z";
    private const string Drops = "M9,18 L8,21 M13,18 L12,21 M17,18 L16,21";
    private const string Bolt = "M13,15.5 L10.5,19 H13.5 L11.5,22";
    private const string Flake =
        "M12,3 V21 M4.2,7.5 L19.8,16.5 M4.2,16.5 L19.8,7.5 M9.7,4.6 L12,6.4 L14.3,4.6 M9.7,19.4 L12,17.6 L14.3,19.4";
    private const string FogLines = "M4,8 H20 M6,12 H18 M4,16 H20";

    public static void Draw(Canvas canvas, WeatherKind kind, bool day)
    {
        canvas.Children.Clear();
        switch (kind)
        {
            case WeatherKind.Clear:
                Add(canvas, day ? SunBig : MoonBig, day ? Sun : Moon, false);
                break;
            case WeatherKind.PartlyCloudy:
                Add(canvas, day ? SunSmall : MoonSmall, day ? Sun : Moon, false);
                Add(canvas, CloudSide, Cloud, true);
                break;
            case WeatherKind.Cloudy:
                Add(canvas, CloudFull, Cloud, true);
                break;
            case WeatherKind.Fog:
                Add(canvas, FogLines, Fog, false);
                break;
            case WeatherKind.Rain:
                Add(canvas, CloudHigh, Cloud, true);
                Add(canvas, Drops, Rain, false);
                break;
            case WeatherKind.Snow:
                Add(canvas, Flake, Snow, false);
                break;
            case WeatherKind.Thunder:
                Add(canvas, CloudHigh, Cloud, true);
                Add(canvas, Bolt, Sun, false);
                break;
        }
    }

    private static void Add(Canvas c, string data, Color color, bool occlude)
    {
        c.Children.Add(new Path
        {
            Data = Geometry.Parse(data),
            Stroke = new SolidColorBrush(color),
            StrokeThickness = 1.9,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Fill = occlude ? Brushes.Black : null,
        });
    }
}
