using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using EposControl.Core;

namespace EposControl.App;

public sealed class MicrophoneActivityView : FrameworkElement
{

    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(168, 181, 200));
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(99, 219, 197));
    private static readonly Brush Activity = new SolidColorBrush(Color.FromRgb(92, 168, 240));
    private static readonly Brush GateGuide = new SolidColorBrush(Color.FromRgb(255, 203, 104));
    private MicrophoneMonitorFrame? frame;
    private float[]? eq;
    private bool eqEnabled, draft, showGate;
    private float? gate;
    private int highlighted = -1;
    public MicrophoneMonitorFrame? Frame => frame;
    public string SourceLabel { get; set; } = "Microphone";
    public float? GateGuidePercent => showGate ? gate : null;
    public IReadOnlyList<float>? Equalizer => eq;
    public event Action<int>? BandSelected;
    public MicrophoneActivityView() { Height = 170; MinWidth = 300; ToolTip = "Blue activity bars: left dBFS axis. EQ curve: right dB gain axis. Click a band to edit its EQ."; }
    public void SetFrame(MicrophoneMonitorFrame? value)
    {
        frame = value; InvalidateVisual();
        AutomationProperties.SetName(this, value is null ? SourceLabel + " activity stopped; no live audio values" :
            $"{SourceLabel} capture RMS {value.RmsDbFs:0.#} dBFS; peak {value.PeakDbFs:0.#} dBFS");
    }
    public void SetControls(float[]? gains, bool enabled, bool pending, float? gatePercent, bool gateVisible)
    {
        eq = gains?.ToArray(); eqEnabled = enabled; draft = pending; gate = gatePercent; showGate = gateVisible;
        InvalidateVisual();
    }
    public void HighlightBand(int index) { highlighted = index; InvalidateVisual(); }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var plot = PlotBounds;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        Text(dc, "Activity · dBFS", plot.Left, 0, 12, Activity);
        var eqLabel = "EQ · dB" + (draft ? " · draft" : "") + (eq is null ? " · unavailable" : eqEnabled ? "" : " · off (stored)");
        var legend = FormatText(eqLabel, 12, eqEnabled ? Accent : Muted);
        dc.DrawText(legend, new(Math.Max(plot.Left, plot.Right - legend.Width), 0));
        foreach (var db in new[] { 0, -30, -60, -90 }) {
            var y = ActivityY(db, plot);
            dc.DrawLine(new Pen(Muted, .35), new(plot.Left, y), new(plot.Right, y));
            Text(dc, db.ToString(CultureInfo.InvariantCulture), 7, y - 7, 10, Activity);
        }
        for (var i = 0; i < 9; i++) {
            var x = BandX(i, plot);
            var value = frame?.BandRmsDbFs.Count == 9 ? frame.BandRmsDbFs[i] : null;
            if (value is { } db && double.IsFinite(db)) {
                var y = ActivityY(db, plot);
                var barWidth = Math.Clamp(plot.Width / 9 * .34, 8, 28);
                dc.PushOpacity(i == highlighted ? .85 : .5);
                dc.DrawRoundedRectangle(Activity, null, new(x - barWidth / 2, y, barWidth, plot.Bottom - y), 3, 3);
                dc.Pop();
            } else Text(dc, "—", x - 4, plot.Bottom - 12, 10, Muted);
            var label = MicrophoneEqPresets.B20BandLabels[i].Replace(" Hz", "").Replace(" kHz", "k");
            Text(dc, label, x - (label.Length * 2.6), plot.Bottom + 8, 10, Muted);
        }
        if (showGate && gate is { } percent) {
            var y = ActivityY(MicrophoneGateGuide.SuiteScaleDb(percent), plot);
            dc.DrawLine(new Pen(GateGuide, 1) { DashStyle = DashStyles.Dash }, new(plot.Left, y), new(plot.Right, y));
            Text(dc, $"Gate {percent:0.#}% · visual guide", plot.Left + 5, Math.Max(plot.Top + 3, y - 16), 10, GateGuide);
        }
        var eqBrush = eqEnabled && eq is not null ? Accent : Muted;
        foreach (var db in new[] { 6, 0, -6 }) {
            var y = EqualizerY(db, plot);
            if (db == 0) dc.DrawLine(new Pen(Muted, .65) { DashStyle = DashStyles.Dot }, new(plot.Left, y), new(plot.Right, y));
            Text(dc, db > 0 ? "+6" : db.ToString(CultureInfo.InvariantCulture), plot.Right + 7, y - 7, 10, eqBrush);
        }
        if (eq is { Length: 9 }) {
            for (var i = 0; i < 9; i++) {
                var point = new Point(BandX(i, plot), EqualizerY(eq[i], plot));
                if (i > 0) {
                    var previous = new Point(BandX(i - 1, plot), EqualizerY(eq[i - 1], plot));
                    dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(24, 35, 52)), 5), previous, point);
                    dc.DrawLine(new Pen(eqBrush, 2), previous, point);
                }
            }
            for (var i = 0; i < 9; i++)
                dc.DrawEllipse(eqBrush, null, new(BandX(i, plot), EqualizerY(eq[i], plot)),
                    i == highlighted ? 5 : 3.5, i == highlighted ? 5 : 3.5);
        }
    }
    private Rect PlotBounds => new(43, 29, Math.Max(1, ActualWidth - 86), Math.Max(1, ActualHeight - 55));
    private static double BandX(int index, Rect plot) => plot.Left + (index + .5) / 9 * plot.Width;
    private static double ActivityY(double db, Rect plot) => plot.Top + Math.Clamp(-db / 90, 0, 1) * plot.Height;
    private static double EqualizerY(double db, Rect plot) => plot.Top + (6 - db) / 12 * plot.Height;
    private FormattedText FormatText(string text, double size, Brush brush) => new(text, CultureInfo.CurrentCulture,
        FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    private void Text(DrawingContext dc, string text, double x, double y, double size, Brush brush) => dc.DrawText(FormatText(text, size, brush), new(x, y));
    private int BandAt(Point point) => Math.Clamp((int)((point.X - PlotBounds.Left) / PlotBounds.Width * 9), 0, 8);
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e); var i = BandAt(e.GetPosition(this)); HighlightBand(i);
        var value = frame?.BandRmsDbFs.Count == 9 ? frame.BandRmsDbFs[i] : null;
        ToolTip = MicrophoneEqPresets.B20BandLabels[i] + ": " + (value is { } db ? $"{db:0.#} dBFS activity" : "no live value") +
            (eq is { Length: 9 } ? $"; EQ {eq[i]:+0.##;-0.##;0} dB" : "") + ". Click to edit EQ.";
    }
    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); HighlightBand(-1); }
    protected override void OnMouseDown(MouseButtonEventArgs e) { base.OnMouseDown(e); if (e.ChangedButton == MouseButton.Left) BandSelected?.Invoke(BandAt(e.GetPosition(this))); }
}
