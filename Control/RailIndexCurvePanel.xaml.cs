using FluentDesigner.ECS.Components;
using FluentDesigner.ECS.System;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using Windows.UI;

namespace FluentDesigner.Control;

public sealed partial class RailIndexCurvePanel : UserControl
{
    private MultiSelectController _controller;
    private List<NoteRailData> _noteData = new();
    private List<RailKeyFrame> _keyFrames = new();
    private bool _isSamplingMode = true;
    private RailInterpolationType _interpolationType = RailInterpolationType.Linear;
    private bool _isInitialized;

    private float _canvasWidth;
    private float _canvasHeight;
    private long _startTimeMs;
    private long _endTimeMs;

    private const float MinRailIndex = 0f;
    private const float MaxRailIndex = 359f;
    private const float BaselineRailIndex = 180f;
    private const int GridCells = 10;
    private const float HitRadius = 12f;
    private const float DiamondSize = 5f;
    private const float ControlPointSize = 4f;

    private enum DragTarget { None, KeyFrame, ControlPoint, NotePoint }
    private DragTarget _currentDragTarget = DragTarget.None;
    private int _dragKeyFrameIndex = -1;
    private int _dragControlPointIndex = -1;
    private bool _isDragging;

    private DispatcherTimer _resizeDebounceTimer;

    public ObservableCollection<RailKeyFrameViewModel> KeyFrameItems { get; } = new();

    public event EventHandler RailIndexChanged;

    public RailIndexCurvePanel()
    {
        InitializeComponent();

        _resizeDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _resizeDebounceTimer.Tick += OnResizeDebounceTimerTick;

        KeyFrameItemsControl.ItemsSource = KeyFrameItems;

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isInitialized = true;
        SamplingModeRadio.IsChecked = true;
        UpdateModeVisibility();
    }

    public void BindController(MultiSelectController controller)
    {
        _controller = controller;
        RefreshData();
    }

    public void RefreshData()
    {
        if (_controller == null || !_isInitialized)
        {
            return;
        }

        List<NoteRailData> selectedNotes;
        try
        {
            selectedNotes = _controller.GetSelectedNotesWithRailIndex();
        }
        catch
        {
            SetPanelEnabled(false);
            return;
        }

        if (selectedNotes == null || selectedNotes.Count == 0)
        {
            RotateWarningInfoBar.IsOpen = false;
            SetPanelEnabled(false);
            return;
        }

        var rotateNotes = selectedNotes.Where(n => n.IsRotateType).ToList();
        if (rotateNotes.Count > 0)
        {
            RotateWarningInfoBar.IsOpen = true;
            RotateNotesList.ItemsSource = rotateNotes.Select(n => n.Name).ToList();
        }
        else
        {
            RotateWarningInfoBar.IsOpen = false;
        }

        _noteData = selectedNotes
            .Where(n => !n.IsRotateType)
            .OrderBy(n => n.TimeMs)
            .ToList();

        if (_noteData.Count < 2)
        {
            SetPanelEnabled(false);
            return;
        }
        SetPanelEnabled(true);

        _startTimeMs = _noteData.First().TimeMs;
        _endTimeMs = _noteData.Last().TimeMs;

        if (_keyFrames.Count == 0 || NeedsKeyFrameReset())
        {
            InitializeDefaultKeyFrames();
        }

        RefreshKeyFrameList();
        RequestRedraw();
    }

    private bool NeedsKeyFrameReset()
    {
        if (_keyFrames.Count < 2) return true;

        var firstKf = _keyFrames.OrderBy(k => k.TimeMs).First();
        var lastKf = _keyFrames.OrderBy(k => k.TimeMs).Last();

        return Math.Abs(firstKf.TimeMs - _startTimeMs) > 1000 ||
               Math.Abs(lastKf.TimeMs - _endTimeMs) > 1000;
    }

    private void InitializeDefaultKeyFrames()
    {
        _keyFrames.Clear();

        if (_noteData.Count >= 2)
        {
            _keyFrames.Add(new RailKeyFrame
            {
                TimeMs = _startTimeMs,
                RailIndex = _noteData.First().CurrentRailIndex,
                ControlPoints = new List<RailControlPoint>()
            });
            _keyFrames.Add(new RailKeyFrame
            {
                TimeMs = _endTimeMs,
                RailIndex = _noteData.Last().CurrentRailIndex,
                ControlPoints = new List<RailControlPoint>()
            });
        }
    }

    private void SetPanelEnabled(bool enabled)
    {
        if (!_isInitialized) return;

        CanvasBorder.Opacity = enabled ? 1.0 : 0.5;
        SamplingModeRadio.IsEnabled = enabled;
        ManualModeRadio.IsEnabled = enabled;
        AddKeyFrameButton.IsEnabled = enabled;
        InterpolationComboBox.IsEnabled = enabled;
        ApplyButton.IsEnabled = enabled;
        KeyFrameExpander.IsEnabled = enabled;
    }

    private void RefreshKeyFrameList()
    {
        KeyFrameItems.Clear();
        var sorted = _keyFrames.OrderBy(k => k.TimeMs).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            var kf = sorted[i];
            var vm = new RailKeyFrameViewModel
            {
                Index = i,
                TimeMs = kf.TimeMs,
                RailIndex = (int)kf.RailIndex,
                IsLastFrame = (i == sorted.Count - 1),
                InterpolationType = _interpolationType
            };

            if (kf.ControlPoints != null)
            {
                for (int j = 0; j < kf.ControlPoints.Count; j++)
                {
                    var cp = kf.ControlPoints[j];
                    vm.ControlPoints.Add(new RailControlPointViewModel
                    {
                        Index = j,
                        TimeMs = cp.TimeMs,
                        RailIndex = (int)cp.RailIndex,
                        ParentKeyFrameIndex = i
                    });
                }
            }

            KeyFrameItems.Add(vm);
        }
    }

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;

        _isSamplingMode = SamplingModeRadio?.IsChecked == true;
        UpdateModeVisibility();
        RequestRedraw();
    }

    private void UpdateModeVisibility()
    {
        if (!_isInitialized) return;

        if (SamplingModeControls != null)
            SamplingModeControls.Visibility = _isSamplingMode ? Visibility.Visible : Visibility.Collapsed;

        if (ManualModeHint != null)
            ManualModeHint.Visibility = _isSamplingMode ? Visibility.Collapsed : Visibility.Visible;

        if (KeyFrameExpander != null)
            KeyFrameExpander.Visibility = _isSamplingMode ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnControlPointValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_isInitialized || double.IsNaN(args.NewValue)) return;

        var sorted = _keyFrames.OrderBy(k => k.TimeMs).ToList();
        for (int i = 0; i < KeyFrameItems.Count && i < sorted.Count; i++)
        {
            var kfVm = KeyFrameItems[i];
            var kf = sorted[i];

            if (kf.ControlPoints == null) continue;

            for (int j = 0; j < kfVm.ControlPoints.Count && j < kf.ControlPoints.Count; j++)
            {
                kf.ControlPoints[j].RailIndex = kfVm.ControlPoints[j].RailIndex;
            }
        }

        RequestRedraw();
    }

    private void OnInterpolationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized || InterpolationComboBox == null) return;

        if (InterpolationComboBox.SelectedItem is ComboBoxItem item)
        {
            _interpolationType = item.Tag?.ToString() switch
            {
                "Step" => RailInterpolationType.Step,
                "Bezier" => RailInterpolationType.Bezier,
                _ => RailInterpolationType.Linear
            };

            foreach (var kf in KeyFrameItems)
            {
                kf.InterpolationType = _interpolationType;
            }

            RequestRedraw();
        }
    }
    private void OnCanvasCreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args)
    {
        _canvasWidth = (float)sender.ActualWidth;
        _canvasHeight = (float)sender.ActualHeight;
    }

    private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _resizeDebounceTimer.Stop();
        _resizeDebounceTimer.Start();
    }

    private void OnResizeDebounceTimerTick(object sender, object e)
    {
        _resizeDebounceTimer.Stop();
        _canvasWidth = (float)CurveCanvas.ActualWidth;
        _canvasHeight = (float)CurveCanvas.ActualHeight;
        RequestRedraw();
    }

    private void RequestRedraw()
    {
        if (!_isInitialized) return;
        CurveCanvas?.Invalidate();
    }

    private void OnCanvasDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        float width = (float)sender.ActualWidth;
        float height = (float)sender.ActualHeight;

        if (width <= 0 || height <= 0) return;

        ds.Clear(Color.FromArgb(255, 30, 30, 30));
        DrawGrid(ds, width, height);
        DrawBaseline(ds, width, height);

        if (_isSamplingMode)
        {
            DrawSamplingCurve(ds, width, height);
            DrawControlPointLines(ds, width, height);
            DrawControlPoints(ds, width, height);
            DrawSamplingKeyFrames(ds, width, height);
            DrawNoteMarkers(ds, width, height);
        }
        else
        {
            DrawManualLines(ds, width, height);
            DrawManualNotePoints(ds, width, height);
        }
    }

    private void DrawGrid(CanvasDrawingSession ds, float width, float height)
    {
        var gridColor = Color.FromArgb(60, 128, 128, 128);
        float cellWidth = width / GridCells;
        float cellHeight = height / GridCells;

        for (int i = 0; i <= GridCells; i++)
        {
            float x = i * cellWidth;
            ds.DrawLine(x, 0, x, height, gridColor, 1f);
        }

        for (int i = 0; i <= GridCells; i++)
        {
            float y = i * cellHeight;
            ds.DrawLine(0, y, width, y, gridColor, 1f);
        }
    }

    private void DrawBaseline(CanvasDrawingSession ds, float width, float height)
    {
        float baselineY = RailIndexToScreenY(BaselineRailIndex, height);
        ds.DrawLine(0, baselineY, width, baselineY, Colors.Orange, 2f);
    }

    private void DrawSamplingCurve(CanvasDrawingSession ds, float width, float height)
    {
        if (_keyFrames.Count < 2) return;

        var sortedKeyFrames = _keyFrames.OrderBy(k => k.TimeMs).ToList();
        var pathBuilder = new CanvasPathBuilder(ds);
        bool started = false;

        long timeRange = _endTimeMs - _startTimeMs;
        if (timeRange <= 0) return;

        int steps = Math.Max(200, (int)width);
        for (int i = 0; i <= steps; i++)
        {
            float t = (float)i / steps;
            long timeMs = _startTimeMs + (long)(t * timeRange);
            float railIndex = InterpolateRailIndex(sortedKeyFrames, timeMs);

            float x = t * width;
            float y = RailIndexToScreenY(railIndex, height);

            if (!started)
            {
                pathBuilder.BeginFigure(x, y);
                started = true;
            }
            else
            {
                pathBuilder.AddLine(x, y);
            }
        }

        if (started)
        {
            pathBuilder.EndFigure(CanvasFigureLoop.Open);
            using var geometry = CanvasGeometry.CreatePath(pathBuilder);
            ds.DrawGeometry(geometry, Colors.Cyan, 2.5f);
        }
    }

    private void DrawControlPointLines(CanvasDrawingSession ds, float width, float height)
    {
        if (_interpolationType != RailInterpolationType.Bezier) return;

        var sorted = _keyFrames.OrderBy(k => k.TimeMs).ToList();
        var lineColor = Color.FromArgb(150, 128, 128, 128);

        for (int i = 0; i < sorted.Count - 1; i++)
        {
            var currentKf = sorted[i];
            var nextKf = sorted[i + 1];

            if (currentKf.ControlPoints == null || currentKf.ControlPoints.Count == 0) continue;

            var (kx, ky) = TimeRailToScreen(currentKf.TimeMs, currentKf.RailIndex, width, height);
            var (nextKx, nextKy) = TimeRailToScreen(nextKf.TimeMs, nextKf.RailIndex, width, height);

            if (currentKf.ControlPoints.Count >= 1)
            {
                var cp1 = currentKf.ControlPoints[0];
                var (cp1x, cp1y) = TimeRailToScreen(cp1.TimeMs, cp1.RailIndex, width, height);
                ds.DrawLine(kx, ky, cp1x, cp1y, lineColor, 1.5f);
            }

            if (currentKf.ControlPoints.Count >= 2)
            {
                var cp2 = currentKf.ControlPoints[1];
                var (cp2x, cp2y) = TimeRailToScreen(cp2.TimeMs, cp2.RailIndex, width, height);
                ds.DrawLine(nextKx, nextKy, cp2x, cp2y, lineColor, 1.5f);
            }
        }
    }

    private void DrawControlPoints(CanvasDrawingSession ds, float width, float height)
    {
        if (_interpolationType != RailInterpolationType.Bezier) return;

        foreach (var kf in _keyFrames)
        {
            if (kf.ControlPoints == null) continue;

            foreach (var cp in kf.ControlPoints)
            {
                var (x, y) = TimeRailToScreen(cp.TimeMs, cp.RailIndex, width, height);
                ds.FillCircle(x, y, ControlPointSize, Colors.LimeGreen);
                ds.DrawCircle(x, y, ControlPointSize, Colors.Black, 1.5f);
            }
        }
    }

    private void DrawSamplingKeyFrames(CanvasDrawingSession ds, float width, float height)
    {
        foreach (var kf in _keyFrames)
        {
            var (x, y) = TimeRailToScreen(kf.TimeMs, kf.RailIndex, width, height);
            DrawDiamond(ds, x, y, Colors.WhiteSmoke, DiamondSize);
        }
    }

    private void DrawNoteMarkers(CanvasDrawingSession ds, float width, float height)
    {
        foreach (var note in _noteData)
        {
            float x = TimeToScreenX(note.TimeMs, width);
            ds.DrawLine(x, 0, x, height, Color.FromArgb(40, 255, 255, 255), 1f);
        }
    }

    private void DrawManualLines(CanvasDrawingSession ds, float width, float height)
    {
        if (_noteData.Count < 2) return;

        var pathBuilder = new CanvasPathBuilder(ds);
        bool started = false;

        foreach (var note in _noteData)
        {
            var (x, y) = TimeRailToScreen(note.TimeMs, note.CurrentRailIndex, width, height);

            if (!started)
            {
                pathBuilder.BeginFigure(x, y);
                started = true;
            }
            else
            {
                pathBuilder.AddLine(x, y);
            }
        }

        if (started)
        {
            pathBuilder.EndFigure(CanvasFigureLoop.Open);
            using var geometry = CanvasGeometry.CreatePath(pathBuilder);
            ds.DrawGeometry(geometry, Colors.LimeGreen, 2.5f);
        }
    }

    private void DrawManualNotePoints(CanvasDrawingSession ds, float width, float height)
    {
        for (int i = 0; i < _noteData.Count; i++)
        {
            var note = _noteData[i];
            var (x, y) = TimeRailToScreen(note.TimeMs, note.CurrentRailIndex, width, height);

            bool isEndpoint = (i == 0 || i == _noteData.Count - 1);
            var color = isEndpoint ? Colors.Orange : Colors.LimeGreen;
            DrawDiamond(ds, x, y, color, DiamondSize);
        }
    }

    private void DrawDiamond(CanvasDrawingSession ds, float x, float y, Color color, float size)
    {
        var pathBuilder = new CanvasPathBuilder(ds);
        pathBuilder.BeginFigure(x, y - size);
        pathBuilder.AddLine(x + size, y);
        pathBuilder.AddLine(x, y + size);
        pathBuilder.AddLine(x - size, y);
        pathBuilder.EndFigure(CanvasFigureLoop.Closed);

        using var geometry = CanvasGeometry.CreatePath(pathBuilder);
        ds.FillGeometry(geometry, color);
        ds.DrawGeometry(geometry, Colors.Black, 0.5f);
    }
    private float RailIndexToScreenY(float railIndex, float height)
    {
        return height - (railIndex / MaxRailIndex) * height;
    }

    private float ScreenYToRailIndex(float screenY, float height)
    {
        return (1 - screenY / height) * MaxRailIndex;
    }

    private float TimeToScreenX(long timeMs, float width)
    {
        long timeRange = _endTimeMs - _startTimeMs;
        return timeRange > 0 ? ((float)(timeMs - _startTimeMs) / timeRange) * width : width / 2f;
    }

    private long ScreenXToTime(float screenX, float width)
    {
        long timeRange = _endTimeMs - _startTimeMs;
        return _startTimeMs + (long)((screenX / width) * timeRange);
    }

    private (float x, float y) TimeRailToScreen(long timeMs, float railIndex, float width, float height)
    {
        float x = TimeToScreenX(timeMs, width);
        float y = RailIndexToScreenY(railIndex, height);
        return (x, y);
    }
    private void OnCanvasPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(CurveCanvas);
        float px = (float)point.Position.X;
        float py = (float)point.Position.Y;
        float width = (float)CurveCanvas.ActualWidth;
        float height = (float)CurveCanvas.ActualHeight;

        if (_isSamplingMode)
        {
            if (_interpolationType == RailInterpolationType.Bezier)
            {
                for (int i = 0; i < _keyFrames.Count; i++)
                {
                    var kf = _keyFrames[i];
                    if (kf.ControlPoints == null) continue;

                    for (int j = 0; j < kf.ControlPoints.Count; j++)
                    {
                        var cp = kf.ControlPoints[j];
                        var (cpx, cpy) = TimeRailToScreen(cp.TimeMs, cp.RailIndex, width, height);
                        if (Distance(px, py, cpx, cpy) <= HitRadius)
                        {
                            _isDragging = true;
                            _currentDragTarget = DragTarget.ControlPoint;
                            _dragKeyFrameIndex = i;
                            _dragControlPointIndex = j;
                            CurveCanvas.CapturePointer(e.Pointer);
                            e.Handled = true;
                            return;
                        }
                    }
                }
            }

            for (int i = 0; i < _keyFrames.Count; i++)
            {
                var (kx, ky) = TimeRailToScreen(_keyFrames[i].TimeMs, _keyFrames[i].RailIndex, width, height);
                if (Distance(px, py, kx, ky) <= HitRadius)
                {
                    _isDragging = true;
                    _currentDragTarget = DragTarget.KeyFrame;
                    _dragKeyFrameIndex = i;
                    _dragControlPointIndex = -1;
                    CurveCanvas.CapturePointer(e.Pointer);
                    e.Handled = true;
                    return;
                }
            }
        }
        else
        {
            for (int i = 0; i < _noteData.Count; i++)
            {
                var (nx, ny) = TimeRailToScreen(_noteData[i].TimeMs, _noteData[i].CurrentRailIndex, width, height);
                if (Distance(px, py, nx, ny) <= HitRadius)
                {
                    _isDragging = true;
                    _currentDragTarget = DragTarget.NotePoint;
                    _dragKeyFrameIndex = i;
                    _dragControlPointIndex = -1;
                    CurveCanvas.CapturePointer(e.Pointer);
                    e.Handled = true;
                    return;
                }
            }
        }
    }

    private void OnCanvasPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging || _dragKeyFrameIndex < 0) return;

        var point = e.GetCurrentPoint(CurveCanvas);
        float px = (float)point.Position.X;
        float py = (float)point.Position.Y;
        float width = (float)CurveCanvas.ActualWidth;
        float height = (float)CurveCanvas.ActualHeight;

        float newRailIndex = Math.Clamp(ScreenYToRailIndex(py, height), MinRailIndex, MaxRailIndex);
        newRailIndex = MathF.Round(newRailIndex);

        long newTime = ScreenXToTime(px, width);
        newTime = Math.Clamp(newTime, _startTimeMs, _endTimeMs);

        switch (_currentDragTarget)
        {
            case DragTarget.KeyFrame:
                _keyFrames[_dragKeyFrameIndex].TimeMs = newTime;
                _keyFrames[_dragKeyFrameIndex].RailIndex = newRailIndex;
                break;

            case DragTarget.ControlPoint:
                if (_dragControlPointIndex >= 0 &&
                    _keyFrames[_dragKeyFrameIndex].ControlPoints != null &&
                    _dragControlPointIndex < _keyFrames[_dragKeyFrameIndex].ControlPoints.Count)
                {
                    _keyFrames[_dragKeyFrameIndex].ControlPoints[_dragControlPointIndex].TimeMs = newTime;
                    _keyFrames[_dragKeyFrameIndex].ControlPoints[_dragControlPointIndex].RailIndex = newRailIndex;
                }
                break;

            case DragTarget.NotePoint:
                _noteData[_dragKeyFrameIndex].CurrentRailIndex = newRailIndex;
                break;
        }

        RequestRedraw();
        e.Handled = true;
    }

    private void OnCanvasPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_isDragging)
        {
            CurveCanvas.ReleasePointerCapture(e.Pointer);
            if (_isSamplingMode && (_currentDragTarget == DragTarget.KeyFrame || _currentDragTarget == DragTarget.ControlPoint))
            {
                _keyFrames = _keyFrames.OrderBy(k => k.TimeMs).ToList();
                RefreshKeyFrameList();
            }

            _isDragging = false;
            _currentDragTarget = DragTarget.None;
            _dragKeyFrameIndex = -1;
            _dragControlPointIndex = -1;

            e.Handled = true;
        }
    }

    private void OnCanvasPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_isDragging)
        {
            if (_isSamplingMode)
            {
                _keyFrames = _keyFrames.OrderBy(k => k.TimeMs).ToList();
                RefreshKeyFrameList();
            }
        }

        _isDragging = false;
        _currentDragTarget = DragTarget.None;
        _dragKeyFrameIndex = -1;
        _dragControlPointIndex = -1;
    }

    private static float Distance(float x1, float y1, float x2, float y2)
    {
        float dx = x1 - x2;
        float dy = y1 - y2;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private void OnAddKeyFrameClicked(object sender, RoutedEventArgs e)
    {
        if (_noteData.Count < 2) return;

        long midTime = (_startTimeMs + _endTimeMs) / 2;
        while (_keyFrames.Any(k => Math.Abs(k.TimeMs - midTime) < 50))
        {
            midTime += 100;
            if (midTime > _endTimeMs)
            {
                midTime = (_startTimeMs + _endTimeMs) / 2 - 100;
                break;
            }
        }

        _keyFrames.Add(new RailKeyFrame
        {
            TimeMs = Math.Clamp(midTime, _startTimeMs, _endTimeMs),
            RailIndex = BaselineRailIndex,
            ControlPoints = new List<RailControlPoint>()
        });

        _keyFrames = _keyFrames.OrderBy(k => k.TimeMs).ToList();
        RefreshKeyFrameList();
        RequestRedraw();
    }

    private void OnRemoveKeyFrameClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not int index) return;
        if (_keyFrames.Count <= 2) return;

        var sorted = _keyFrames.OrderBy(k => k.TimeMs).ToList();
        if (index >= 0 && index < sorted.Count)
        {
            _keyFrames.Remove(sorted[index]);
            RefreshKeyFrameList();
            RequestRedraw();
        }
    }

    private void OnKeyFrameValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_isInitialized || double.IsNaN(args.NewValue)) return;

        var sorted = _keyFrames.OrderBy(k => k.TimeMs).ToList();
        for (int i = 0; i < KeyFrameItems.Count && i < sorted.Count; i++)
        {
            sorted[i].RailIndex = KeyFrameItems[i].RailIndex;
        }

        RequestRedraw();
    }

    private void OnAddControlPointClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not int keyFrameIndex) return;

        var sorted = _keyFrames.OrderBy(k => k.TimeMs).ToList();
        if (keyFrameIndex < 0 || keyFrameIndex >= sorted.Count) return;

        var kf = sorted[keyFrameIndex];
        if (kf.ControlPoints == null)
        {
            kf.ControlPoints = new List<RailControlPoint>();
        }

        if (kf.ControlPoints.Count >= 2) return;

        long nextTime = keyFrameIndex + 1 < sorted.Count ? sorted[keyFrameIndex + 1].TimeMs : _endTimeMs;
        long cpTime = kf.TimeMs + (nextTime - kf.TimeMs) / 3 * (kf.ControlPoints.Count + 1);

        kf.ControlPoints.Add(new RailControlPoint
        {
            TimeMs = cpTime,
            RailIndex = kf.RailIndex
        });

        RefreshKeyFrameList();
        RequestRedraw();
    }

    private void OnRemoveControlPointClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;

        var cpVm = button.DataContext as RailControlPointViewModel;
        if (cpVm == null) return;

        var sorted = _keyFrames.OrderBy(k => k.TimeMs).ToList();
        if (cpVm.ParentKeyFrameIndex < 0 || cpVm.ParentKeyFrameIndex >= sorted.Count) return;

        var kf = sorted[cpVm.ParentKeyFrameIndex];
        if (kf.ControlPoints != null && cpVm.Index >= 0 && cpVm.Index < kf.ControlPoints.Count)
        {
            kf.ControlPoints.RemoveAt(cpVm.Index);
            RefreshKeyFrameList();
            RequestRedraw();
        }
    }


    private void OnApplyClicked(object sender, RoutedEventArgs e)
    {
        if (_noteData.Count == 0) return;

        if (_isSamplingMode)
        {
            var sortedKeyFrames = _keyFrames.OrderBy(k => k.TimeMs).ToList();
            foreach (var note in _noteData)
            {
                float railIndex = InterpolateRailIndex(sortedKeyFrames, note.TimeMs);
                note.CurrentRailIndex = MathF.Round(railIndex);
            }
        }

        _controller?.ApplyRailIndexChanges(_noteData);
        RailIndexChanged?.Invoke(this, EventArgs.Empty);
    }

    private float InterpolateRailIndex(List<RailKeyFrame> sortedKeyFrames, long timeMs)
    {
        if (sortedKeyFrames.Count == 0) return BaselineRailIndex;
        if (sortedKeyFrames.Count == 1) return sortedKeyFrames[0].RailIndex;

        if (timeMs <= sortedKeyFrames[0].TimeMs) return sortedKeyFrames[0].RailIndex;
        if (timeMs >= sortedKeyFrames[^1].TimeMs) return sortedKeyFrames[^1].RailIndex;

        for (int i = 0; i < sortedKeyFrames.Count - 1; i++)
        {
            var current = sortedKeyFrames[i];
            var next = sortedKeyFrames[i + 1];

            if (timeMs >= current.TimeMs && timeMs <= next.TimeMs)
            {
                float t = (float)(timeMs - current.TimeMs) / (next.TimeMs - current.TimeMs);

                return _interpolationType switch
                {
                    RailInterpolationType.Step => current.RailIndex,
                    RailInterpolationType.Bezier => BezierInterpolate(current, next, t),
                    _ => Lerp(current.RailIndex, next.RailIndex, t)
                };
            }
        }

        return BaselineRailIndex;
    }

    private float BezierInterpolate(RailKeyFrame current, RailKeyFrame next, float t)
    {
        var points = new List<Vector2>
        {
            new Vector2(current.TimeMs, current.RailIndex)
        };

        if (current.ControlPoints != null)
        {
            foreach (var cp in current.ControlPoints)
            {
                points.Add(new Vector2(cp.TimeMs, cp.RailIndex));
            }
        }

        points.Add(new Vector2(next.TimeMs, next.RailIndex));

        if (points.Count == 2)
        {
            return Lerp(current.RailIndex, next.RailIndex, t);
        }
        return DeCasteljau(points, t).Y;
    }

    private Vector2 DeCasteljau(List<Vector2> points, float t)
    {
        if (points.Count == 1)
        {
            return points[0];
        }

        var newPoints = new List<Vector2>();
        for (int i = 0; i < points.Count - 1; i++)
        {
            float x = Lerp(points[i].X, points[i + 1].X, t);
            float y = Lerp(points[i].Y, points[i + 1].Y, t);
            newPoints.Add(new Vector2(x, y));
        }

        return DeCasteljau(newPoints, t);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}

public class RailKeyFrame
{
    public long TimeMs { get; set; }
    public float RailIndex { get; set; }
    public List<RailControlPoint> ControlPoints { get; set; } = new();
}

public class RailControlPoint
{
    public long TimeMs { get; set; }
    public float RailIndex { get; set; }
}

public class RailKeyFrameViewModel : INotifyPropertyChanged
{
    private int _index;
    private long _timeMs;
    private int _railIndex;
    private bool _isLastFrame;
    private RailInterpolationType _interpolationType;
    private bool _isExpanded;

    public int Index
    {
        get => _index;
        set { _index = value; OnPropertyChanged(); }
    }

    public long TimeMs
    {
        get => _timeMs;
        set { _timeMs = value; OnPropertyChanged(); OnPropertyChanged(nameof(TimeDisplay)); }
    }

    public int RailIndex
    {
        get => _railIndex;
        set { _railIndex = value; OnPropertyChanged(); }
    }

    public bool IsLastFrame
    {
        get => _isLastFrame;
        set { _isLastFrame = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowControlPoints)); }
    }

    public RailInterpolationType InterpolationType
    {
        get => _interpolationType;
        set { _interpolationType = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowControlPoints)); OnPropertyChanged(nameof(CanAddControlPoint)); }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; OnPropertyChanged(); }
    }

    public bool ShowControlPoints => !IsLastFrame && InterpolationType == RailInterpolationType.Bezier
        ? Visibility.Visible == Visibility.Visible : false;
    public Visibility ShowControlPointsVisibility => ShowControlPoints ? Visibility.Visible : Visibility.Collapsed;
    public bool CanAddControlPoint => ControlPoints.Count < 2;
    public Visibility NoControlPointsVisibility => ControlPoints.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public ObservableCollection<RailControlPointViewModel> ControlPoints { get; } = new();

    public string TimeDisplay
    {
        get
        {
            int min = (int)(_timeMs / 60000);
            int sec = (int)((_timeMs % 60000) / 1000);
            int tick = (int)(_timeMs % 1000);
            return $"{min:D2}:{sec:D2}:{tick:D3}";
        }
    }

    public event PropertyChangedEventHandler PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class RailControlPointViewModel : INotifyPropertyChanged
{
    private int _index;
    private long _timeMs;
    private int _railIndex;
    private int _parentKeyFrameIndex;

    public int Index
    {
        get => _index;
        set { _index = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayIndex)); }
    }

    public string DisplayIndex => $"CP{_index + 1}";

    public long TimeMs
    {
        get => _timeMs;
        set { _timeMs = value; OnPropertyChanged(); OnPropertyChanged(nameof(TimeDisplay)); }
    }

    public int RailIndex
    {
        get => _railIndex;
        set { _railIndex = value; OnPropertyChanged(); }
    }

    public int ParentKeyFrameIndex
    {
        get => _parentKeyFrameIndex;
        set { _parentKeyFrameIndex = value; OnPropertyChanged(); }
    }

    public string TimeDisplay
    {
        get
        {
            int min = (int)(_timeMs / 60000);
            int sec = (int)((_timeMs % 60000) / 1000);
            int tick = (int)(_timeMs % 1000);
            return $"{min:D2}:{sec:D2}:{tick:D3}";
        }
    }

    public event PropertyChangedEventHandler PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public enum RailInterpolationType
{
    Linear,
    Step,
    Bezier
}