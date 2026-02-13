using FluentDesigner.ECS.System;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using Windows.UI;

namespace FluentDesigner.Control;

public sealed partial class WaveformTrack : UserControl
{
    private WaveformService _waveformService;
    private bool _isInitialized;
    private float _canvasWidth;
    private float _canvasHeight;

    private double _viewStartTime;
    private double _viewEndTime;
    private double _pixelsPerSecond = 100.0;

    private static readonly Color BackgroundColor = Color.FromArgb(255, 30, 30, 30);
    private static readonly Color WaveformStrokeColor = Color.FromArgb(255, 86, 156, 214);
    private static readonly Color WaveformFillColor = Color.FromArgb(100, 86, 156, 214);
    private static readonly Color CenterLineColor = Color.FromArgb(80, 128, 128, 128);

    private DispatcherTimer _redrawDebounceTimer;
    private DispatcherTimer _resizeDebounceTimer;

    private double _lastDrawnStartTime = -1;
    private double _lastDrawnEndTime = -1;
    private double _lastDrawnPixelsPerSecond = -1;
    private float _lastDrawnWidth = -1;
    private float _lastDrawnHeight = -1;

    public Action<float, float> SizeChangedCallback { get; set; }

    public WaveformTrack()
    {
        InitializeComponent();

        _redrawDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(4)
        };
        _redrawDebounceTimer.Tick += OnRedrawDebounce;

        _resizeDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _resizeDebounceTimer.Tick += OnResizeDebounce;
    }

    public void Initialize(WaveformService waveformService)
    {
        _waveformService = waveformService;

        if (_waveformService != null)
        {
            _waveformService.WaveformLoaded += OnWaveformLoaded;
            _waveformService.WaveformCleared += OnWaveformCleared;
            _waveformService.ProgressChanged += OnProgressChanged;
            _waveformService.SegmentGenerated += OnSegmentGenerated;

            UpdateVisibility();
        }

        _isInitialized = true;
    }

    private void OnWaveformLoaded(object sender, WaveformCache cache)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateVisibility();
            WaveformCanvas.Invalidate();
        });
    }

    private void OnWaveformCleared(object sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateVisibility();
            WaveformCanvas.Invalidate();
        });
    }

    private void OnProgressChanged(object sender, float progress)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            LoadingText.Text = $"Loading waveform... {progress * 100:F0}%";
        });
    }

    private void OnSegmentGenerated(object sender, (WaveformCacheLevel level, int startIndex, PeakSample[] peaks) e)
    {
        DispatcherQueue.TryEnqueue(RequestRedraw);
    }

    private void UpdateVisibility()
    {
        bool hasData = _waveformService?.Cache?.IsLoaded == true;
        bool isLoading = _waveformService?.IsLoading == true;

        NoDataText.Visibility = (!hasData && !isLoading) ? Visibility.Visible : Visibility.Collapsed;
        LoadingIndicator.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
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
        WaveformCanvas.Invalidate();
    }

    private void RequestRedraw()
    {
        _redrawDebounceTimer.Stop();
        _redrawDebounceTimer.Start();
    }

    private void OnRedrawDebounce(object sender, object e)
    {
        _redrawDebounceTimer.Stop();
        WaveformCanvas.Invalidate();
    }

    private void OnResizeDebounce(object sender, object e)
    {
        _resizeDebounceTimer.Stop();
        InvalidateDrawCache();
        WaveformCanvas.Invalidate();
        SizeChangedCallback?.Invoke(_canvasWidth, _canvasHeight);
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
        _resizeDebounceTimer.Stop();

        if (_waveformService != null)
        {
            _waveformService.WaveformLoaded -= OnWaveformLoaded;
            _waveformService.WaveformCleared -= OnWaveformCleared;
            _waveformService.ProgressChanged -= OnProgressChanged;
            _waveformService.SegmentGenerated -= OnSegmentGenerated;
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

        float centerY = _canvasHeight / 2f;
        ds.DrawLine(0, centerY, _canvasWidth, centerY, CenterLineColor, 1f);

        var cache = _waveformService?.Cache;
        if (cache == null || !cache.IsLoaded)
        {
            return;
        }
        bool needsFullRedraw =
            Math.Abs(_lastDrawnStartTime - _viewStartTime) > 0.001 ||
            Math.Abs(_lastDrawnEndTime - _viewEndTime) > 0.001 ||
            Math.Abs(_lastDrawnPixelsPerSecond - _pixelsPerSecond) > 0.1 ||
            Math.Abs(_lastDrawnWidth - _canvasWidth) > 1 ||
            Math.Abs(_lastDrawnHeight - _canvasHeight) > 1;

        if (needsFullRedraw)
        {
            DrawWaveform(ds, cache, centerY);
            _lastDrawnStartTime = _viewStartTime;
            _lastDrawnEndTime = _viewEndTime;
            _lastDrawnPixelsPerSecond = _pixelsPerSecond;
            _lastDrawnWidth = _canvasWidth;
            _lastDrawnHeight = _canvasHeight;
        }
        else
        {
            DrawWaveform(ds, cache, centerY);
        }
    }

    private void DrawWaveform(CanvasDrawingSession ds, WaveformCache cache, float centerY)
    {
        double effectiveStartTime = Math.Max(0, _viewStartTime);
        double effectiveEndTime = _viewEndTime;

        if (effectiveEndTime <= 0)
        {
            return;
        }

        var level = WaveformCache.GetOptimalLevel(_pixelsPerSecond, cache.SampleRate);
        var peaks = cache.GetPeaksInRange(
            level,
            TimeSpan.FromSeconds(effectiveStartTime),
            TimeSpan.FromSeconds(effectiveEndTime));

        if (peaks.IsEmpty)
        {
            return;
        }

        int samplesPerPeak = WaveformCache.GetSamplesPerPeak(level);
        double peaksPerSecond = (double)cache.SampleRate / samplesPerPeak;
        double pixelsPerPeak = _pixelsPerSecond / peaksPerSecond;

        double startPeakIndex = effectiveStartTime * peaksPerSecond;
        double fractionalOffset = (startPeakIndex - Math.Floor(startPeakIndex)) * pixelsPerPeak;

        float virtualPaddingOffset = 0;
        if (_viewStartTime < 0)
        {
            virtualPaddingOffset = (float)(-_viewStartTime * _pixelsPerSecond);
        }

        float halfHeight = (_canvasHeight / 2f) - 2f;

        int firstVisibleIndex = Math.Max(0, (int)(fractionalOffset / pixelsPerPeak) - 1);
        int lastVisibleIndex = Math.Min(peaks.Length - 1, (int)((_canvasWidth - virtualPaddingOffset + fractionalOffset) / pixelsPerPeak) + 1);

        int visibleCount = lastVisibleIndex - firstVisibleIndex + 1;
        if (visibleCount <= 0)
        {
            return;
        }

        using var pathBuilder = new CanvasPathBuilder(ds);

        bool pathStarted = false;
        float firstX = 0;

        for (int i = firstVisibleIndex; i <= lastVisibleIndex; i++)
        {
            float x = (float)(i * pixelsPerPeak - fractionalOffset) + virtualPaddingOffset;

            if (x < -10)
            {
                continue;
            }

            var peak = peaks[i];
            float topY = centerY - peak.Max * halfHeight;

            if (Math.Abs(peak.Max - peak.Min) < 0.01f)
            {
                topY = centerY - 0.5f;
            }

            if (!pathStarted)
            {
                firstX = x;
                pathBuilder.BeginFigure(x, centerY);
                pathStarted = true;
            }

            pathBuilder.AddLine(x, topY);
        }

        if (!pathStarted)
        {
            return;
        }

        for (int i = lastVisibleIndex; i >= firstVisibleIndex; i--)
        {
            float x = (float)(i * pixelsPerPeak - fractionalOffset) + virtualPaddingOffset;

            if (x < -10)
            {
                continue;
            }

            var peak = peaks[i];
            float bottomY = centerY - peak.Min * halfHeight;
            if (Math.Abs(peak.Max - peak.Min) < 0.01f)
            {
                bottomY = centerY + 0.5f;
            }

            pathBuilder.AddLine(x, bottomY);
        }

        pathBuilder.EndFigure(CanvasFigureLoop.Closed);

        using var geometry = CanvasGeometry.CreatePath(pathBuilder);
        ds.FillGeometry(geometry, WaveformFillColor);
        ds.DrawGeometry(geometry, WaveformStrokeColor, 1f);
        cache.ClearAllDirtyFlags();
    }
}