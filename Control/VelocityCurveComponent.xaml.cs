using FluentDesigner.ECS.Components;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Windows.UI;

namespace FluentDesigner.Control
{
    public sealed partial class VelocityCurveComponent : UserControl, INotifyPropertyChanged
    {
        private VelocityCurve _velocityCurve;
        private Timing _timing;
        private int _entityId = -1;
        private bool _isUpdating;
        private float _canvasWidth;
        private float _canvasHeight;
        private bool _needsRedraw = true;
        private DispatcherTimer _resizeDebounceTimer;
        private float _canvasStartTime = 0f;
        private float _canvasEndTime = 1f;

        private const float MinValue = -12f;
        private const float MaxValue = 12f;
        private const float Precision = 0.001f;
        private const int GridCells = 10;

        private enum DragTarget { None, KeyFrame, ControlPoint }
        private DragTarget _currentDragTarget = DragTarget.None;
        private int _dragKeyFrameIndex = -1;
        private int _dragControlPointIndex = -1;
        private bool _isDragging;
        private const float HitRadius = 10f;

        public ObservableCollection<KeyFrameItemViewModel> KeyFrameItems { get; } = new();

        public Visibility NoKeyFramesVisibility =>
            KeyFrameItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        public bool InsufficientKeyFramesVisibility =>
            KeyFrameItems.Count > 0 && KeyFrameItems.Count < 2;

        public event EventHandler<VelocityCurve> CurveChanged;

        public VelocityCurveComponent()
        {
            InitializeComponent();
            _resizeDebounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _resizeDebounceTimer.Tick += OnResizeDebounceTimerTick;

            KeyFrameItems.CollectionChanged += (s, e) => OnPropertyChanged(nameof(NoKeyFramesVisibility));
        }

        public void BindVelocityCurve(VelocityCurve velocityCurve, Timing timing, int entityId)
        {
            _velocityCurve = velocityCurve;
            _timing = timing;
            _entityId = entityId;
            _isUpdating = true;

            UpdateCanvasRange();
            RefreshKeyFrameList();

            _isUpdating = false;
            RequestRedraw();
        }

        public void RefreshDisplay()
        {
            if (_velocityCurve == null)
            {
                return;
            }

            _isUpdating = true;

            UpdateCanvasRange();
            RefreshKeyFrameList();

            _isUpdating = false;
            RequestRedraw();
        }

        private (float startTime, float endTime) GetCurveTimeRange() => (_canvasStartTime, _canvasEndTime);

        private void RefreshKeyFrameList()
        {
            KeyFrameItems.Clear();
            if (_velocityCurve?.Curve.Frames == null)
            {
                return;
            }

            float targetTime = GetTargetTimeInSeconds();
            int totalFrames = _velocityCurve.Curve.Frames.Length;
            for (int i = 0; i < totalFrames; i++)
            {
                ref var frame = ref _velocityCurve.Curve.Frames[i];
                var viewModel = new KeyFrameItemViewModel
                {
                    Index = i,
                    Time = frame.Time,
                    Value = frame.Value,
                    InterpolationType = frame.InterpolationType,
                    IsLastFrame = (i == totalFrames - 1)
                };
                if (frame.ControlPoints != null)
                {
                    for (int j = 0; j < frame.ControlPoints.Length; j++)
                    {
                        ref var point = ref frame.ControlPoints[j];
                        viewModel.ControlPoints.Add(new ControlPointItemViewModel
                        {
                            Index = j,
                            MaxTime = targetTime,
                            X = point.X,
                            Y = point.Y,
                            ParentKeyFrameIndex = i
                        });
                    }
                }

                KeyFrameItems.Add(viewModel);
            }
        }

        private void UpdateCanvasRange()
        {
            float targetTime = GetTargetTimeInSeconds();

            if (_velocityCurve?.Curve.Frames == null || _velocityCurve.Curve.Frames.Length == 0)
            {
                _canvasStartTime = 0f;
                _canvasEndTime = Math.Min(1f, targetTime);
                return;
            }
            float minTime = float.MaxValue;
            float maxTime = float.MinValue;

            foreach (ref var frame in _velocityCurve.Curve.Frames.AsSpan())
            {
                if (frame.Time < minTime)
                {
                    minTime = frame.Time;
                }
                if (frame.Time > maxTime)
                {
                    maxTime = frame.Time;
                }
            }
            _canvasStartTime = Math.Max(0f, minTime - 0.5f);
            _canvasEndTime = Math.Min(targetTime, maxTime + 0.5f);

            if (_canvasEndTime <= _canvasStartTime)
            {
                _canvasEndTime = _canvasStartTime + 1f;
                if (_canvasEndTime > targetTime)
                {
                    _canvasEndTime = targetTime;
                    _canvasStartTime = Math.Max(0f, _canvasEndTime - 1f);
                }
            }
        }

        private float GetTargetTimeInSeconds()
        {
            if (_timing == null)
            {
                return 1f;
            }

            return _timing.TargetMin * 60f + _timing.TargetSecond + _timing.TargetTick / 1000f;
        }

        private (float screenX, float screenY) DataToScreen(float time, float value, float width, float height)
        {
            var (startTime, endTime) = GetCurveTimeRange();
            float timeRange = endTime - startTime;

            float screenX = timeRange > 0 ? ((time - startTime) / timeRange) * width : width / 2f;
            float screenY = height - ((value - MinValue) / (MaxValue - MinValue)) * height;

            return (screenX, screenY);
        }

        private (float time, float value) ScreenToData(float screenX, float screenY, float width, float height)
        {
            var (startTime, endTime) = GetCurveTimeRange();
            float timeRange = endTime - startTime;

            float time = startTime + (screenX / width) * timeRange;
            float value = MaxValue - (screenY / height) * (MaxValue - MinValue);

            return (time, value);
        }

        private void OnCanvasPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_velocityCurve?.Curve.Frames == null)
            {
                return;
            }

            var point = e.GetCurrentPoint(CurveCanvas);
            float px = (float)point.Position.X;
            float py = (float)point.Position.Y;
            float width = (float)CurveCanvas.ActualWidth;
            float height = (float)CurveCanvas.ActualHeight;

            foreach (var keyFrame in KeyFrameItems)
            {
                foreach (var cp in keyFrame.ControlPoints)
                {
                    cp.IsSelected = false;
                }
            }

            if (TryHitControlPoint(px, py, width, height, out int kfIdx, out int cpIdx))
            {
                _currentDragTarget = DragTarget.ControlPoint;
                _dragKeyFrameIndex = kfIdx;
                _dragControlPointIndex = cpIdx;
                _isDragging = true;

                SetControlPointSelected(kfIdx, cpIdx, true);

                CurveCanvas.CapturePointer(e.Pointer);
                e.Handled = true;
                return;
            }

            if (TryHitKeyFrame(px, py, width, height, out int frameIdx))
            {
                _currentDragTarget = DragTarget.KeyFrame;
                _dragKeyFrameIndex = frameIdx;
                _dragControlPointIndex = -1;
                _isDragging = true;
                CurveCanvas.CapturePointer(e.Pointer);
                e.Handled = true;
                return;
            }

            _currentDragTarget = DragTarget.None;
            _isDragging = false;
        }

        private void OnCanvasPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isDragging || _velocityCurve == null)
            {
                return;
            }

            var point = e.GetCurrentPoint(CurveCanvas);
            float px = (float)point.Position.X;
            float py = (float)point.Position.Y;
            float width = (float)CurveCanvas.ActualWidth;
            float height = (float)CurveCanvas.ActualHeight;

            var (time, value) = ScreenToData(px, py, width, height);
            float targetTime = GetTargetTimeInSeconds();

            time = Math.Clamp(time, 0f, targetTime);
            value = Math.Clamp(value, MinValue, MaxValue);

            if (_currentDragTarget == DragTarget.KeyFrame)
            {
                UpdateKeyFramePosition(_dragKeyFrameIndex, time, value);
            }
            else if (_currentDragTarget == DragTarget.ControlPoint)
            {
                UpdateControlPointPosition(_dragKeyFrameIndex, _dragControlPointIndex, time, value);
            }

            e.Handled = true;
        }

        private void OnCanvasPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_isDragging)
            {
                _isDragging = false;
                CurveCanvas.ReleasePointerCapture(e.Pointer);

                if (_currentDragTarget == DragTarget.KeyFrame)
                {
                    SortKeyFramesByTime();
                }

                foreach (var keyFrame in KeyFrameItems)
                {
                    foreach (var cp in keyFrame.ControlPoints)
                    {
                        cp.IsSelected = false;
                    }
                }

                _currentDragTarget = DragTarget.None;
                _dragKeyFrameIndex = -1;
                _dragControlPointIndex = -1;

                NotifyCurveChanged();
                e.Handled = true;
            }
        }

        private void OnCanvasPointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            _isDragging = false;
            _currentDragTarget = DragTarget.None;
            _dragKeyFrameIndex = -1;
            _dragControlPointIndex = -1;

            foreach (var keyFrame in KeyFrameItems)
            {
                foreach (var cp in keyFrame.ControlPoints)
                {
                    cp.IsSelected = false;
                }
            }
        }

        private void SetControlPointSelected(int keyFrameIndex, int controlPointIndex, bool isSelected)
        {
            if (keyFrameIndex >= 0 && keyFrameIndex < KeyFrameItems.Count)
            {
                var keyFrame = KeyFrameItems[keyFrameIndex];
                if (controlPointIndex >= 0 && controlPointIndex < keyFrame.ControlPoints.Count)
                {
                    keyFrame.ControlPoints[controlPointIndex].IsSelected = isSelected;
                }
            }
        }

        private bool TryHitKeyFrame(float px, float py, float width, float height, out int frameIndex)
        {
            frameIndex = -1;
            if (_velocityCurve?.Curve.Frames == null)
            {
                return false;
            }

            for (int i = 0; i < _velocityCurve.Curve.Frames.Length; i++)
            {
                ref var frame = ref _velocityCurve.Curve.Frames[i];
                var (screenX, screenY) = DataToScreen(frame.Time, frame.Value, width, height);

                float dx = px - screenX;
                float dy = py - screenY;
                if (dx * dx + dy * dy <= HitRadius * HitRadius)
                {
                    frameIndex = i;
                    return true;
                }
            }
            return false;
        }

        private bool TryHitControlPoint(float px, float py, float width, float height,
            out int keyFrameIndex, out int controlPointIndex)
        {
            keyFrameIndex = -1;
            controlPointIndex = -1;
            if (_velocityCurve?.Curve.Frames == null)
            {
                return false;
            }

            for (int i = 0; i < _velocityCurve.Curve.Frames.Length - 1; i++)
            {
                ref var frame = ref _velocityCurve.Curve.Frames[i];
                if (frame.ControlPoints == null)
                {
                    continue;
                }

                for (int j = 0; j < frame.ControlPoints.Length; j++)
                {
                    ref var cp = ref frame.ControlPoints[j];
                    var (screenX, screenY) = DataToScreen(cp.X, cp.Y, width, height);

                    float dx = px - screenX;
                    float dy = py - screenY;
                    if (dx * dx + dy * dy <= HitRadius * HitRadius)
                    {
                        keyFrameIndex = i;
                        controlPointIndex = j;
                        return true;
                    }
                }
            }
            return false;
        }

        private void UpdateKeyFramePosition(int frameIndex, float time, float value)
        {
            if (frameIndex < 0 || frameIndex >= _velocityCurve.Curve.Frames.Length)
            {
                return;
            }

            _velocityCurve.Curve.Frames[frameIndex].Time = time;
            _velocityCurve.Curve.Frames[frameIndex].Value = value;

            if (frameIndex < KeyFrameItems.Count)
            {
                _isUpdating = true;
                KeyFrameItems[frameIndex].Time = time;
                KeyFrameItems[frameIndex].Value = value;
                _isUpdating = false;
            }

            RequestRedraw();
        }

        private void UpdateControlPointPosition(int keyFrameIndex, int controlPointIndex, float time, float value)
        {
            if (keyFrameIndex < 0 || keyFrameIndex >= _velocityCurve.Curve.Frames.Length)
            {
                return;
            }

            ref var frame = ref _velocityCurve.Curve.Frames[keyFrameIndex];
            if (frame.ControlPoints == null || controlPointIndex < 0 || controlPointIndex >= frame.ControlPoints.Length)
            {
                return;
            }

            frame.ControlPoints[controlPointIndex] = new ControlPoint(time, value);
            if (keyFrameIndex < KeyFrameItems.Count)
            {
                var kfVm = KeyFrameItems[keyFrameIndex];
                if (controlPointIndex < kfVm.ControlPoints.Count)
                {
                    _isUpdating = true;
                    kfVm.ControlPoints[controlPointIndex].X = time;
                    kfVm.ControlPoints[controlPointIndex].Y = value;
                    _isUpdating = false;
                }
            }

            UpdateCanvasRange();
            RequestRedraw();
        }

        private void OnAddKeyFrameClicked(object sender, RoutedEventArgs e)
        {
            if (_velocityCurve == null)
            {
                return;
            }

            float targetTime = GetTargetTimeInSeconds();
            float newTime = 0f;

            if (_velocityCurve.Curve.Frames.Length == 0)
            {
                newTime = targetTime;
            }
            else
            {
                float firstFrameTime = _velocityCurve.Curve.Frames[0].Time;
                newTime = Math.Max(0f, firstFrameTime - 0.1f);
            }

            newTime = Math.Clamp(newTime, 0f, targetTime);

            var newFrame = new KeyFrame
            {
                Index = (uint)_velocityCurve.Curve.Frames.Length,
                Time = newTime,
                Value = 0f,
                InterpolationType = CurveInterpolationType.Linear,
                ControlPoints = Array.Empty<ControlPoint>()
            };

            var frames = new KeyFrame[_velocityCurve.Curve.Frames.Length + 1];
            Array.Copy(_velocityCurve.Curve.Frames, frames, _velocityCurve.Curve.Frames.Length);
            frames[^1] = newFrame;
            _velocityCurve.Curve = new KeyFrames { Frames = frames };

            UpdateCanvasRange();
            RefreshKeyFrameList();
            SortKeyFramesByTime();
            NotifyCurveChanged();
            RequestRedraw();
        }

        private void OnRemoveKeyFrameClicked(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not int index)
            {
                return;
            }

            if (_velocityCurve == null || index < 0 || index >= _velocityCurve.Curve.Frames.Length)
            {
                return;
            }

            var frames = new KeyFrame[_velocityCurve.Curve.Frames.Length - 1];
            int destIndex = 0;
            for (int i = 0; i < _velocityCurve.Curve.Frames.Length; i++)
            {
                if (i != index)
                {
                    frames[destIndex++] = _velocityCurve.Curve.Frames[i];
                }
            }

            _velocityCurve.Curve = new KeyFrames { Frames = frames };
            UpdateCanvasRange();
            RefreshKeyFrameList();
            NotifyCurveChanged();
            RequestRedraw();
        }

        private void OnKeyFrameTimeChanged(object sender, RoutedEventArgs e)
        {
            if (_isUpdating || _velocityCurve == null)
            {
                return;
            }

            if (sender is not TextBox textBox)
            {
                return;
            }

            var item = textBox.DataContext as KeyFrameItemViewModel;
            if (item == null || item.Index < 0 || item.Index >= _velocityCurve.Curve.Frames.Length)
            {
                return;
            }

            if (float.TryParse(textBox.Text, out float time))
            {
                float targetTime = GetTargetTimeInSeconds();
                time = Math.Clamp(time, 0f, targetTime);

                _velocityCurve.Curve.Frames[item.Index].Time = time;
                item.Time = time;

                SortKeyFramesByTime();
                NotifyCurveChanged();
                RequestRedraw();
            }
        }

        private void OnKeyFrameValueChanged(object sender, RoutedEventArgs e)
        {
            if (_isUpdating || _velocityCurve == null)
            {
                return;
            }

            if (sender is not TextBox textBox)
            {
                return;
            }

            var item = textBox.DataContext as KeyFrameItemViewModel;
            if (item == null || item.Index < 0 || item.Index >= _velocityCurve.Curve.Frames.Length)
            {
                return;
            }

            if (float.TryParse(textBox.Text, out float value))
            {
                value = Math.Clamp(value, MinValue, MaxValue);
                _velocityCurve.Curve.Frames[item.Index].Value = value;
                item.Value = value;

                NotifyCurveChanged();
                RequestRedraw();
            }
        }

        private void OnKeyFrameInterpolationChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdating || _velocityCurve == null)
            {
                return;
            }

            if (sender is not ComboBox comboBox)
            {
                return;
            }

            int keyFrameIndex = -1;
            if (comboBox.Tag is int tagIndex)
            {
                keyFrameIndex = tagIndex;
            }

            if (keyFrameIndex < 0 || keyFrameIndex >= _velocityCurve.Curve.Frames.Length)
            {
                return;
            }

            if (comboBox.SelectedItem is ComboBoxItem comboItem &&
                Enum.TryParse<CurveInterpolationType>(comboItem.Tag?.ToString(), out var type))
            {
                _velocityCurve.Curve.Frames[keyFrameIndex].InterpolationType = type;

                if (keyFrameIndex < KeyFrameItems.Count)
                {
                    KeyFrameItems[keyFrameIndex].InterpolationType = type;
                }

                NotifyCurveChanged();
                RequestRedraw();
            }
        }

        private void OnAddControlPointClicked(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button)
            {
                return;
            }

            if (button.Tag is not int keyFrameIndex)
            {
                return;
            }

            if (_velocityCurve == null)
            {
                return;
            }

            if (keyFrameIndex < 0 || keyFrameIndex >= _velocityCurve.Curve.Frames.Length)
            {
                return;
            }

            ref var frame = ref _velocityCurve.Curve.Frames[keyFrameIndex];

            if (frame.InterpolationType == CurveInterpolationType.Bezier && frame.ControlPoints != null &&
                frame.ControlPoints.Length >= 2)
            {
                return;
            }

            var points = frame.ControlPoints ?? Array.Empty<ControlPoint>();
            float targetTime = GetTargetTimeInSeconds();
            float defaultX = points.Length == 0
                ? Math.Min(frame.Time + 0.1f, targetTime)
                : Math.Min(points[^1].X + 0.1f, targetTime);

            var newPoints = new ControlPoint[points.Length + 1];
            Array.Copy(points, newPoints, points.Length);
            newPoints[^1] = new ControlPoint(0.5f, 0f);
            frame.ControlPoints = newPoints;
            if (keyFrameIndex < KeyFrameItems.Count)
            {
                var keyFrameVm = KeyFrameItems[keyFrameIndex];
                keyFrameVm.ControlPoints.Add(new ControlPointItemViewModel
                {
                    Index = keyFrameVm.ControlPoints.Count,
                    X = defaultX,
                    Y = 0f,
                    MaxTime = targetTime,
                    ParentKeyFrameIndex = keyFrameIndex
                });
            }

            NotifyCurveChanged();
            RequestRedraw();
        }

        private void OnRemoveControlPointClicked(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button)
            {
                return;
            }

            var controlPointVm = button.DataContext as ControlPointItemViewModel;
            if (controlPointVm == null)
            {
                return;
            }

            int keyFrameIndex = controlPointVm.ParentKeyFrameIndex;
            int controlPointIndex = controlPointVm.Index;

            if (_velocityCurve == null)
            {
                return;
            }

            if (keyFrameIndex < 0 || keyFrameIndex >= _velocityCurve.Curve.Frames.Length)
            {
                return;
            }

            ref var frame = ref _velocityCurve.Curve.Frames[keyFrameIndex];
            if (frame.ControlPoints == null || controlPointIndex < 0 || controlPointIndex >= frame.ControlPoints.Length)
            {
                return;
            }

            var points = new ControlPoint[frame.ControlPoints.Length - 1];
            int destIndex = 0;
            for (int i = 0; i < frame.ControlPoints.Length; i++)
            {
                if (i != controlPointIndex)
                {
                    points[destIndex++] = frame.ControlPoints[i];
                }
            }
            frame.ControlPoints = points;
            if (keyFrameIndex < KeyFrameItems.Count)
            {
                var keyFrameVm = KeyFrameItems[keyFrameIndex];
                keyFrameVm.ControlPoints.RemoveAt(controlPointIndex);
                for (int i = 0; i < keyFrameVm.ControlPoints.Count; i++)
                {
                    keyFrameVm.ControlPoints[i].Index = i;
                }
            }

            NotifyCurveChanged();
            RequestRedraw();
        }

        private void OnControlPointChanged(object sender, RoutedEventArgs e)
        {
            if (_isUpdating || _velocityCurve == null)
            {
                return;
            }

            if (sender is not TextBox textBox)
            {
                return;
            }

            var item = textBox.DataContext as ControlPointItemViewModel;
            if (item == null)
            {
                return;
            }

            int keyFrameIndex = item.ParentKeyFrameIndex;
            if (keyFrameIndex < 0 || keyFrameIndex >= _velocityCurve.Curve.Frames.Length)
            {
                return;
            }

            ref var frame = ref _velocityCurve.Curve.Frames[keyFrameIndex];
            if (frame.ControlPoints == null || item.Index < 0 || item.Index >= frame.ControlPoints.Length)
            {
                return;
            }

            float targetTime = GetTargetTimeInSeconds();
            float x = Math.Clamp(item.X, 0f, targetTime);
            float y = Math.Clamp(item.Y, MinValue, MaxValue);

            frame.ControlPoints[item.Index] = new ControlPoint(x, y);

            NotifyCurveChanged();
            RequestRedraw();
        }

        private void SortKeyFramesByTime()
        {
            if (_velocityCurve?.Curve.Frames == null || _velocityCurve.Curve.Frames.Length <= 1)
            {
                return;
            }

            Array.Sort(_velocityCurve.Curve.Frames, (a, b) => a.Time.CompareTo(b.Time));

            for (int i = 0; i < _velocityCurve.Curve.Frames.Length; i++)
            {
                _velocityCurve.Curve.Frames[i].Index = (uint)i;
            }

            RefreshKeyFrameList();
        }

        private void NotifyCurveChanged()
        {
            CurveChanged?.Invoke(this, _velocityCurve);
        }

        private void RequestRedraw()
        {
            _needsRedraw = true;
            CurveCanvas?.Invalidate();
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

        private void OnCanvasDraw(CanvasControl sender, CanvasDrawEventArgs args)
        {
            var ds = args.DrawingSession;
            float width = (float)sender.ActualWidth;
            float height = (float)sender.ActualHeight;

            if (width <= 0 || height <= 0)
            {
                return;
            }

            ds.Clear(Color.FromArgb(255, 30, 30, 30));
            DrawGrid(ds, width, height);

            if (_velocityCurve?.Curve.Frames != null && _velocityCurve.Curve.Frames.Length >= 2)
            {
                DrawCurve(ds, width, height);
                DrawControlPoints(ds, width, height);
            }

            DrawKeyFrames(ds, width, height);
        }

        private void DrawGrid(CanvasDrawingSession ds, float width, float height)
        {
            var gridColor = Color.FromArgb(80, 128, 128, 128);
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

            float zeroY = height * (MaxValue / (MaxValue - MinValue));
            ds.DrawLine(0, zeroY, width, zeroY, Colors.Gray, 2f);
        }

        private void DrawCurve(CanvasDrawingSession ds, float width, float height)
        {
            if (_velocityCurve?.Curve.Frames == null || _velocityCurve.Curve.Frames.Length < 2)
            {
                return;
            }

            var (startTime, endTime) = GetCurveTimeRange();
            float timeRange = endTime - startTime;
            if (timeRange <= 0)
            {
                return;
            }

            int steps = (int)(timeRange / Precision);
            if (steps <= 0)
            {
                return;
            }

            var pathBuilder = new CanvasPathBuilder(ds);
            bool started = false;

            for (int i = 0; i <= steps; i++)
            {
                float t = startTime + (i * Precision);
                float value = _velocityCurve.Evaluate(t);

                float x = ((t - startTime) / timeRange) * width;
                float y = height - ((value - MinValue) / (MaxValue - MinValue)) * height;

                x = Math.Clamp(x, 0, width);
                y = Math.Clamp(y, 0, height);

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
                ds.DrawGeometry(geometry, Colors.Cyan, 2f);
            }
        }

        private void DrawKeyFrames(CanvasDrawingSession ds, float width, float height)
        {
            if (_velocityCurve?.Curve.Frames == null || _velocityCurve.Curve.Frames.Length == 0)
            {
                return;
            }

            var (startTime, endTime) = GetCurveTimeRange();
            float timeRange = endTime - startTime;

            const float diamondSize = 8f;

            for (int i = 0; i < _velocityCurve.Curve.Frames.Length; i++)
            {
                ref var frame = ref _velocityCurve.Curve.Frames[i];

                float x;
                if (_velocityCurve.Curve.Frames.Length == 1)
                {
                    x = width / 2f;
                }
                else if (timeRange <= 0)
                {
                    x = width / 2f;
                }
                else
                {
                    x = ((frame.Time - startTime) / timeRange) * width;
                }

                float y = height - ((frame.Value - MinValue) / (MaxValue - MinValue)) * height;

                x = Math.Clamp(x, diamondSize, width - diamondSize);
                y = Math.Clamp(y, diamondSize, height - diamondSize);

                var diamondPath = new CanvasPathBuilder(ds);
                diamondPath.BeginFigure(x, y - diamondSize);
                diamondPath.AddLine(x + diamondSize, y);
                diamondPath.AddLine(x, y + diamondSize);
                diamondPath.AddLine(x - diamondSize, y);
                diamondPath.EndFigure(CanvasFigureLoop.Closed);

                using var diamond = CanvasGeometry.CreatePath(diamondPath);
                bool isSelected = _isDragging && _currentDragTarget == DragTarget.KeyFrame && _dragKeyFrameIndex == i;
                Color fillColor = frame.InterpolationType switch
                {
                    CurveInterpolationType.Linear => Colors.White,
                    CurveInterpolationType.Bezier => Colors.Orange,
                    CurveInterpolationType.CatmullRom => Colors.LimeGreen,
                    CurveInterpolationType.Step => Colors.Purple,
                    _ => Colors.White
                };

                ds.FillGeometry(diamond, fillColor);
                ds.DrawGeometry(diamond, Colors.Black, 2f);
            }
        }

        private void DrawControlPoints(CanvasDrawingSession ds, float width, float height)
        {
            if (_velocityCurve?.Curve.Frames == null || _velocityCurve.Curve.Frames.Length < 2)
            {
                return;
            }

            var (startTime, endTime) = GetCurveTimeRange();
            float timeRange = endTime - startTime;
            if (timeRange <= 0)
            {
                return;
            }

            const float circleRadius = 5f;

            for (int frameIdx = 0; frameIdx < _velocityCurve.Curve.Frames.Length - 1; frameIdx++)
            {
                ref var frame = ref _velocityCurve.Curve.Frames[frameIdx];
                ref var nextFrame = ref _velocityCurve.Curve.Frames[frameIdx + 1];

                if (frame.ControlPoints == null || frame.ControlPoints.Length == 0)
                {
                    continue;
                }

                float frameX = ((frame.Time - startTime) / timeRange) * width;
                float frameY = height - ((frame.Value - MinValue) / (MaxValue - MinValue)) * height;
                float nextFrameX = ((nextFrame.Time - startTime) / timeRange) * width;
                float nextFrameY = height - ((nextFrame.Value - MinValue) / (MaxValue - MinValue)) * height;
                bool isCatmullRom = frame.InterpolationType == CurveInterpolationType.CatmullRom;
                bool isBezier = frame.InterpolationType == CurveInterpolationType.Bezier;
                Span<(float x, float y)> controlSP = stackalloc (float, float)[frame.ControlPoints.Length];

                for (int i = 0; i < frame.ControlPoints.Length; i++)
                {
                    var (cpX, cpY) = DataToScreen(frame.ControlPoints[i].X, frame.ControlPoints[i].Y, width, height);
                    controlSP[i] = (
                        Math.Clamp(cpX, circleRadius, width - circleRadius),
                        Math.Clamp(cpY, circleRadius, height - circleRadius)
                    );
                }


                if (isCatmullRom)
                {
                    ds.DrawLine(frameX, frameY, controlSP[0].x, controlSP[0].y, Colors.Gray, 1f);
                    for (int i = 0; i < frame.ControlPoints.Length - 1; i++)
                    {
                        ds.DrawLine(controlSP[i].x, controlSP[i].y, controlSP[i + 1].x, controlSP[i + 1].y, Colors.Gray, 1f);
                    }

                    ds.DrawLine(controlSP[^1].x, controlSP[^1].y, nextFrameX, nextFrameY, Colors.Gray, 1f);
                }
                else if (isBezier)
                {
                    ds.DrawLine(frameX, frameY, controlSP[0].x, controlSP[0].y, Colors.Gray, 1f);
                    if (frame.ControlPoints.Length > 1)
                    {
                        ds.DrawLine(nextFrameX, nextFrameY, controlSP[1].x, controlSP[1].y, Colors.Gray, 1f);
                    }
                }

                for (int cpIdx = 0; cpIdx < frame.ControlPoints.Length; cpIdx++)
                {
                    var (cpX, cpY) = controlSP[cpIdx];

                    bool isSelected = _isDragging &&
                                      _currentDragTarget == DragTarget.ControlPoint &&
                                      _dragKeyFrameIndex == frameIdx &&
                                      _dragControlPointIndex == cpIdx;

                    if (isBezier && cpIdx == 1)
                    {
                        ds.DrawLine(nextFrameX, nextFrameY, cpX, cpY, Colors.Gray, 1f);
                    }
                    else if (!isCatmullRom)
                    {
                        ds.DrawLine(frameX, frameY, cpX, cpY, Colors.Gray, 1f);
                    }

                    ds.FillCircle(cpX, cpY, circleRadius, Colors.LimeGreen);
                    ds.DrawCircle(cpX, cpY, circleRadius, Colors.Black, 1.5f);
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class KeyFrameItemViewModel : INotifyPropertyChanged
    {
        private int _index;
        private float _time;
        private float _value;
        private CurveInterpolationType _interpolationType;
        private bool _isExpanded;
        private bool _isLastFrame;

        public KeyFrameItemViewModel()
        {
            ControlPoints.CollectionChanged += (s, e) =>
            {
                OnPropertyChanged(nameof(HasNoControlPoints));
                OnPropertyChanged(nameof(CanAddControlPoint));
            };
        }

        public ObservableCollection<ControlPointItemViewModel> ControlPoints { get; } = new();

        public int Index
        {
            get => _index;
            set { _index = value; OnPropertyChanged(); }
        }

        public float Time
        {
            get => _time;
            set { _time = value; OnPropertyChanged(); OnPropertyChanged(nameof(TimeString)); }
        }

        public float Value
        {
            get => _value;
            set { _value = value; OnPropertyChanged(); OnPropertyChanged(nameof(ValueString)); }
        }

        public CurveInterpolationType InterpolationType
        {
            get => _interpolationType;
            set
            {
                _interpolationType = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(InterpolationIndex));
                OnPropertyChanged(nameof(ShowControlPoints));
                OnPropertyChanged(nameof(CanAddControlPoint));
            }
        }

        public int InterpolationIndex
        {
            get => (int)_interpolationType;
            set
            {
                InterpolationType = (CurveInterpolationType)value;
            }
        }

        public bool IsExpanded
        {
            get => _isExpanded;
            set { _isExpanded = value; OnPropertyChanged(); }
        }

        public bool IsLastFrame
        {
            get => _isLastFrame;
            set { _isLastFrame = value; OnPropertyChanged(); }
        }

        public Visibility ShowControlPoints =>
            (_interpolationType == CurveInterpolationType.Bezier ||
             _interpolationType == CurveInterpolationType.CatmullRom) && !_isLastFrame
                ? Visibility.Visible
                : Visibility.Collapsed;

        public Visibility HasNoControlPoints =>
            ControlPoints.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        public string TimeString
        {
            get => _time.ToString("F3");
            set
            {
                if (float.TryParse(value, out float t))
                {
                    Time = t;
                }
            }
        }

        public string ValueString
        {
            get => _value.ToString("F3");
            set
            {
                if (float.TryParse(value, out float v))
                {
                    Value = Math.Clamp(v, -12f, 12f);
                }
            }
        }

        public bool CanAddControlPoint =>
            (_interpolationType == CurveInterpolationType.Bezier && ControlPoints.Count < 2) ||
            (_interpolationType == CurveInterpolationType.CatmullRom);

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class ControlPointItemViewModel : INotifyPropertyChanged
    {
        private int _index;
        private float _x;
        private float _y;
        private int _parentKeyFrameIndex;
        private float _maxTime = 1f;
        private bool _isSelected;

        public int Index
        {
            get => _index;
            set { _index = value; OnPropertyChanged(); }
        }

        public int ParentKeyFrameIndex
        {
            get => _parentKeyFrameIndex;
            set { _parentKeyFrameIndex = value; OnPropertyChanged(); }
        }

        public float X
        {
            get => _x;
            set { _x = Math.Clamp(value, 0f, 1f); OnPropertyChanged(); OnPropertyChanged(nameof(XString)); }
        }

        public float Y
        {
            get => _y;
            set { _y = Math.Clamp(value, -5f, 5f); OnPropertyChanged(); OnPropertyChanged(nameof(YString)); }
        }

        public float MaxTime
        {
            get => _maxTime;
            set
            {
                _maxTime = value;
                OnPropertyChanged();
            }
        }

        public string XString
        {
            get => _x.ToString("F3");
            set
            {
                if (float.TryParse(value, out float v))
                {
                    X = v;
                }
            }
        }

        public string YString
        {
            get => _y.ToString("F3");
            set
            {
                if (float.TryParse(value, out float v))
                {
                    Y = v;
                }
            }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class BoolToHighlightBrushConverter : Microsoft.UI.Xaml.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is bool isSelected && isSelected)
            {
                return new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Cyan);
            }
            return new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}