using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using NetRoute.Core.PathMap;

namespace NetRoute.App;

/// The small latency graph under a device's details: latency over the window, a lost check as a red tick on the baseline, a check that
/// could not be measured as a grey one. Shared by the path map's and the Local network tab's detail panels.
static class SparkLine
{
    public static void Draw(Canvas spark, IReadOnlyList<Sample> series, Func<string, Brush> resource)
    {
        spark.Children.Clear();
        if (series.Count == 0) return;

        var values = series.Where(s => s.Outcome == ProbeOutcome.Reply && s.RttMs is not null).Select(s => s.RttMs!.Value).ToList();
        var max = Math.Max(1, values.Count == 0 ? 1 : values.Max()) * 1.15;
        var step = series.Count > 1 ? (spark.Width - 10) / (series.Count - 1) : 0;
        spark.Children.Add(new Line { X1 = 0, X2 = spark.Width, Y1 = 44, Y2 = 44, Stroke = resource("CardBorder"), StrokeThickness = 1 });

        var points = new PointCollection();
        void Flush()
        {
            if (points.Count > 1) spark.Children.Add(new Polyline { Points = points, Stroke = resource("Accent"), StrokeThickness = 1.8, StrokeLineJoin = PenLineJoin.Round });
            else if (points.Count == 1) spark.Children.Add(new Ellipse { Width = 3, Height = 3, Fill = resource("Accent"), Margin = new Thickness(points[0].X - 1.5, points[0].Y - 1.5, 0, 0) });
            points = new PointCollection();
        }

        for (var i = 0; i < series.Count; i++)
        {
            var x = 5 + i * step;
            switch (series[i].Outcome)
            {
                case ProbeOutcome.Reply:
                    points.Add(new Point(x, 44 - (series[i].RttMs ?? 0) / max * 38));
                    break;
                default:
                    Flush();
                    spark.Children.Add(new Line
                    {
                        X1 = x, X2 = x, Y1 = 46, Y2 = 54, StrokeThickness = 2,
                        Stroke = resource(series[i].Outcome == ProbeOutcome.NoReply ? "DotRed" : "DotGray"),
                    });
                    break;
            }
        }
        Flush();
    }
}
