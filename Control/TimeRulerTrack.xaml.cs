using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using Windows.UI;

namespace FluentDesigner.Control;

public sealed partial class TimeRulerTrack : UserControl
{
    private float _canvasWidth;
    private float _canvasHeight;

    private double _viewStartTime;
    private double _viewEndTime;
    private double _pixelsPerSecond = 100.0;

    private double? _inPoint;
    private double? _outPoint;

    private static readonly Color BackgroundColor = Color.FromArgb(255, 40, 40, 40);
    private static readonly Color RulerLineColor = Color.FromArgb(255, 100, 100, 100);
    private static readonly Color RulerTextColor = Color.FromArgb(255, 180, 180, 180);
    private static readonly Color InOutRegionColor = Color.FromArgb(120, 80, 160, 255);
    private static readonly Color InOutPointColor = Color.FromArgb(255, 100, 180, 255);
    private static readonly Color StrokeColor = Color.FromArgb(255, 60, 60, 60);

    private const float MajorTickHeight = 12f;
    private const float MinorTickHeight = 6f;
    private const float TickWidth = 1f;
    private const float PointSize = 6f;
    private const float RegionHeight = 10f;

    private DispatcherTimer _redrawDebounceTimer;

    public event EventHandler InOutPointsChanged;

    public double? InPoint => _inPoint;
    public double? OutPoint => _outPoint;
    public bool HasSelection => _inPoint.HasValue && _outPoint.HasValue;

    public TimeRulerTrack()
    {
        InitializeComponent();

        _redrawDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(8)
        };
        _redrawDebounceTimer.Tick += OnRedrawDebounce;

        Unloaded += OnUnloaded;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _redrawDebounceTimer.Stop();
    }

    public void UpdateView(double startTime, double endTime, double pixelsPerSecond)
    {
        if (Math.Abs(_viewStartTime - startTime) < 0.001 &&
            Math.Abs(_viewEndTime - endTime) < 0.001 &&
            Math.Abs(_pixelsPerSecond - pixelsPerSecond) < 0.1)
        {
            return;
        }

        _viewStartTime = startTime;
        _viewEndTime = endTime;
        _pixelsPerSecond = pixelsPerSecond;
        RequestRedraw();
    }

    public void SetInPoint(double time)
    {
        _inPoint = time;
        NormalizeInOutPoints();
        RequestRedraw();
        InOutPointsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetOutPoint(double time)
    {
        _outPoint = time;
        NormalizeInOutPoints();
        RequestRedraw();
        InOutPointsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearSelection()
    {
        _inPoint = null;
        _outPoint = null;
        RequestRedraw();
        InOutPointsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void NormalizeInOutPoints()
    {
        if (_inPoint.HasValue && _outPoint.HasValue && _inPoint.Value > _outPoint.Value)
        {
            (_inPoint, _outPoint) = (_outPoint, _inPoint);
        }
    }

    public (double start, double end)? GetSelection()
    {
        if (!HasSelection)
        {
            return null;
        }
        return (_inPoint!.Value, _outPoint!.Value);
    }

    public void InvalidateCanvas()
    {
        RulerCanvas.Invalidate();
    }

    private void RequestRedraw()
    {
        _redrawDebounceTimer.Stop();
        _redrawDebounceTimer.Start();
    }

    private void OnRedrawDebounce(object sender, object e)
    {
        _redrawDebounceTimer.Stop();
        RulerCanvas.Invalidate();
    }

    private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _canvasWidth = (float)e.NewSize.Width;
        _canvasHeight = (float)e.NewSize.Height;
        RequestRedraw();
    }

    private void OnCanvasDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        _canvasWidth = (float)sender.ActualWidth;
        _canvasHeight = (float)sender.ActualHeight;

        if (_canvasWidth <= 0 || _canvasHeight <= 0)
        {
            return;
        }

        ds.Clear(BackgroundColor);

        DrawInOutRegion(ds);
        DrawTimeRuler(ds);
        DrawInOutPoints(ds);
    }

    private void DrawTimeRuler(CanvasDrawingSession ds)
    {
        double secondsPerMajorTick = CalculateMajorTickInterval();
        double secondsPerMinorTick = secondsPerMajorTick / 5.0;

        double effectiveStartTime = Math.Max(0, _viewStartTime);
        double startTime = Math.Floor(effectiveStartTime / secondsPerMinorTick) * secondsPerMinorTick;
        float bottomY = _canvasHeight;

        using var textFormat = new CanvasTextFormat
        {
            FontSize = 9,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Bottom
        };

        for (double time = startTime; time <= _viewEndTime; time += secondsPerMinorTick)
        {
            if (time < 0)
            {
                continue;
            }

            float x = TimeToX(time);

            if (x < -10 || x > _canvasWidth + 10)
            {
                continue;
            }

            bool isMajor = Math.Abs(time % secondsPerMajorTick) < 0.001 ||
                           Math.Abs(time % secondsPerMajorTick - secondsPerMajorTick) < 0.001;

            float tickHeight = isMajor ? MajorTickHeight : MinorTickHeight;
            ds.DrawLine(x, bottomY - tickHeight, x, bottomY, RulerLineColor, TickWidth);

            if (isMajor && time >= 0)
            {
                string label = FormatTime(time);
                ds.DrawText(label, x, bottomY - tickHeight - 2, RulerTextColor, textFormat);
            }
        }
    }

    private void DrawInOutRegion(CanvasDrawingSession ds)
    {
        if (!HasSelection)
        {
            return;
        }

        float inX = TimeToX(_inPoint!.Value);
        float outX = TimeToX(_outPoint!.Value);

        if (outX < 0 || inX > _canvasWidth)
        {
            return;
        }

        float startX = Math.Max(0, Math.Min(inX, outX));
        float endX = Math.Min(_canvasWidth, Math.Max(inX, outX));

        float centerY = _canvasHeight / 2f;
        float halfHeight = RegionHeight / 2f;

        using var pathBuilder = new CanvasPathBuilder(ds);

        float pointSize = PointSize;
        float leftX = TimeToX(_inPoint!.Value);
        float rightX = TimeToX(_outPoint!.Value);

        leftX = Math.Max(-pointSize, leftX);
        rightX = Math.Min(_canvasWidth + pointSize, rightX);

        pathBuilder.BeginFigure(leftX - pointSize, centerY);
        pathBuilder.AddLine(leftX, centerY - halfHeight);
        pathBuilder.AddLine(rightX, centerY - halfHeight);
        pathBuilder.AddLine(rightX + pointSize, centerY);
        pathBuilder.AddLine(rightX, centerY + halfHeight);
        pathBuilder.AddLine(leftX, centerY + halfHeight);
        pathBuilder.EndFigure(CanvasFigureLoop.Closed);

        using var geometry = CanvasGeometry.CreatePath(pathBuilder);
        ds.FillGeometry(geometry, InOutRegionColor);
        ds.DrawGeometry(geometry, InOutPointColor, 1.5f);
    }

    private void DrawInOutPoints(CanvasDrawingSession ds)
    {
        float centerY = _canvasHeight / 2f;

        if (_inPoint.HasValue)
        {
            float x = TimeToX(_inPoint.Value);
            if (x >= -PointSize && x <= _canvasWidth + PointSize)
            {
                DrawPointMarker(ds, x, centerY, true);
            }
        }

        if (_outPoint.HasValue)
        {
            float x = TimeToX(_outPoint.Value);
            if (x >= -PointSize && x <= _canvasWidth + PointSize)
            {
                DrawPointMarker(ds, x, centerY, false);
            }
        }
    }

    private void DrawPointMarker(CanvasDrawingSession ds, float x, float centerY, bool isInPoint)
    {
        using var pathBuilder = new CanvasPathBuilder(ds);

        float size = PointSize;
        if (isInPoint)
        {
            pathBuilder.BeginFigure(x - size, centerY - size);
            pathBuilder.AddLine(x + size / 2, centerY);
            pathBuilder.AddLine(x - size, centerY + size);
        }
        else
        {
            pathBuilder.BeginFigure(x + size, centerY - size);
            pathBuilder.AddLine(x - size / 2, centerY);
            pathBuilder.AddLine(x + size, centerY + size);
        }
        pathBuilder.EndFigure(CanvasFigureLoop.Closed);

        using var geometry = CanvasGeometry.CreatePath(pathBuilder);
        ds.FillGeometry(geometry, InOutPointColor);
        ds.DrawGeometry(geometry, StrokeColor, 1f);
    }

    private float TimeToX(double timeSeconds)
    {
        return (float)((timeSeconds - _viewStartTime) * _pixelsPerSecond);
    }

    private double CalculateMajorTickInterval()
    {
        double targetPixelsPerTick = 80.0;
        double secondsPerTick = targetPixelsPerTick / _pixelsPerSecond;

        double[] intervals = { 0.1, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600 };

        foreach (var interval in intervals)
        {
            if (interval >= secondsPerTick)
            {
                return interval;
            }
        }

        return 600;
    }

    private static string FormatTime(double seconds)
    {
        int totalMs = (int)(seconds * 1000);
        int min = totalMs / 60000;
        int sec = (totalMs % 60000) / 1000;
        int ms = totalMs % 1000;
        return $"{min:D2}:{sec:D2}:{ms:D3}";
    }
}