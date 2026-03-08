using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace FileX.Controls;

/// <summary>
/// Adorner that draws a semi-transparent selection rectangle over a ListView
/// for marquee (rubber-band) selection.
/// </summary>
public class MarqueeAdorner : Adorner
{
    private Rect _rect;
    private bool _isVisible;

    private static readonly Brush FillBrush;
    private static readonly Pen StrokePen;

    static MarqueeAdorner()
    {
        FillBrush = new SolidColorBrush(Color.FromArgb(40, 108, 108, 240));
        FillBrush.Freeze();
        StrokePen = new Pen(new SolidColorBrush(Color.FromRgb(108, 108, 240)), 1);
        StrokePen.Freeze();
    }

    public MarqueeAdorner(UIElement adornedElement) : base(adornedElement)
    {
        IsHitTestVisible = false;
    }

    public void Update(Point start, Point end)
    {
        _rect = new Rect(start, end);
        _isVisible = true;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (!_isVisible) return;
        dc.DrawRectangle(FillBrush, StrokePen, _rect);
    }
}
