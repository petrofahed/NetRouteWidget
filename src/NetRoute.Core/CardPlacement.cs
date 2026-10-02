namespace NetRoute.Core;

public sealed record Bounds(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
}

public static class CardPlacement
{
    public const double Margin = 16;

    /// Keeps the saved position when the whole card still fits on the (multi-monitor) virtual screen;
    /// otherwise, e.g. after a monitor was unplugged, puts it bottom-right of the primary work area.
    public static (double Left, double Top) Resolve(
        double? savedLeft, double? savedTop, double cardWidth, double cardHeight, Bounds virtualScreen, Bounds workArea)
    {
        if (savedLeft is { } left && savedTop is { } top &&
            left >= virtualScreen.Left && top >= virtualScreen.Top &&
            left + cardWidth <= virtualScreen.Right && top + cardHeight <= virtualScreen.Bottom)
            return (left, top);

        return (workArea.Right - cardWidth - Margin, workArea.Bottom - cardHeight - Margin);
    }
}
