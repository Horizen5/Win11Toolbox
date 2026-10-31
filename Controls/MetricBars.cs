using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace Win11Toolbox.Controls;

/// <summary>
/// 每秒一根的柱状历史，走 macOS 活动监视器那种路子。
///
/// 为什么不用折线：折线在 200px 宽里画 40 个点，每秒的抖动会被连成锯齿，
/// 抗锯齿还会把 1px 线摊成半透明的灰；柱子天然对齐像素，边界清楚，
/// 而且"刚才那一下尖峰"比平滑曲线更直观。
///
/// 量程不再写死 100：满刻度跟着最近一段数据的峰值走（涨得快、落得慢），
/// 否则 CPU 跑 15% 时曲线只有 5px 起伏，等于没有信息。
/// </summary>
public sealed class MetricBars : FrameworkElement
{
    private readonly List<double> _values = new();
    private double _ceiling = double.NaN;

    /// <summary>窗口里保留多少根柱子（每秒一根）。</summary>
    public int Capacity { get; set; } = 36;

    /// <summary>百分比类指标：封顶 100，并按阈值变色。</summary>
    public bool IsPercent { get; set; }

    /// <summary>满刻度的下限，避免空载时把 2% 的抖动放大成满格。</summary>
    public double Floor { get; set; } = 20;

    /// <summary>
    /// 固定满刻度（&gt;0 时生效）。内存 / 磁盘这种"绝对占比"必须固定，
    /// 否则 43% 会被放大成满格，看着像要爆了。
    /// </summary>
    public double FixedCeiling { get; set; }

    public Brush? BarBrush
    {
        get => (Brush?)GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    public Brush? CautionBrush
    {
        get => (Brush?)GetValue(CautionBrushProperty);
        set => SetValue(CautionBrushProperty, value);
    }

    public Brush? CriticalBrush
    {
        get => (Brush?)GetValue(CriticalBrushProperty);
        set => SetValue(CriticalBrushProperty, value);
    }

    public Brush? BaselineBrush
    {
        get => (Brush?)GetValue(BaselineBrushProperty);
        set => SetValue(BaselineBrushProperty, value);
    }

    public static readonly DependencyProperty BarBrushProperty =
        DependencyProperty.Register(nameof(BarBrush), typeof(Brush), typeof(MetricBars),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CautionBrushProperty =
        DependencyProperty.Register(nameof(CautionBrush), typeof(Brush), typeof(MetricBars),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CriticalBrushProperty =
        DependencyProperty.Register(nameof(CriticalBrush), typeof(Brush), typeof(MetricBars),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BaselineBrushProperty =
        DependencyProperty.Register(nameof(BaselineBrush), typeof(Brush), typeof(MetricBars),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public void Push(double value)
    {
        _values.Add(double.IsNaN(value) || double.IsInfinity(value) ? 0 : Math.Max(0, value));
        while (_values.Count > Capacity)
            _values.RemoveAt(0);

        UpdateCeiling();
        InvalidateVisual();
    }

    public void Clear()
    {
        _values.Clear();
        _ceiling = double.NaN;
        InvalidateVisual();
    }

    /// <summary>满刻度追着峰值走：往上追得快（尖峰不被裁掉），往下退得慢（不跳）。</summary>
    private void UpdateCeiling()
    {
        if (FixedCeiling > 0)
        {
            _ceiling = FixedCeiling;
            return;
        }

        double peak = 0;
        foreach (var v in _values)
            if (v > peak) peak = v;

        double target = Math.Max(peak * 1.18, Floor);
        if (IsPercent) target = Math.Min(target, 100);

        if (double.IsNaN(_ceiling) || _ceiling <= 0)
        {
            _ceiling = target;
            return;
        }

        double k = target > _ceiling ? 0.55 : 0.10;
        _ceiling += (target - _ceiling) * k;
    }

    private Brush PickBrush(double value)
    {
        var normal = BarBrush ?? UiTheme.Accent;
        if (!IsPercent) return normal;
        if (value >= 85) return CriticalBrush ?? normal;
        if (value >= 70) return CautionBrush ?? normal;
        return normal;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth <= 1 || ActualHeight <= 1)
            return;

        double width = ActualWidth;
        double height = ActualHeight;
        var dpi = VisualTreeHelper.GetDpi(this);
        double scaleX = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
        double scaleY = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;

        // 按物理像素对齐：柱子边缘落在整数像素上，小尺寸下才不会糊成一片
        double SnapX(double x) => Math.Round(x * scaleX) / scaleX;
        double SnapY(double y) => Math.Round(y * scaleY) / scaleY;

        // 底线：一根很淡的横线，给柱子一个"地面"
        double baseY = SnapY(height - 1 / scaleY) + 0.5 / scaleY;
        if (BaselineBrush != null)
            dc.DrawLine(new Pen(BaselineBrush, 1), new Point(0, baseY), new Point(width, baseY));

        if (_values.Count == 0 || double.IsNaN(_ceiling) || _ceiling <= 0)
            return;

        double pitch = width / Capacity;
        double gap = pitch >= 5 ? 1.5 : pitch >= 3 ? 0.9 : 0.4;
        double top = 1.5;                          // 顶部留一点余量，满格时不贴边
        double usable = Math.Max(1, baseY - top - 1);

        int count = _values.Count;
        int last = count - 1;

        void DrawBar(int index)
        {
            // 从右往左排：最新的贴右边，窗口没填满时只在右侧长出来
            double left = width - (count - index) * pitch;
            double right = left + pitch - gap;
            double x0 = SnapX(left);
            double x1 = SnapX(right);
            double barWidth = Math.Max(1 / scaleX, x1 - x0);

            double ratio = Math.Clamp(_values[index] / _ceiling, 0, 1);
            // 最小 2px：网络空闲时数值接近 0，1px 的柱子肉眼看不见，
            // 整条图会像"没数据"；留 2px 至少能看出"有采样、只是很小"
            double barHeight = Math.Max(2 / scaleY, ratio * usable);
            var rect = new Rect(new Point(x0, baseY - barHeight), new Size(barWidth, barHeight));
            dc.DrawRectangle(PickBrush(_values[index]), null, rect);
        }

        // 历史柱子压到 75% 不透明度，最新那根满色 —— 一眼知道"现在"在哪
        dc.PushOpacity(0.75);
        for (int i = 0; i < last; i++)
            DrawBar(i);
        dc.Pop();

        DrawBar(last);
    }
}
