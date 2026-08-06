using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Harugeki.Formats;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace HarugekiStudio.Views;

public partial class DopeSheetView : UserControl
{
    public static readonly StyledProperty<IEnumerable<RingAnimation>> AnimationsProperty =
        AvaloniaProperty.Register<DopeSheetView, IEnumerable<RingAnimation>>(nameof(Animations));

    public static readonly StyledProperty<RingAnimation?> SelectedAnimationProperty =
        AvaloniaProperty.Register<DopeSheetView, RingAnimation?>(nameof(SelectedAnimation));

    public static readonly StyledProperty<double> CurrentTimeProperty =
        AvaloniaProperty.Register<DopeSheetView, double>(nameof(CurrentTime));

    public static readonly StyledProperty<double> DurationProperty =
        AvaloniaProperty.Register<DopeSheetView, double>(nameof(Duration));

    public IEnumerable<RingAnimation> Animations
    {
        get => GetValue(AnimationsProperty);
        set => SetValue(AnimationsProperty, value);
    }

    public RingAnimation? SelectedAnimation
    {
        get => GetValue(SelectedAnimationProperty);
        set => SetValue(SelectedAnimationProperty, value);
    }

    public double CurrentTime
    {
        get => GetValue(CurrentTimeProperty);
        set => SetValue(CurrentTimeProperty, value);
    }

    public double Duration
    {
        get => GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    static DopeSheetView()
    {
        AffectsRender<DopeSheetView>(AnimationsProperty, SelectedAnimationProperty, CurrentTimeProperty, DurationProperty);
    }

    public override void Render(DrawingContext context)
    {
        var anim = SelectedAnimation;
        if (anim is null || anim.Tracks.Count == 0)
        {
            var text = new FormattedText(
                "No animation selected",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Consolas, monospace"),
                11,
                Brushes.Gray);
            context.DrawText(text, new Point(Bounds.Width / 2 - text.Width / 2, Bounds.Height / 2 - text.Height / 2));
            return;
        }

        double trackHeight = 20;
        double headerWidth = 150;
        double timelineWidth = Bounds.Width - headerWidth;
        double timeScale = timelineWidth / (Duration > 0 ? Duration : 1);

        var keyframePen = new Pen(new SolidColorBrush(Color.Parse("#FFCC00")), 2);
        var playheadPen = new Pen(new SolidColorBrush(Color.Parse("#FF4444")), 1);
        var linePen = new Pen(new SolidColorBrush(Color.Parse("#333333")), 1);

        for (int i = 0; i < anim.Tracks.Count; i++)
        {
            double y = i * trackHeight;
            var text = new FormattedText(
                anim.Tracks[i].Name,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Consolas, monospace"),
                11,
                Brushes.Gray);
            context.DrawText(text, new Point(4, y + 4));
            context.DrawLine(linePen, new Point(headerWidth, y + trackHeight), new Point(Bounds.Width, y + trackHeight));

            foreach (float t in anim.Tracks[i].Times)
            {
                double x = headerWidth + (t / RingAnimation.Fps) * timeScale;
                context.DrawLine(keyframePen, new Point(x, y + 2), new Point(x, y + trackHeight - 2));
            }
        }

        if (Duration > 0)
        {
            double px = headerWidth + CurrentTime * timeScale;
            context.DrawLine(playheadPen, new Point(px, 0), new Point(px, Bounds.Height));
        }
    }
}
