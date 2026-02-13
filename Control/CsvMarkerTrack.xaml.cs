using FluentDesigner.ECS.System;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.Numerics;
using Windows.UI;

namespace FluentDesigner.Control;

public sealed partial class CsvMarkerTrack : UserControl
{
    private CsvMarkerService _csvMarkerService;
    private bool _isInitialized;
    private float _canvasWidth;
    private float _canvasHeight;

    private double _viewStartTime;
    private double _viewEndTime;
    private double _pixelsPerSecond = 100.0;

    private static readonly Color BackgroundColor = Color.FromArgb(255, 35, 35, 35);
    private static readonly Color CueColor = Color.FromArgb(255, 255, 255, 255);
    private static readonly Color CartColor = Color.FromArgb(255, 144, 238, 144);
    private static readonly Color TrackColor = Color.FromArgb(255, 130, 180, 255);
    private static readonly Color SubclipColor = Color.FromArgb(255, 210, 180, 140);
    private static readonly Color StrokeColor = Color.FromArgb(255, 0, 0, 0);
    private static readonly Color DurationLineColor = Color.FromArgb(180, 128, 128, 128);

    private const float DiamondSize = 6f;
    private const float StrokeWidth = 1.5f;
    private const float DurationLineWidth = 2f;
    private const float ClickTolerance = 10f;

    private DispatcherTimer _redrawDebounceTimer;

    private double _lastDrawnStartTime = -1;
    private double _lastDrawnEndTime = -1;
    private double _lastDrawnPixelsPerSecond = -1;
    private float _lastDrawnWidth = -1;
    private float _lastDrawnHeight = -1;

    public Action<float, float> SizeChangedCallback { get; set; }
    public event EventHandler<float> MarkerClicked;

    public CsvMarkerTrack()
    {
        InitializeComponent();

        _redrawDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(8)
        };
        _redrawDebounceTimer.Tick += OnRedrawDebounce;

        Unloaded += OnUnloaded;
    }

    public void Initialize(CsvMarkerService csvMarkerService)
    {
        _csvMarkerService = csvMarkerService;

        if (_csvMarkerService != null)
        {
            _csvMarkerService.MarkersLoaded += OnMarkersLoaded;
            _csvMarkerService.MarkersCleared += OnMarkersCleared;

            UpdateVisibility();
        }

        _isInitialized = true;
    }

    private void OnMarkersLoaded(object sender, IReadOnlyList<CsvMarker> markers)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateVisibility();
            MarkerCanvas.Invalidate();
        });
    }

    private void OnMarkersCleared(object sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateVisibility();
            MarkerCanvas.Invalidate();
        });
    }

    private void UpdateVisibility()
    {
        bool hasData = _csvMarkerService?.IsLoaded == true && _csvMarkerService.MarkerCount > 0;

        NoDataText.Visibility = hasData ? Visibility.Collapsed : Visibility.Visible;
        LoadingIndicator.Visibility = Visibility.Collapsed;
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

    public void InvalidateCanvas()
    {
        InvalidateDrawCache();
        RequestRedraw();
    }

    private void ForceRedraw()
    {
        _redrawDebounceTimer.Stop();
        InvalidateDrawCache();
        MarkerCanvas.Invalidate();
    }

    private void RequestRedraw()
    {
        _redrawDebounceTimer.Stop();
        _redrawDebounceTimer.Start();
    }

    private void OnRedrawDebounce(object sender, object e)
    {
        _redrawDebounceTimer.Stop();
        MarkerCanvas.Invalidate();
    }

    private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _canvasWidth = (float)e.NewSize.Width;
        _canvasHeight = (float)e.NewSize.Height;
        RequestRedraw();
    }

    private void InvalidateDrawCache()
    {
        _lastDrawnStartTime = -1;
        _lastDrawnEndTime = -1;
        _lastDrawnPixelsPerSecond = -1;
        _lastDrawnWidth = -1;
        _lastDrawnHeight = -1;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _redrawDebounceTimer.Stop();

        if (_csvMarkerService != null)
        {
            _csvMarkerService.MarkersLoaded -= OnMarkersLoaded;
            _csvMarkerService.MarkersCleared -= OnMarkersCleared;
        }
    }

    public void UpdateCanvasSize(float width, float height)
    {
        if (Math.Abs(_canvasWidth - width) < 1 && Math.Abs(_canvasHeight - height) < 1)
        {
            return;
        }

        _canvasWidth = width;
        _canvasHeight = height;
        ForceRedraw();
    }

    private void OnCanvasPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_csvMarkerService == null || !_csvMarkerService.IsLoaded)
        {
            return;
        }

        var point = e.GetCurrentPoint(MarkerCanvas);

        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        float clickX = (float)point.Position.X;
        float clickY = (float)point.Position.Y;
        float centerY = _canvasHeight / 2f;

        float clickTime = XToTime(clickX);
        if (clickTime < 0)
        {
            return;
        }

        if (Math.Abs(clickY - centerY) > DiamondSize + ClickTolerance)
        {
            return;
        }

        CsvMarker clickedMarker = null;
        float clickedTime = -1f;
        float minDistance = float.MaxValue;

        float effectiveStartTime = Math.Max(0, (float)_viewStartTime);
        var markers = _csvMarkerService.GetMarkersInRange(effectiveStartTime, (float)_viewEndTime);

        foreach (var marker in markers)
        {
            float startX = TimeToX(marker.StartTimeSeconds);
            float startDistance = Math.Abs(clickX - startX);

            if (startDistance < minDistance && startDistance <= ClickTolerance + DiamondSize)
            {
                minDistance = startDistance;
                clickedMarker = marker;
                clickedTime = marker.StartTimeSeconds;
            }

            if (marker.HasDuration)
            {
                float endX = TimeToX(marker.EndTimeSeconds);
                float endDistance = Math.Abs(clickX - endX);

                if (endDistance < minDistance && endDistance <= ClickTolerance + DiamondSize)
                {
                    minDistance = endDistance;
                    clickedMarker = marker;
                    clickedTime = marker.EndTimeSeconds;
                }
            }
        }

        if (clickedMarker != null && clickedTime >= 0)
        {
            MarkerClicked?.Invoke(this, clickedTime);
            e.Handled = true;
        }
    }

    private float XToTime(float x)
    {
        float time = (float)(_viewStartTime + x / _pixelsPerSecond);
        return time < 0 ? -1 : time;
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

        if (_csvMarkerService == null || !_csvMarkerService.IsLoaded || _csvMarkerService.MarkerCount == 0)
        {
            return;
        }

        bool needsRedraw =
            Math.Abs(_lastDrawnStartTime - _viewStartTime) > 0.001 ||
            Math.Abs(_lastDrawnEndTime - _viewEndTime) > 0.001 ||
            Math.Abs(_lastDrawnPixelsPerSecond - _pixelsPerSecond) > 0.1 ||
            Math.Abs(_lastDrawnWidth - _canvasWidth) > 1 ||
            Math.Abs(_lastDrawnHeight - _canvasHeight) > 1;

        if (needsRedraw)
        {
            DrawMarkers(ds);
            _lastDrawnStartTime = _viewStartTime;
            _lastDrawnEndTime = _viewEndTime;
            _lastDrawnPixelsPerSecond = _pixelsPerSecond;
            _lastDrawnWidth = _canvasWidth;
            _lastDrawnHeight = _canvasHeight;
        }
        else
        {
            DrawMarkers(ds);
        }
    }

    private void DrawMarkers(CanvasDrawingSession ds)
    {
        float effectiveStartTime = Math.Max(0, (float)_viewStartTime);
        float effectiveEndTime = (float)_viewEndTime;

        if (effectiveEndTime <= 0)
        {
            return;
        }

        var markers = _csvMarkerService.GetMarkersInRange(effectiveStartTime, effectiveEndTime);
        float centerY = _canvasHeight / 2f;

        foreach (var marker in markers)
        {
            if (marker.HasDuration)
            {
                DrawDurationLine(ds, marker, centerY);
            }
        }

        foreach (var marker in markers)
        {
            DrawMarkerDiamond(ds, marker, centerY, isEndMarker: false);
            if (marker.HasDuration)
            {
                DrawMarkerDiamond(ds, marker, centerY, isEndMarker: true);
            }
        }
    }

    private void DrawDurationLine(CanvasDrawingSession ds, CsvMarker marker, float centerY)
    {
        float startX = TimeToX(marker.StartTimeSeconds);
        float endX = TimeToX(marker.EndTimeSeconds);
        if (endX < 0 || startX > _canvasWidth)
        {
            return;
        }

        startX = Math.Max(0, startX);
        endX = Math.Min(_canvasWidth, endX);

        ds.DrawLine(startX, centerY, endX, centerY, DurationLineColor, DurationLineWidth);
    }

    private void DrawMarkerDiamond(CanvasDrawingSession ds, CsvMarker marker, float centerY, bool isEndMarker)
    {
        float timeSeconds = isEndMarker ? marker.EndTimeSeconds : marker.StartTimeSeconds;
        float x = TimeToX(timeSeconds);

        if (x < -DiamondSize || x > _canvasWidth + DiamondSize)
        {
            return;
        }

        Color fillColor = GetMarkerColor(marker.Type);
        using var pathBuilder = new CanvasPathBuilder(ds);
        pathBuilder.BeginFigure(x, centerY - DiamondSize);
        pathBuilder.AddLine(x + DiamondSize, centerY);
        pathBuilder.AddLine(x, centerY + DiamondSize);
        pathBuilder.AddLine(x - DiamondSize, centerY);
        pathBuilder.EndFigure(CanvasFigureLoop.Closed);

        using var geometry = CanvasGeometry.CreatePath(pathBuilder);
        ds.FillGeometry(geometry, fillColor);
        ds.DrawGeometry(geometry, StrokeColor, StrokeWidth);
    }

    private float TimeToX(float timeSeconds)
    {
        return (float)((timeSeconds - _viewStartTime) * _pixelsPerSecond);
    }

    private static Color GetMarkerColor(CsvMarkerType type)
    {
        return type switch
        {
            CsvMarkerType.Cue => CueColor,
            CsvMarkerType.Cart => CartColor,
            CsvMarkerType.Track => TrackColor,
            CsvMarkerType.Subclip => SubclipColor,
            _ => CueColor
        };
    }
}