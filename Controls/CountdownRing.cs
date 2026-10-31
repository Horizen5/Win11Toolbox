using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Win11Toolbox.Controls;

/// <summary>
/// 2FA 验证码的倒计时圆圈：中间显示剩余秒数，外圈随剩余时间缩短，
/// 颜色按「绿 → 黄 → 红」走。
///
/// <para>
/// 为什么不用 Ellipse + StrokeDashArray 糊一个：<c>StrokeDashArray</c> 的长度单位是
/// 线宽的倍数，圆环半径一变就得重算，窗口缩放 / DPI 变化时对不齐，圆头端帽还会被裁掉。
/// 这里直接在 <see cref="OnRender"/> 里按当前尺寸画一段 ArcSegment，尺寸怎么变都准。
/// </para>
///
/// <para>
/// 颜色不是三段硬切，而是「绿→黄」在 60%~100% 之间过渡、20%~60% 保持黄、20% 以下再压向红，
/// 这样"中间是黄色"能占到三分之一以上的时间，看得清，又不会在换色瞬间跳一下。
/// </para>
/// </summary>
public sealed class CountdownRing : FrameworkElement
{
    public static readonly DependencyProperty ProgressProperty =
        DependencyProperty.Register(nameof(Progress), typeof(double), typeof(CountdownRing),
            new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SecondsProperty =
        DependencyProperty.Register(nameof(Seconds), typeof(int), typeof(CountdownRing),
            new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RingThicknessProperty =
        DependencyProperty.Register(nameof(RingThickness), typeof(double), typeof(CountdownRing),
            new FrameworkPropertyMetadata(4.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty =
        DependencyProperty.Register(nameof(TrackBrush), typeof(Brush), typeof(CountdownRing),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>剩余比例，1 = 刚刷新，0 = 马上跳下一个码。</summary>
    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    /// <summary>中间显示的剩余秒数。</summary>
    public int Seconds
    {
        get => (int)GetValue(SecondsProperty);
        set => SetValue(SecondsProperty, value);
    }

    public double RingThickness
    {
        get => (double)GetValue(RingThicknessProperty);
        set => SetValue(RingThicknessProperty, value);
    }

    /// <summary>底环颜色，不给就用主题的中性灰。</summary>
    public Brush? TrackBrush
    {
        get => (Brush?)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 6)
            return;

        double thickness = Math.Clamp(RingThickness, 1, size / 3);
        double radius = (size - thickness) / 2 - 0.5;   // 留半像素，圆头端帽不贴边
        var center = new Point(ActualWidth / 2, ActualHeight / 2);

        // 底环：比进度环淡一档，靠透明度而不是换颜色，深浅主题都成立
        var track = TrackBrush ?? UiTheme.Neutral;
        var trackPen = new Pen(track, thickness);
        if (track is SolidColorBrush solid)
        {
            var faded = new SolidColorBrush(solid.Color) { Opacity = 0.35 };
            faded.Freeze();
            trackPen = new Pen(faded, thickness);
        }

        dc.DrawEllipse(null, trackPen, center, radius, radius);

        double progress = Math.Clamp(Progress, 0, 1);

        if (progress > 0.004)
        {
            var brush = new SolidColorBrush(Blend(progress));
            brush.Freeze();

            var pen = new Pen(brush, thickness)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
            };

            if (progress >= 0.9995)
            {
                // 满圈时 ArcSegment 画不出 360°（起止点重合，几何退化成空），直接画圆
                dc.DrawEllipse(null, pen, center, radius, radius);
            }
            else
            {
                double sweep = progress * 360;
                var start = PointAt(center, radius, -90);
                var end = PointAt(center, radius, -90 + sweep);

                var geometry = new StreamGeometry();
                using (var ctx = geometry.Open())
                {
                    ctx.BeginFigure(start, false, false);
                    ctx.ArcTo(end, new Size(radius, radius), 0, sweep > 180,
                              SweepDirection.Clockwise, true, false);
                }

                geometry.Freeze();
                dc.DrawGeometry(null, pen, geometry);
            }
        }

        // 中心秒数
        if (Seconds > 0)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var typeface = new Typeface(
                new FontFamily("Segoe UI Variable Text, Segoe UI"),
                FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

            var text = new FormattedText(
                Seconds.ToString(CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                typeface,
                Math.Max(10, size * 0.32),
                UiTheme.TextPrimary,
                dpi.PixelsPerDip <= 0 ? 1 : dpi.PixelsPerDip);

            dc.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));
        }
    }

    private static Point PointAt(Point center, double radius, double degrees)
    {
        double rad = degrees * Math.PI / 180;
        return new Point(center.X + radius * Math.Cos(rad), center.Y + radius * Math.Sin(rad));
    }

    /// <summary>
    /// 颜色分段：剩余 75% 以上纯绿，75%→50% 渐变成黄，50%→20% 保持黄，20% 以下压向红。
    /// 以 30 秒周期算，就是「前 7.5 秒绿、接下来 7.5 秒转黄、中间 9 秒黄、最后 6 秒转红」。
    /// </summary>
    private static Color Blend(double progress)
    {
        var green = SolidColor(UiTheme.Success, Color.FromRgb(0x0F, 0x7B, 0x0F));
        var yellow = SolidColor(UiTheme.Caution, Color.FromRgb(0x9D, 0x5D, 0x00));
        var red = SolidColor(UiTheme.Critical, Color.FromRgb(0xC4, 0x2B, 0x1C));

        if (progress >= 0.75)
            return green;

        if (progress >= 0.5)
            return Lerp(yellow, green, (progress - 0.5) / 0.25);

        if (progress >= 0.2)
            return yellow;

        return Lerp(red, yellow, progress / 0.2);
    }

    private static Color SolidColor(Brush brush, Color fallback)
        => brush is SolidColorBrush solid ? solid.Color : fallback;

    private static Color Lerp(Color from, Color to, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(
            (byte)(from.A + (to.A - from.A) * t),
            (byte)(from.R + (to.R - from.R) * t),
            (byte)(from.G + (to.G - from.G) * t),
            (byte)(from.B + (to.B - from.B) * t));
    }
}
