using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Harugeki.Formats;
using System.Globalization;

namespace HarugekiStudio.Views;

/// <summary>
/// The keyframe timeline: one row per animation track, a tick at every key, and
/// a playhead at the current time.
///
/// <para>
/// Drawn directly rather than composed from controls — a clip can carry fifty
/// tracks with hundreds of keys between them, and that is far more elements than
/// is reasonable to template. There is no XAML file for the same reason: the one
/// that used to exist was never loaded, and its opaque Canvas would have covered
/// this drawing entirely if it ever had been.
/// </para>
/// </summary>
public class DopeSheetView : Control
{
    private const double TrackHeight = 20;
    private const double HeaderWidth = 150;
    private const double LabelPadding = 6;
    private const double LabelSize = 11;

    private static readonly Typeface s_typeface = new("Consolas,Cascadia Mono,monospace");

    /// <summary>
    /// Pens and brushes for the current theme variant, rebuilt whenever it
    /// changes.
    ///
    /// <para>
    /// They cannot be <see langword="static" /> <see langword="readonly" />: that
    /// resolves them once at type load, against whichever theme happened to be
    /// active then, and they would never follow the OS switching to light. Nor can
    /// they be rebuilt per frame — <see cref="Render"/> runs every frame while a
    /// clip is playing.
    /// </para>
    /// </summary>
    private IBrush _labelBrush = Brushes.Gray;
    private IPen _keyframePen = new Pen(Brushes.Gray, 2);
    private IPen _playheadPen = new Pen(Brushes.Gray, 1.5);
    private IPen _gridPen = new Pen(Brushes.Gray, 1);

    public static readonly StyledProperty<RingAnimation?> SelectedAnimationProperty =
        AvaloniaProperty.Register<DopeSheetView, RingAnimation?>(nameof(SelectedAnimation));

    public static readonly StyledProperty<double> CurrentTimeProperty =
        AvaloniaProperty.Register<DopeSheetView, double>(nameof(CurrentTime));

    public static readonly StyledProperty<double> DurationProperty =
        AvaloniaProperty.Register<DopeSheetView, double>(nameof(Duration));

    static DopeSheetView()
    {
        AffectsRender<DopeSheetView>(SelectedAnimationProperty, CurrentTimeProperty, DurationProperty);
        AffectsMeasure<DopeSheetView>(SelectedAnimationProperty);
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

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        RebuildBrushes();
    }

    public DopeSheetView()
    {
        // Notified as an event rather than an override: StyledElement exposes
        // ActualThemeVariantChanged, not a virtual On… hook.
        ActualThemeVariantChanged += (_, _) =>
        {
            RebuildBrushes();
            InvalidateVisual();
        };
    }

    private void RebuildBrushes()
    {
        _labelBrush = Resolve("TextMuted");
        _keyframePen = new Pen(Resolve("KeyframeBrush"), 2);
        _playheadPen = new Pen(Resolve("PlayheadBrush"), 1.5);
        _gridPen = new Pen(Resolve("Divider"), 1);

        IBrush Resolve(string key)
        {
            return this.TryFindResource(key, ActualThemeVariant, out object? value) && value is IBrush brush
                ? brush
                : Brushes.Gray;
        }
    }

    /// <summary>
    /// Asks for the height every track needs, so the enclosing scroll viewer has
    /// something to scroll. Without this a 49-bone skeleton was simply clipped at
    /// the bottom of the pane with no way to reach the rest.
    /// </summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        int tracks = SelectedAnimation?.Tracks.Count ?? 0;
        double height = Math.Max(TrackHeight * 2, tracks * TrackHeight);
        double width = double.IsInfinity(availableSize.Width) ? HeaderWidth * 3 : availableSize.Width;
        return new Size(width, height);
    }

    public override void Render(DrawingContext context)
    {
        RingAnimation? clip = SelectedAnimation;

        if (clip is null || clip.Tracks.Count == 0)
        {
            DrawPlaceholder(context);
            return;
        }

        double timelineWidth = Math.Max(1, Bounds.Width - HeaderWidth);
        double scale = timelineWidth / (Duration > 0 ? Duration : 1);

        for (int i = 0; i < clip.Tracks.Count; i++)
        {
            RingAnimation.Track track = clip.Tracks[i];
            double y = i * TrackHeight;

            FormattedText label = Text(track.Name);
            context.DrawText(label, new Point(LabelPadding, y + ((TrackHeight - label.Height) / 2)));

            context.DrawLine(
                _gridPen,
                new Point(HeaderWidth, y + TrackHeight),
                new Point(Bounds.Width, y + TrackHeight));

            foreach (float frame in track.Times)
            {
                double x = HeaderWidth + (frame / RingAnimation.Fps * scale);
                context.DrawLine(_keyframePen, new Point(x, y + 3), new Point(x, y + TrackHeight - 3));
            }
        }

        if (Duration > 0)
        {
            double x = HeaderWidth + (CurrentTime * scale);
            context.DrawLine(_playheadPen, new Point(x, 0), new Point(x, Bounds.Height));
        }
    }

    private void DrawPlaceholder(DrawingContext context)
    {
        FormattedText text = Text("No animation selected");
        context.DrawText(text, new Point(
            (Bounds.Width - text.Width) / 2,
            (Bounds.Height - text.Height) / 2));
    }

    private FormattedText Text(string value)
    {
        return new FormattedText(
            value,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            s_typeface,
            LabelSize,
            _labelBrush);
    }
}
