using FluentDesigner.ECS.Components;
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
using R3;
using Windows.UI;

namespace FluentDesigner.Control;

public sealed partial class TrackPreviewTrack : UserControl
{
    private EcsWorldService _ecsWorld;
    private HierarchyService _hierarchyService;
    private InspectorService _inspectorService;
    private IDisposable _propertyChangedSubscription;
    private bool _isInitialized;
    private float _canvasWidth;
    private float _canvasHeight;

    private double _viewStartTime;
    private double _viewEndTime;
    private double _pixelsPerSecond = 100.0;

    private static readonly Color BackgroundColor = Color.FromArgb(255, 35, 35, 35);
    private static readonly Color ClickColor = Color.FromArgb(255, 135, 206, 250);
    private static readonly Color SlideRailColor = Color.FromArgb(255, 255, 180, 120);
    private static readonly Color FlickColor = Color.FromArgb(255, 221, 160, 221);
    private static readonly Color RotateRColor = Color.FromArgb(255, 255, 182, 193);
    private static readonly Color RotateLColor = Color.FromArgb(255, 176, 196, 222);
    private static readonly Color MineColor = Color.FromArgb(255, 240, 128, 128);
    private static readonly Color MergedColor = Color.FromArgb(255, 255, 255, 255);
    private static readonly Color LinkLineColor = Color.FromArgb(180, 128, 128, 128);
    private static readonly Color StrokeColor = Color.FromArgb(255, 0, 0, 0);

    private const float DiamondSize = 6f;
    private const float StrokeWidth = 1.5f;
    private const float LinkLineWidth = 2f;
    private const float MergedRectHeight = 8f;
    private const float ClickTolerance = 10f;
    private const float MergeThresholdPixels = 20f;
    private const float LinkThresholdPixels = 100f;

    private DispatcherTimer _redrawDebounceTimer;

    private double _lastDrawnStartTime = -1;
    private double _lastDrawnEndTime = -1;
    private double _lastDrawnPixelsPerSecond = -1;
    private float _lastDrawnWidth = -1;
    private float _lastDrawnHeight = -1;

    private List<NotePreviewData> _cachedNotes = new();
    private bool _notesCacheDirty = true;

    public Action<float, float> SizeChangedCallback { get; set; }
    public event EventHandler<float> NoteClicked;

    public TrackPreviewTrack()
    {
        InitializeComponent();

        _redrawDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(8)
        };
        _redrawDebounceTimer.Tick += OnRedrawDebounce;

        Unloaded += OnUnloaded;
    }

    public void Initialize(EcsWorldService ecsWorld, HierarchyService hierarchyService, InspectorService inspectorService)
    {
        _ecsWorld = ecsWorld;
        _hierarchyService = hierarchyService;
        _inspectorService = inspectorService;

        if (_hierarchyService != null)
        {
            _hierarchyService.HierarchyChanged += OnHierarchyChanged;
        }

        if (_inspectorService != null)
        {
            _propertyChangedSubscription = _inspectorService.OnPropertyChanged
                .Subscribe(OnPropertyChanged);
        }

        _notesCacheDirty = true;
        UpdateVisibility();
        _isInitialized = true;
    }

    private void OnPropertyChanged(PropertyChangedEvent e)
    {
        if (e is { Name: "Timing", Property: "TargetTime" })
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _notesCacheDirty = true;
                RequestRedraw();
            });
        }
    }

    private void OnHierarchyChanged(object sender, HierarchyChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _notesCacheDirty = true;
            UpdateVisibility();
            PreviewCanvas.Invalidate();
        });
    }

    private void UpdateVisibility()
    {
        RefreshNotesCache();
        bool hasData = _cachedNotes.Count > 0;
        NoDataText.Visibility = hasData ? Visibility.Collapsed : Visibility.Visible;
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
        _notesCacheDirty = true;
        InvalidateDrawCache();
        RequestRedraw();
    }

    private void ForceRedraw()
    {
        _redrawDebounceTimer.Stop();
        InvalidateDrawCache();
        PreviewCanvas.Invalidate();
    }

    private void RequestRedraw()
    {
        _redrawDebounceTimer.Stop();
        _redrawDebounceTimer.Start();
    }

    private void OnRedrawDebounce(object sender, object e)
    {
        _redrawDebounceTimer.Stop();
        PreviewCanvas.Invalidate();
    }

    private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _canvasWidth = (float)e.NewSize.Width;
        _canvasHeight = (float)e.NewSize.Height;
        SizeChangedCallback?.Invoke(_canvasWidth, _canvasHeight);
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

        if (_hierarchyService != null)
        {
            _hierarchyService.HierarchyChanged -= OnHierarchyChanged;
        }

        _propertyChangedSubscription?.Dispose();
        _propertyChangedSubscription = null;
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
        if (_ecsWorld == null || _cachedNotes.Count == 0)
        {
            return;
        }

        var point = e.GetCurrentPoint(PreviewCanvas);

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

        float clickedTime = -1f;
        float minDistance = float.MaxValue;

        foreach (var note in _cachedNotes)
        {
            if (note.TimeSeconds < 0 || note.TimeSeconds < Math.Max(0, _viewStartTime) || note.TimeSeconds > _viewEndTime)
            {
                continue;
            }

            float x = TimeToX(note.TimeSeconds);
            float distance = Math.Abs(clickX - x);

            if (distance < minDistance && distance <= ClickTolerance + DiamondSize)
            {
                minDistance = distance;
                clickedTime = note.TimeSeconds;
            }
        }

        if (clickedTime >= 0)
        {
            NoteClicked?.Invoke(this, clickedTime);
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

        if (_notesCacheDirty)
        {
            RefreshNotesCache();
        }

        if (_cachedNotes.Count == 0)
        {
            return;
        }

        DrawNotes(ds);

        _lastDrawnStartTime = _viewStartTime;
        _lastDrawnEndTime = _viewEndTime;
        _lastDrawnPixelsPerSecond = _pixelsPerSecond;
        _lastDrawnWidth = _canvasWidth;
        _lastDrawnHeight = _canvasHeight;
    }

    private void RefreshNotesCache()
    {
        _cachedNotes.Clear();

        if (_ecsWorld == null)
        {
            _notesCacheDirty = false;
            return;
        }

        var timingPool = _ecsWorld.GetPool<Timing>();
        var meshPool = _ecsWorld.GetPool<MeshRenderer>();

        foreach (var entityId in timingPool.GetEntities())
        {
            var timing = timingPool.Get(entityId);
            var mesh = meshPool.Get(entityId);

            if (timing == null || mesh == null)
            {
                continue;
            }

            if (mesh.Type == NoteTypeEnum.Guiding || mesh.Type == NoteTypeEnum.Empty)
            {
                continue;
            }
            if (_hierarchyService != null &&
                _hierarchyService.NodeMapping.TryGetValue(entityId, out var node) &&
                node.IsGroup)
            {
                continue;
            }

            float timeSeconds = timing.TargetMin * 60f + timing.TargetSecond + timing.TargetTick / 1000f;

            _cachedNotes.Add(new NotePreviewData
            {
                TimeSeconds = timeSeconds,
                Type = mesh.Type
            });
        }
        _cachedNotes.Sort((a, b) => a.TimeSeconds.CompareTo(b.TimeSeconds));
        _notesCacheDirty = false;
    }

    private void DrawNotes(CanvasDrawingSession ds)
    {
        float centerY = _canvasHeight / 2f;
        var visibleNotes = GetVisibleNotes();
        if (visibleNotes.Count == 0)
        {
            return;
        }
        var (mergeGroups, linkPairs) = AnalyzeNoteRelationships(visibleNotes);
        foreach (var (startNote, endNote) in linkPairs)
        {
            DrawLinkLine(ds, startNote, endNote, centerY);
        }
        foreach (var group in mergeGroups)
        {
            if (group.Count >= 2)
            {
                DrawMergedRect(ds, group, centerY);
            }
        }
        var mergedNoteSet = new HashSet<NotePreviewData>();
        foreach (var group in mergeGroups)
        {
            if (group.Count >= 2)
            {
                foreach (var note in group)
                {
                    mergedNoteSet.Add(note);
                }
            }
        }

        foreach (var note in visibleNotes)
        {
            if (!mergedNoteSet.Contains(note))
            {
                DrawNoteDiamond(ds, note, centerY);
            }
        }
    }

    private List<NotePreviewData> GetVisibleNotes()
    {
        var result = new List<NotePreviewData>();
        float margin = (float)(DiamondSize * 2 / _pixelsPerSecond);
        float effectiveStartTime = Math.Max(0, (float)_viewStartTime);

        foreach (var note in _cachedNotes)
        {
            if (note.TimeSeconds >= effectiveStartTime - margin &&
                note.TimeSeconds <= _viewEndTime + margin)
            {
                result.Add(note);
            }
        }

        return result;
    }

    private (List<List<NotePreviewData>> mergeGroups, List<(NotePreviewData, NotePreviewData)> linkPairs)
        AnalyzeNoteRelationships(List<NotePreviewData> notes)
    {
        var mergeGroups = new List<List<NotePreviewData>>();
        var linkPairs = new List<(NotePreviewData, NotePreviewData)>();

        if (notes.Count == 0)
        {
            return (mergeGroups, linkPairs);
        }
        float mergeThresholdTime = (float)(MergeThresholdPixels / _pixelsPerSecond);
        float linkThresholdTime = (float)(LinkThresholdPixels / _pixelsPerSecond);

        var currentMergeGroup = new List<NotePreviewData> { notes[0] };
        var processedForLink = new HashSet<int>();

        for (int i = 1; i < notes.Count; i++)
        {
            float timeDiff = notes[i].TimeSeconds - notes[i - 1].TimeSeconds;

            if (timeDiff <= mergeThresholdTime)
            {
                currentMergeGroup.Add(notes[i]);
            }
            else
            {
                if (currentMergeGroup.Count >= 2)
                {
                    mergeGroups.Add(new List<NotePreviewData>(currentMergeGroup));
                }
                if (timeDiff <= linkThresholdTime && timeDiff > mergeThresholdTime)
                {
                    linkPairs.Add((notes[i - 1], notes[i]));
                }

                currentMergeGroup.Clear();
                currentMergeGroup.Add(notes[i]);
            }
        }
        if (currentMergeGroup.Count >= 2)
        {
            mergeGroups.Add(currentMergeGroup);
        }

        return (mergeGroups, linkPairs);
    }

    private void DrawLinkLine(CanvasDrawingSession ds, NotePreviewData startNote, NotePreviewData endNote, float centerY)
    {
        float startX = TimeToX(startNote.TimeSeconds);
        float endX = TimeToX(endNote.TimeSeconds);

        if (endX < 0 || startX > _canvasWidth)
        {
            return;
        }

        startX = Math.Max(-DiamondSize, startX);
        endX = Math.Min(_canvasWidth + DiamondSize, endX);

        ds.DrawLine(startX, centerY, endX, centerY, LinkLineColor, LinkLineWidth);
    }

    private void DrawMergedRect(CanvasDrawingSession ds, List<NotePreviewData> group, float centerY)
    {
        if (group.Count < 2)
        {
            return;
        }

        float startX = TimeToX(group[0].TimeSeconds);
        float endX = TimeToX(group[^1].TimeSeconds);

        if (endX < -DiamondSize || startX > _canvasWidth + DiamondSize)
        {
            return;
        }

        float halfHeight = MergedRectHeight / 2f;
        float pointSize = DiamondSize;

        using var pathBuilder = new CanvasPathBuilder(ds);
        pathBuilder.BeginFigure(startX - pointSize, centerY);
        pathBuilder.AddLine(startX, centerY - halfHeight);
        pathBuilder.AddLine(endX, centerY - halfHeight);
        pathBuilder.AddLine(endX + pointSize, centerY);
        pathBuilder.AddLine(endX, centerY + halfHeight);
        pathBuilder.AddLine(startX, centerY + halfHeight);

        pathBuilder.EndFigure(CanvasFigureLoop.Closed);

        using var geometry = CanvasGeometry.CreatePath(pathBuilder);
        ds.FillGeometry(geometry, MergedColor);
        ds.DrawGeometry(geometry, StrokeColor, StrokeWidth);
    }

    private void DrawNoteDiamond(CanvasDrawingSession ds, NotePreviewData note, float centerY)
    {
        float x = TimeToX(note.TimeSeconds);

        if (x < -DiamondSize || x > _canvasWidth + DiamondSize)
        {
            return;
        }

        Color fillColor = GetNoteColor(note.Type);

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

    private static Color GetNoteColor(NoteTypeEnum type)
    {
        return type switch
        {
            NoteTypeEnum.Click => ClickColor,
            NoteTypeEnum.Slide => SlideRailColor,
            NoteTypeEnum.Rail => SlideRailColor,
            NoteTypeEnum.Flick => FlickColor,
            NoteTypeEnum.RotateR => RotateRColor,
            NoteTypeEnum.RotateL => RotateLColor,
            NoteTypeEnum.Mine => MineColor,
            NoteTypeEnum.Catch => ClickColor,
            _ => ClickColor
        };
    }

    private struct NotePreviewData
    {
        public float TimeSeconds;
        public NoteTypeEnum Type;
    }
}