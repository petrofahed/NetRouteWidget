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

    public const double PopupGap = 8;

    /// Where the "LAN is offline" popup goes: directly above the card (left edges aligned, kept inside the work area)
    /// so it never covers it; bottom-right of the work area when the card is hidden or there is no room above.
    public static (double Left, double Top) PopupAboveCard(Bounds? card, double width, double height, Bounds workArea)
    {
        if (card is { } c)
        {
            var top = c.Top - height - PopupGap;
            if (top >= workArea.Top)
                return (Math.Clamp(c.Left, workArea.Left, Math.Max(workArea.Left, workArea.Right - width)), top);
        }
        return (workArea.Right - width - Margin, workArea.Bottom - height - Margin);
    }
}
