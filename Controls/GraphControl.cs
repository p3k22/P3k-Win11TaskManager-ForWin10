using System;
using System.Windows;
using System.Windows.Media;

namespace Win11TaskMan.Controls;

/// <summary>
/// Rolling filled-area graph. Drawn in OnRender so it stays cheap and looks
/// like the real Performance tab. Set ShowGrid=false for sparklines.
///
/// By default pushed values are treated as 0..1 fractions (CPU %, memory %).
/// Set AutoScale=true for unbounded units (network bytes/sec): values are then
/// stored raw and normalized at render against the largest value in the
/// visible window, so the whole trace rescales together the way Task Manager
/// does — instead of each sample being frozen against a moving scale.
/// </summary>
public sealed class GraphControl : FrameworkElement
{
    private readonly double[] _data;
    private readonly double[]? _data2; // optional 2nd series (e.g. Send), drawn as a dashed line
    private int _head;
    private double _scale;             // smoothed auto-scale max (fast attack, slow release)
    private const double ScaleRelease = 0.9; // per-sample decay when the peak shrinks (~0.9/s)
    private readonly Brush _fill;
    private readonly Pen _line;
    private readonly Pen? _line2;      // dashed pen for the 2nd series
    private readonly Pen _grid;

    public bool ShowGrid { get; set; } = true;

    /// <summary>Normalize against the window max instead of a fixed 1.0 ceiling.</summary>
    public bool AutoScale { get; set; }

    /// <summary>Lower bound for the auto-scale max, in pushed-value units.</summary>
    public double ScaleFloor { get; set; }

    /// <summary>
    /// Optional: maps the raw window max to the display scale (e.g. round up to a
    /// "nice" 1/2/5 value with a sensible minimum). Applied when AutoScale is on.
    /// </summary>
    public Func<double, double>? ScaleRounder { get; set; }

    /// <summary>
    /// The value the trace is normalized against: the smoothed window max (floored at
    /// ScaleFloor, then shaped by ScaleRounder if set). Returns 1.0 when AutoScale is off.
    /// </summary>
    public double NormalizationScale()
    {
        if (!AutoScale) return 1.0;
        double s = _scale > 0 ? _scale : ScaleFloor;
        if (ScaleRounder != null) s = ScaleRounder(s);
        return s <= 0 ? 1.0 : s;
    }

    public GraphControl() : this(Color.FromRgb(0x17, 0xA9, 0xC4), 60) { }

    public GraphControl(Color accent, int points, bool dualSeries = false)
    {
        _data = new double[points];
        var fill = new LinearGradientBrush(
            Color.FromArgb(0xB0, accent.R, accent.G, accent.B),
            Color.FromArgb(0x30, accent.R, accent.G, accent.B), 90);
        fill.Freeze();
        _fill = fill;
        _line = new Pen(new SolidColorBrush(accent), 1.2); _line.Freeze();
        _grid = new Pen(new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)), 0.5); _grid.Freeze();
        if (dualSeries)
        {
            _data2 = new double[points];
            _line2 = new Pen(new SolidColorBrush(accent), 1) { DashStyle = new DashStyle(new double[] { 2, 2 }, 0) };
            _line2.Freeze();
        }
    }

    /// <summary>Push one value (single-series graphs).</summary>
    public void Push(double value) => Store(value, value);

    /// <summary>Push both series (dual-series graphs): primary is filled+solid, secondary is dashed.</summary>
    public void Push(double primary, double secondary) => Store(primary, secondary);

    private void Store(double primary, double secondary)
    {
        _data[_head] = AutoScale ? Math.Max(primary, 0) : Math.Clamp(primary, 0, 1);
        if (_data2 != null) _data2[_head] = AutoScale ? Math.Max(secondary, 0) : Math.Clamp(secondary, 0, 1);
        _head = (_head + 1) % _data.Length;
        if (AutoScale) UpdateScale();
        InvalidateVisual();
    }

    // Fast attack / slow release on the auto-scale max: jump up at once to fit a new
    // peak (so spikes never clip), but ease back down gradually so the axis doesn't
    // snap — and the whole graph rescale — the instant a spike scrolls off the window.
    private void UpdateScale()
    {
        double target = ScaleFloor;
        foreach (double d in _data) if (d > target) target = d;
        if (_data2 != null) foreach (double d in _data2) if (d > target) target = d;
        _scale = target >= _scale ? target : Math.Max(target, _scale * ScaleRelease);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        double scale = NormalizationScale();

        int n = _data.Length;
        double dx = w / (n - 1);

        if (ShowGrid)
        {
            for (int i = 1; i < 5; i++)
            {
                double y = h * i / 5.0;
                dc.DrawLine(_grid, new Point(0, y), new Point(w, y));
            }
            for (int i = 1; i < 6; i++)
            {
                double x = w * i / 6.0;
                dc.DrawLine(_grid, new Point(x, 0), new Point(x, h));
            }
        }
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(0, h), true, true);
            for (int i = 0; i < n; i++)
            {
                double v = _data[(_head + i) % n] / scale;
                ctx.LineTo(new Point(i * dx, h - v * h), true, false);
            }
            ctx.LineTo(new Point(w, h), true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(_fill, null, geo);

        // top stroke only
        var stroke = new StreamGeometry();
        using (var ctx = stroke.Open())
        {
            bool first = true;
            for (int i = 0; i < n; i++)
            {
                double v = _data[(_head + i) % n] / scale;
                var pt = new Point(i * dx, h - v * h);
                if (first) { ctx.BeginFigure(pt, false, false); first = false; }
                else ctx.LineTo(pt, true, false);
            }
        }
        stroke.Freeze();
        dc.DrawGeometry(null, _line, stroke);

        // second series (e.g. Send): dashed line, no fill
        if (_data2 != null && _line2 != null)
        {
            var s2 = new StreamGeometry();
            using (var ctx = s2.Open())
            {
                bool first = true;
                for (int i = 0; i < n; i++)
                {
                    double v = _data2[(_head + i) % n] / scale;
                    var pt = new Point(i * dx, h - v * h);
                    if (first) { ctx.BeginFigure(pt, false, false); first = false; }
                    else ctx.LineTo(pt, true, false);
                }
            }
            s2.Freeze();
            dc.DrawGeometry(null, _line2, s2);
        }
    }
}
