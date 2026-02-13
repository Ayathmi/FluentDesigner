using FluentDesigner.ECS.System;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Numerics;
using Windows.Foundation;
using Windows.System;

namespace FluentDesigner.Control;

public sealed partial class TimelineControl : UserControl
{
    private PlaybackService _playbackService;
    private CameraService _cameraService;
    private WaveformService _waveformService;
    private InspectorService _inspectorService;
    private CsvMarkerService _csvMarkerService;
    private EcsWorldService _ecsWorldService;

    private bool _isInitialized;
    private bool _isDragging;
    private bool _isPanning;
    private Point _lastPointerPosition;
    private bool _isPlayheadSnapEnabled = true;

    private double _pixelsPerSecond = 100.0;
    private double _viewStartTime;
    private double _totalDuration;
    private double _currentTime;
    private double _virtualPadding;

    private bool _hasAudioTrack;
    private bool _hasBeatMarkers;
    private bool _hasTrackPreview;
    private bool _hasCsvMarkers;

    private const double MinPixelsPerSecond = 10.0;
    private const double MaxPixelsPerSecond = 1000.0;
    private const double DefaultCameraDistance = 15f;
    private const float DefaultCameraYaw = 45f;
    private const float DefaultCameraPitch = 30f;

    private DateTime _lastPlayheadUpdate = DateTime.MinValue;
    private const double PlayheadUpdateIntervalMs = 4.0;
    private DispatcherTimer _sizeChangeDebounceTimer;

    public double PixelsPerSecond => _pixelsPerSecond;
    public double ViewStartTime => _viewStartTime;
    public double TotalDuration => _totalDuration;
    public double CurrentTime => _currentTime;
    public bool IsInteractionEnabled => _hasAudioTrack || _hasBeatMarkers || _hasTrackPreview || _hasCsvMarkers;

    public TimeRulerTrack timeRulerTrackControl => TimeRulerTrackControl;

    public event EventHandler<double> TimeChanged;
    public event EventHandler<(double start, double end)> VisibleRangeChanged;
    public event EventHandler<float> AudioDelayChanged;

    public TimelineControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;

        _sizeChangeDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _sizeChangeDebounceTimer.Tick += OnSizeChangeDebounce;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_isInitialized)
        {
            return;
        }

        var framework = AyaMvcFramework<AyaMvcFrameworkImpl>.EntryPoint;
        _playbackService = framework.GetService<PlaybackService>();
        _cameraService = framework.GetService<CameraService>();
        _waveformService = framework.GetService<WaveformService>();
        _inspectorService = framework.GetService<InspectorService>();
        _csvMarkerService = framework.GetService<CsvMarkerService>();
        _ecsWorldService = framework.GetService<EcsWorldService>();
        var hierarchyService = framework.GetService<HierarchyService>();

        TrackPreviewControl?.Initialize(_ecsWorldService, hierarchyService, _inspectorService); ;
        if (TrackPreviewControl != null)
        {
            TrackPreviewControl.NoteClicked += OnTrackPreviewNoteClicked;
            TrackPreviewControl.SizeChangedCallback = OnTrackPreviewSizeChanged;
        }

        if (_playbackService != null)
        {
            _playbackService.TimeUpdated += OnPlaybackTimeUpdated;
            _playbackService.PlayModeEntered += OnPlayModeEntered;
            _playbackService.PlayModeExited += OnPlayModeExited;
        }

        if (_waveformService != null)
        {
            _waveformService.WaveformLoaded += OnWaveformLoaded;
            _waveformService.WaveformCleared += OnWaveformCleared;

            if (_waveformService.Cache?.IsLoaded == true)
            {
                InitializeFromWaveform(_waveformService.Cache);
            }
        }

        if (_inspectorService != null)
        {
            var fileIO = GetFileIOState();
            if (fileIO != null)
            {
                fileIO.PropertyChanged += OnFileIOStateChanged;
                UpdateFileNames(fileIO);
            }
        }

        if (_csvMarkerService != null)
        {
            _csvMarkerService.MarkersLoaded += OnCsvMarkersLoaded;
            _csvMarkerService.MarkersCleared += OnCsvMarkersCleared;
        }
        CsvMarkerTrackControl?.Initialize(_csvMarkerService);
        if (CsvMarkerTrackControl != null)
        {
            CsvMarkerTrackControl.MarkerClicked += OnCsvMarkerClicked;
        }

        TimeRulerTrackControl?.UpdateView(_viewStartTime, GetVisibleEndTime(), _pixelsPerSecond);
        if (TimeRulerPanelControl != null)
        {
            TimeRulerPanelControl.SetInPointRequested += OnSetInPointRequested;
            TimeRulerPanelControl.SetOutPointRequested += OnSetOutPointRequested;
            TimeRulerPanelControl.ClearSelectionRequested += OnClearSelectionRequested;
            TimeRulerPanelControl.SnapToggleChanged += OnSnapToggleChanged;
            TimeRulerPanelControl.TimeJumpRequested += OnTimeJumpRequested;
            TimeRulerTrackControl.InOutPointsChanged += OnInOutPointsChanged;
            TimeRulerPanelControl.LoopToggleChanged += OnLoopToggleChanged;
        }

        WaveformTrackControl?.Initialize(_waveformService);

        if (WaveformTrackControl != null)
        {
            WaveformTrackControl.SizeChangedCallback = OnWaveformTrackSizeChanged;
        }

        UpdatePlayheadHeight();
        UpdateTrackStates();
        _isInitialized = true;
    }

    private void OnFileIOStateChanged(object sender, PropertyChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (sender is FileIOState fileIO)
            {
                UpdateFileNames(fileIO);
                UpdateTrackStates();
            }
        });
    }

    private void UpdateTrackStates()
    {
        _hasAudioTrack = _waveformService?.Cache?.IsLoaded == true;
        _hasBeatMarkers = false;
        _hasTrackPreview = _inspectorService?.Statistics?.TotalObjectCount > 0;
        _hasCsvMarkers = _inspectorService?.Statistics?.CsvMarkerCount > 0;

        bool anyTrackLoaded = _hasAudioTrack || _hasBeatMarkers || _hasTrackPreview || _hasCsvMarkers;

        if (DisabledOverlay != null)
        {
            DisabledOverlay.Visibility = anyTrackLoaded ? Visibility.Collapsed : Visibility.Visible;
        }

        if (InteractionLayer != null)
        {
            InteractionLayer.IsHitTestVisible = anyTrackLoaded;
        }
    }

    private void UpdateFileNames(FileIOState fileIO)
    {
        if (fileIO == null)
        {
            return;
        }

        if (AudioFileNameText != null)
        {
            AudioFileNameText.Text = fileIO.HasWavFile ? fileIO.WavFileName : "No file loaded";
        }

        if (CsvFileNameText != null)
        {
            CsvFileNameText.Text = fileIO.HasCsvFile ? fileIO.CsvFileName : "No file loaded";
        }

        if (TrackAudioDelayBox != null)
        {
            TrackAudioDelayBox.Value = fileIO.AudioDelaySeconds;
        }
    }

    private void OnTrackAudioDelayChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (double.IsNaN(args.NewValue))
        {
            return;
        }

        float delay = (float)Math.Round(args.NewValue / 1000f, 1);
        AudioDelayChanged?.Invoke(this, delay);
    }

    private void OnCsvMarkersLoaded(object sender, IReadOnlyList<CsvMarker> markers)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateTrackStates();
            if (_csvMarkerService != null && _csvMarkerService.MaxTimeSeconds > _totalDuration)
            {
                _totalDuration = _csvMarkerService.MaxTimeSeconds;
            }
            CsvMarkerTrackControl?.UpdateView(_viewStartTime, GetVisibleEndTime(), _pixelsPerSecond);
            TrackPreviewControl?.UpdateView(_viewStartTime, GetVisibleEndTime(), _pixelsPerSecond);

        });
    }

    private void OnCsvMarkersCleared(object sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateTrackStates();
            CsvMarkerTrackControl?.InvalidateCanvas();
        });
    }

    private FileIOState GetFileIOState()
    {
        return AyaMvcFramework<AyaMvcFrameworkImpl>.EntryPoint.GetService<InspectorService>().FileIO;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _sizeChangeDebounceTimer?.Stop();

        if (_playbackService != null)
        {
            _playbackService.TimeUpdated -= OnPlaybackTimeUpdated;
            _playbackService.PlayModeEntered -= OnPlayModeEntered;
            _playbackService.PlayModeExited -= OnPlayModeExited;
        }

        if (_waveformService != null)
        {
            _waveformService.WaveformLoaded -= OnWaveformLoaded;
            _waveformService.WaveformCleared -= OnWaveformCleared;
        }


        if (_csvMarkerService != null)
        {
            _csvMarkerService.MarkersLoaded -= OnCsvMarkersLoaded;
            _csvMarkerService.MarkersCleared -= OnCsvMarkersCleared;
        }

        if (CsvMarkerTrackControl != null)
        {
            CsvMarkerTrackControl.MarkerClicked -= OnCsvMarkerClicked;
        }

        var fileIO = GetFileIOState();
        if (fileIO != null)
        {
            fileIO.PropertyChanged -= OnFileIOStateChanged;
        }

        if (WaveformTrackControl != null)
        {
            WaveformTrackControl.SizeChangedCallback = null;
        }

        if (TimeRulerPanelControl != null)
        {
            TimeRulerPanelControl.SetInPointRequested -= OnSetInPointRequested;
            TimeRulerPanelControl.SetOutPointRequested -= OnSetOutPointRequested;
            TimeRulerPanelControl.ClearSelectionRequested -= OnClearSelectionRequested;
            TimeRulerPanelControl.SnapToggleChanged -= OnSnapToggleChanged;
            TimeRulerPanelControl.TimeJumpRequested -= OnTimeJumpRequested;
            TimeRulerTrackControl.InOutPointsChanged -= OnInOutPointsChanged;
            TimeRulerPanelControl.LoopToggleChanged -= OnLoopToggleChanged;
        }

        if (TrackPreviewControl != null)
        {
            TrackPreviewControl.NoteClicked -= OnTrackPreviewNoteClicked;
        }
    }

    private void OnLoopToggleChanged(object sender, bool isEnabled)
    {
        _playbackService?.SetLoopEnabled(isEnabled);
    }

    private void OnInOutPointsChanged(object sender, EventArgs e)
    {
        SyncLoopRegionToPlayback();
    }

    private void SyncLoopRegionToPlayback()
    {
        if (_playbackService == null || TimeRulerTrackControl == null)
        {
            return;
        }

        var selection = TimeRulerTrackControl.GetSelection();
        if (selection.HasValue)
        {
            _playbackService.SetLoopRegion((float)selection.Value.start, (float)selection.Value.end);
            _playbackService.SetLoopEnabled(true);
        }
        else
        {
            _playbackService.ClearLoopRegion();
            _playbackService.SetLoopEnabled(false);
        }
    }

    private void OnSetInPointRequested(object sender, double time)
    {
        TimeRulerTrackControl?.SetInPoint(time);
        SyncLoopRegionToPlayback();
    }

    private void OnSetOutPointRequested(object sender, double time)
    {
        TimeRulerTrackControl?.SetOutPoint(time);
        SyncLoopRegionToPlayback();
    }

    private void OnClearSelectionRequested(object sender, EventArgs e)
    {
        TimeRulerTrackControl?.ClearSelection();
        _playbackService?.ClearLoopRegion();
        _playbackService?.SetLoopEnabled(false);
    }

    private void OnSnapToggleChanged(object sender, bool isEnabled)
    {
        _isPlayheadSnapEnabled = isEnabled;
        if (isEnabled)
        {
            CenterPlayheadInView();
        }
    }

    private void OnTimeJumpRequested(object sender, double timeSeconds)
    {
        SeekToTime(timeSeconds);
    }

    private void CenterPlayheadInView()
    {
        if (!_isPlayheadSnapEnabled)
        {
            return;
        }

        double viewWidth = InteractionLayer.ActualWidth;
        double centerTime = _currentTime;
        _viewStartTime = Math.Max(0, centerTime - viewWidth / (2 * _pixelsPerSecond));

        NotifyVisibleRangeChanged();
        UpdatePlayheadPosition();
        UpdateAllTrackViews();
    }

    private void OnCsvMarkerClicked(object sender, float timeSeconds)
    {
        _currentTime = Math.Clamp(timeSeconds, 0, _totalDuration);
        UpdatePlayheadPosition();

        if (_playbackService?.IsInPlayMode == true)
        {
            _playbackService.Seek((float)_currentTime);
        }
        else
        {
            NavigateCameraToTime(_currentTime);
        }

        TimeChanged?.Invoke(this, _currentTime);
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _sizeChangeDebounceTimer.Stop();
        _sizeChangeDebounceTimer.Start();
    }

    private void OnSizeChangeDebounce(object sender, object e)
    {
        _sizeChangeDebounceTimer.Stop();
        UpdatePlayheadHeight();
        NotifyVisibleRangeChanged();
        if (WaveformTrackControl != null)
        {
            float width = (float)WaveformTrackControl.ActualWidth;
            float height = (float)WaveformTrackControl.ActualHeight;
            WaveformTrackControl.UpdateView(_viewStartTime, GetVisibleEndTime(), _pixelsPerSecond);
        }
    }

    private void OnWaveformTrackSizeChanged(float width, float height)
    {
        NotifyVisibleRangeChanged();
    }

    private void OnWaveformLoaded(object sender, WaveformCache cache)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            InitializeFromWaveform(cache);
            UpdateTrackStates();
        });
    }

    private void OnWaveformCleared(object sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _totalDuration = 0;
            _viewStartTime = 0;
            _currentTime = 0;
            _hasAudioTrack = false;
            UpdatePlayheadPosition();
            UpdateTrackStates();
            WaveformTrackControl?.InvalidateCanvas();
        });
    }

    private void InitializeFromWaveform(WaveformCache cache)
    {
        _totalDuration = cache.Duration.TotalSeconds;
        _viewStartTime = 0;
        _currentTime = 0;
        UpdatePlayheadPosition();
        NotifyVisibleRangeChanged();
        WaveformTrackControl?.InvalidateCanvas();
    }

    private void OnPlaybackTimeUpdated(object sender, float time)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastPlayheadUpdate).TotalMilliseconds < PlayheadUpdateIntervalMs)
        {
            return;
        }
        _lastPlayheadUpdate = now;

        DispatcherQueue.TryEnqueue(() =>
        {
            _currentTime = time;
            UpdatePlayheadPosition();

            if (_playbackService?.State == PlaybackState.Playing)
            {
                EnsureTimeVisible(time);
            }
        });
    }

    private void OnPlayModeEntered(object sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _totalDuration = _playbackService?.TotalTime ?? _totalDuration;
        });
    }

    private void OnPlayModeExited(object sender, EventArgs e) { }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!IsInteractionEnabled)
        {
            e.Handled = true;
            return;
        }

        var point = e.GetCurrentPoint(InteractionLayer);
        _lastPointerPosition = point.Position;

        if (point.Properties.IsMiddleButtonPressed)
        {
            _isPanning = true;
            InteractionLayer.CapturePointer(e.Pointer);
        }
        else if (point.Properties.IsLeftButtonPressed)
        {
            _isDragging = true;
            InteractionLayer.CapturePointer(e.Pointer);
            SetTimeFromPointer(point.Position);
        }

        e.Handled = true;
    }
    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(InteractionLayer);

        if (_isPanning)
        {
            double deltaX = point.Position.X - _lastPointerPosition.X;
            double deltaTime = deltaX / _pixelsPerSecond;

            if (_isPlayheadSnapEnabled)
            {
                double newTime = _currentTime - deltaTime;
                _currentTime = Math.Clamp(newTime, 0, _totalDuration);
                if (_playbackService != null)
                {
                    _playbackService.EditorTime = (float)_currentTime;
                }
                UpdatePlayheadPosition();
                TimeChanged?.Invoke(this, _currentTime);
            }
            else
            {
                _viewStartTime = Math.Max(0, _viewStartTime - deltaTime);
                NotifyVisibleRangeChanged();
                UpdatePlayheadPosition();
            }

            _lastPointerPosition = point.Position;
            WaveformTrackControl?.UpdateView(_viewStartTime, GetVisibleEndTime(), _pixelsPerSecond);
            CsvMarkerTrackControl?.UpdateView(_viewStartTime, GetVisibleEndTime(), _pixelsPerSecond);
            TrackPreviewControl?.UpdateView(_viewStartTime, GetVisibleEndTime(), _pixelsPerSecond);
            TimeRulerTrackControl?.UpdateView(_viewStartTime, GetVisibleEndTime(), _pixelsPerSecond);
        }
        else if (_isDragging)
        {
            SetTimeFromPointer(point.Position);
        }

        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        bool wasDragging = _isDragging;
        bool wasPanning = _isPanning;

        _isDragging = false;
        _isPanning = false;
        InteractionLayer.ReleasePointerCapture(e.Pointer);

        if (wasDragging)
        {
            CommitTimeChange();
        }

        e.Handled = true;
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(InteractionLayer);
        int delta = point.Properties.MouseWheelDelta;

        bool isZooming = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (isZooming)
        {
            double mouseTime = _isPlayheadSnapEnabled
                ? _currentTime
                : _viewStartTime + point.Position.X / _pixelsPerSecond;

            double zoomFactor = delta > 0 ? 1.2 : 1.0 / 1.2;
            double newPixelsPerSecond = Math.Clamp(_pixelsPerSecond * zoomFactor, MinPixelsPerSecond, MaxPixelsPerSecond);

            if (!_isPlayheadSnapEnabled)
            {
                double newViewStart = mouseTime - point.Position.X / newPixelsPerSecond;
                _viewStartTime = Math.Max(0, newViewStart);
            }

            _pixelsPerSecond = newPixelsPerSecond;
        }
        else
        {
            double scrollAmount = delta > 0 ? -0.5 : 0.5;

            if (_isPlayheadSnapEnabled)
            {
                double newTime = _currentTime + scrollAmount;
                _currentTime = Math.Clamp(newTime, 0, _totalDuration);
                if (_playbackService != null)
                {
                    _playbackService.EditorTime = (float)_currentTime;
                }
                TimeChanged?.Invoke(this, _currentTime);
            }
            else
            {
                _viewStartTime = Math.Max(0, _viewStartTime + scrollAmount);
            }
        }

        NotifyVisibleRangeChanged();
        UpdatePlayheadPosition();
        WaveformTrackControl?.UpdateView(_viewStartTime, GetVisibleEndTime(), _pixelsPerSecond);
        CsvMarkerTrackControl?.UpdateView(_viewStartTime, GetVisibleEndTime(), _pixelsPerSecond);
        TrackPreviewControl?.UpdateView(_viewStartTime, GetVisibleEndTime(), _pixelsPerSecond);
        TimeRulerTrackControl?.UpdateView(_viewStartTime, GetVisibleEndTime(), _pixelsPerSecond);

        e.Handled = true;
    }

    private void OnTrackPreviewNoteClicked(object sender, float timeSeconds)
    {
        SeekToTime(timeSeconds);
    }

    private void OnTrackPreviewSizeChanged(float width, float height) { }

    private void UpdateAllTrackViews()
    {
        double endTime = GetVisibleEndTime();
        WaveformTrackControl?.UpdateView(_viewStartTime, endTime, _pixelsPerSecond);
        CsvMarkerTrackControl?.UpdateView(_viewStartTime, endTime, _pixelsPerSecond);
        TrackPreviewControl?.UpdateView(_viewStartTime, endTime, _pixelsPerSecond);
        TimeRulerTrackControl?.UpdateView(_viewStartTime, GetVisibleEndTime(), _pixelsPerSecond);
    }

    private void CommitTimeChange()
    {
        if (_playbackService?.IsInPlayMode == true)
        {
            _playbackService.Seek((float)_currentTime);
        }
        else
        {
            NavigateCameraToTime(_currentTime);
        }
    }

    private void NavigateCameraToTime(double timeSeconds)
    {
        if (_cameraService == null || _cameraService.IsPlaybackMode)
        {
            return;
        }

        var targetPosition = new Vector3(0f, 0f, (float)(timeSeconds * 10.0));

        _cameraService.AnimateTo(
            target: targetPosition,
            distance: (float)DefaultCameraDistance,
            yaw: DefaultCameraYaw,
            pitch: DefaultCameraPitch);
    }

    public void SeekToTime(double timeSeconds)
    {
        _currentTime = Math.Clamp(timeSeconds, 0, _totalDuration);
        UpdatePlayheadPosition();
        EnsureTimeVisible(timeSeconds);
        if (_playbackService != null)
        {
            _playbackService.EditorTime = (float)_currentTime;
        }
        CommitTimeChange();
    }

    private void SetTimeFromPointer(Point position)
    {
        if (!IsInteractionEnabled)
        {
            return;
        }

        double viewWidth = InteractionLayer.ActualWidth;
        double time;

        if (_isPlayheadSnapEnabled)
        {
            double centerX = viewWidth / 2;
            double offsetX = position.X - centerX;
            double offsetTime = offsetX / _pixelsPerSecond;
            time = _currentTime + offsetTime;
        }
        else
        {
            time = _viewStartTime + position.X / _pixelsPerSecond;
        }

        time = Math.Clamp(time, 0, _totalDuration);

        _currentTime = time;
        UpdatePlayheadPosition();
        TimeChanged?.Invoke(this, time);

        if (_playbackService != null)
        {
            _playbackService.EditorTime = (float)time;
        }

        if (_playbackService?.IsInPlayMode == true)
        {
            _playbackService.PreviewSeek((float)time);
        }
    }

    private void EnsureTimeVisible(double timeSeconds)
    {
        if (_isPlayheadSnapEnabled)
        {
            return;
        }

        double viewWidth = ActualWidth;
        double viewEndTime = _viewStartTime + viewWidth / _pixelsPerSecond;

        if (timeSeconds < _viewStartTime)
        {
            _viewStartTime = Math.Max(0, timeSeconds - 0.5);
            NotifyVisibleRangeChanged();
            UpdateAllTrackViews();
        }
        else if (timeSeconds > viewEndTime - 1.0)
        {
            _viewStartTime = timeSeconds - viewWidth / _pixelsPerSecond + 1.0;
            NotifyVisibleRangeChanged();
            UpdateAllTrackViews();
        }
    }

    private void UpdatePlayheadPosition()
    {
        double viewWidth = InteractionLayer.ActualWidth;
        _virtualPadding = viewWidth / 2;
        double x;

        if (_isPlayheadSnapEnabled)
        {
            x = viewWidth / 2;

            double viewStartTime = _currentTime - (viewWidth / 2) / _pixelsPerSecond;

            _viewStartTime = viewStartTime;
            UpdateAllTrackViews();
        }
        else
        {
            x = (_currentTime - _viewStartTime) * _pixelsPerSecond;
        }

        Canvas.SetLeft(PlayheadTopTriangle, x);
        Canvas.SetLeft(PlayheadLine, x - 1);
        Canvas.SetLeft(PlayheadBottomTriangle, x);
        TimeRulerPanelControl?.UpdateTimeDisplay(_currentTime);
    }

    private void UpdatePlayheadHeight()
    {
        if (PlayheadLine != null && PlayheadCanvas != null && PlayheadClipBorder != null)
        {
            double totalHeight = PlayheadClipBorder.ActualHeight;
            double totalWidth = PlayheadClipBorder.ActualWidth;
            double triangleHeight = 10;

            if (PlayheadClipGeometry != null)
            {
                PlayheadClipGeometry.Rect = new Windows.Foundation.Rect(0, 0, totalWidth, totalHeight);
            }

            PlayheadLine.Height = Math.Max(0, totalHeight - triangleHeight * 2);
            Canvas.SetTop(PlayheadLine, triangleHeight);
            Canvas.SetTop(PlayheadBottomTriangle, totalHeight - triangleHeight);
        }
    }

    private double GetVisibleEndTime()
    {
        return _viewStartTime + ActualWidth / _pixelsPerSecond;
    }

    private void NotifyVisibleRangeChanged()
    {
        VisibleRangeChanged?.Invoke(this, (_viewStartTime, GetVisibleEndTime()));
    }
}