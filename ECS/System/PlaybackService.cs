using Cysharp.Threading.Tasks;
using FluentDesigner.ECS.Components;
using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FluentDesigner.ECS.System;

public sealed class PlaybackService : Service, IDisposable
{
    private const float UpdateIntervalMs = 2f;
    private readonly Stopwatch _stopwatch = new();
    private DispatcherTimer _timer;
    private PlaybackState _state = PlaybackState.Stopped;
    private float _editorTimeSeconds;
    private float _currentTimeSeconds;
    private float _totalTimeSeconds;
    private float _baseSpeedMultiplier = 1f;
    private string _cachedDescriptionJson;
    private float? _loopInPoint;
    private float? _loopOutPoint;
    private bool _isLoopEnabled;

    public PlaybackState State => _state;
    public float CurrentTime => _state == PlaybackState.Stopped ? _editorTimeSeconds : _currentTimeSeconds;
    public float TotalTime => _totalTimeSeconds;

    private PlaybackCacheData _cachedData;
    private List<PlaybackNoteInstance> _noteInstances;
    private CancellationTokenSource _cts;

    private DescriptionFileExporter _exporter;
    private PlaybackCacheParser _parser;

    private AudioPlaybackManager _audioManager;
    private ProjectPropertiesService _projectPropertiesService;
    private WaveformService _waveformService;
    private string _loadedAudioPath;

    public float BaseSpeedMultiplier
    {
        get => _baseSpeedMultiplier;
        set => _baseSpeedMultiplier = Math.Clamp((float)Math.Round(value, 1), 1.0f, 8.0f);
    }

    public float EditorTime
    {
        get => _editorTimeSeconds;
        set => _editorTimeSeconds = value;
    }

    public event EventHandler<float> TimeUpdated;
    public event EventHandler<PlaybackState> StateChanged;
    public event EventHandler PlaybackEnded;
    public event EventHandler<PlaybackNoteInstance> NoteSpawned;
    public event EventHandler<PlaybackNoteInstance> NoteDestroyed;
    public event EventHandler PlayModeEntered;
    public event EventHandler PlayModeExited;
    public event EventHandler<string> PlayModeError;
    public event EventHandler<string> ProgressChanged;
    public event EventHandler<float> SeekPreviewRequested;
    public event EventHandler<string> AudioDeviceChanged;
    public event EventHandler AudioDeviceLost;

    private ExchangeFileExporter _snapshotExporter;
    private ExchangeFileImporter _snapshotImporter;
    private HierarchyService _hierarchyService;
    private EcsWorldService _ecsWorld;
    private string _sceneSnapshotJson;
    private EditorSelectionSnapshot _savedEditorSnapshot;
    private bool _usedAsyncMode;
    private const int AsyncThresholdNoteCount = 256;
    private const int AsyncThresholdBatchSize = 16;
    private float _playStartBaseTime;

    public bool IsInPlayMode => _state != PlaybackState.Stopped;
    public bool IsLoopEnabled => _isLoopEnabled && _loopInPoint.HasValue && _loopOutPoint.HasValue;
    public float? LoopInPoint => _loopInPoint;
    public float? LoopOutPoint => _loopOutPoint;
    public IReadOnlyList<PlaybackNoteInstance> NoteInstances => _noteInstances;

    public override void Initialize()
    {
        base.Initialize();

        var framework = GetFramework();
        _ecsWorld = framework.GetService<EcsWorldService>();
        _hierarchyService = framework.GetService<HierarchyService>();
        _projectPropertiesService = framework.GetService<ProjectPropertiesService>();
        _waveformService = framework.GetService<WaveformService>();
        var ecsWorld = framework.GetService<EcsWorldService>();
        var hierarchyService = framework.GetService<HierarchyService>();
        var projectProperties = _projectPropertiesService.ProjectProperties;
        var textureService = framework.GetService<TextureService>();

        _exporter = new DescriptionFileExporter(ecsWorld, hierarchyService, projectProperties);
        _parser = new PlaybackCacheParser();

        _snapshotExporter = new ExchangeFileExporter(_ecsWorld, _hierarchyService, projectProperties);
        _snapshotImporter = new ExchangeFileImporter(_ecsWorld, _hierarchyService, projectProperties, textureService);

        _audioManager = new AudioPlaybackManager();
        _audioManager.DeviceChanged += OnAudioDeviceChanged;
        _audioManager.DeviceLost += OnAudioDeviceLost;
        _audioManager.PlaybackError += OnAudioPlaybackError;

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(UpdateIntervalMs)
        };
        _timer.Tick += OnTimerTick;
    }

    private float CalculateMaxDuration(float noteMaxTime)
    {
        float audioDuration = 0f;
        float csvMaxTime = 0f;
        if (_waveformService?.Cache?.IsLoaded == true)
        {
            audioDuration = (float)_waveformService.Cache.Duration.TotalSeconds;
        }
        csvMaxTime = _projectPropertiesService?.Statistics?.CsvMaxTimeSeconds ?? 0f;
        return Math.Max(Math.Max(audioDuration, csvMaxTime), noteMaxTime);
    }

    public bool LoadAudioForPlayback(string audioFilePath)
    {
        if (string.IsNullOrEmpty(audioFilePath) || !File.Exists(audioFilePath))
        {
            return false;
        }

        _loadedAudioPath = audioFilePath;
        return _audioManager.LoadAudio(audioFilePath);
    }

    public void SetLoopRegion(float? inPoint, float? outPoint)
    {
        if (inPoint.HasValue && outPoint.HasValue && inPoint.Value > outPoint.Value)
        {
            _loopInPoint = outPoint;
            _loopOutPoint = inPoint;
        }
        else
        {
            _loopInPoint = inPoint;
            _loopOutPoint = outPoint;
        }
    }

    public void ClearLoopRegion()
    {
        _loopInPoint = null;
        _loopOutPoint = null;
    }

    public void SetLoopEnabled(bool enabled)
    {
        _isLoopEnabled = enabled;
    }

    public bool IsTimeInLoopRegion(float time)
    {
        if (!_loopInPoint.HasValue || !_loopOutPoint.HasValue)
        {
            return false;
        }
        return time >= _loopInPoint.Value && time <= _loopOutPoint.Value;
    }

    private void OnAudioDeviceChanged(object sender, string deviceName)
    {
        AudioDeviceChanged?.Invoke(this, deviceName);
    }

    private void OnAudioDeviceLost(object sender, EventArgs e)
    {
        if (_state == PlaybackState.Playing)
        {
            Pause();
            AudioDeviceLost?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnAudioPlaybackError(object sender, string error)
    {
        PlayModeError?.Invoke(this, $"Audio error: {error}");
    }

    public async UniTask EnterPlayModeAsync()
    {
        if (_state != PlaybackState.Stopped)
        {
            PlayModeError?.Invoke(this, "Already in play mode.");
            return;
        }

        if (!ValidateSceneNotEmpty())
        {
            return;
        }

        int noteCount = CountNotes();
        if (noteCount == 0)
        {
            PlayModeError?.Invoke(this, "No notes found in scene.");
            return;
        }

        int batchSize = noteCount / Math.Max(1, Environment.ProcessorCount / 2);
        bool shouldUseAsync = noteCount >= AsyncThresholdNoteCount && batchSize >= AsyncThresholdBatchSize;

        _usedAsyncMode = shouldUseAsync;

        if (shouldUseAsync)
        {
            await EnterPlayModeAsyncInternal();
        }
        else
        {
            EnterPlayModeSyncInternal();
        }
    }

    public async UniTask ExitPlayModeAsync()
    {
        if (_state == PlaybackState.Stopped)
        {
            return;
        }

        if (_usedAsyncMode)
        {
            await ExitPlayModeAsyncInternal();
        }
        else
        {
            ExitPlayModeSyncInternal();
        }
    }

    private bool ValidateSceneNotEmpty()
    {
        if (_hierarchyService == null ||
            _hierarchyService.Roots == null ||
            _hierarchyService.Roots.Count == 0)
        {
            PlayModeError?.Invoke(this, "Scene is empty. Please add some notes before entering play mode.");
            return false;
        }
        return true;
    }

    private int CountNotes()
    {
        int count = 0;
        foreach (var kvp in _hierarchyService.NodeMapping)
        {
            if (!kvp.Value.IsGroup)
            {
                count++;
            }
        }
        return count;
    }

    private async UniTask EnterPlayModeAsyncInternal()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _hierarchyService.SetPlaybackMode(true);

        try
        {
            ProgressChanged?.Invoke(this, "Export Json");
            _sceneSnapshotJson = _snapshotExporter.ExportToJson();
            _savedEditorSnapshot = _hierarchyService.SaveEditorSnapshot();

            ProgressChanged?.Invoke(this, "Parse Description File");
            _cachedData = await _exporter.ExportToJsonAsync(_cts.Token);

            if (_cachedData == null || _cachedData.Notes == null || _cachedData.Notes.Count == 0)
            {
                ClearSnapshotData();
                _state = PlaybackState.Stopped;
                StateChanged?.Invoke(this, _state);
                PlayModeError?.Invoke(this, "No playable notes found.");
                return;
            }

            ProgressChanged?.Invoke(this, "Generate Temporary Data");
            _noteInstances = await _parser.PrecomputePositionsAsync(
                _cachedData,
                _baseSpeedMultiplier,
                _cts.Token);

            ProgressChanged?.Invoke(this, "Ready");
            await Task.Delay(350, _cts.Token);

            FinalizeEnterPlayMode();
        }
        catch (OperationCanceledException)
        {
            ClearSnapshotData();
        }
        catch (Exception ex)
        {
            ClearSnapshotData();
            _state = PlaybackState.Stopped;
            StateChanged?.Invoke(this, _state);
            PlayModeError?.Invoke(this, $"Failed to enter play mode: {ex.Message}");
        }
    }

    private async UniTask ExitPlayModeAsyncInternal()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _hierarchyService.SetPlaybackMode(false);

        try
        {
            ProgressChanged?.Invoke(this, "Stop Playing");
            _timer.Stop();
            _stopwatch.Stop();

            ProgressChanged?.Invoke(this, "Restore Scene");
            if (!string.IsNullOrEmpty(_sceneSnapshotJson))
            {
                var importResult = await _snapshotImporter.ImportFromJsonStringAsync(
                    _sceneSnapshotJson,
                    skipProjectProperties: true,
                    _cts.Token);

                if (!importResult.Success)
                {
                    PlayModeError?.Invoke(this, $"Failed to restore scene: {importResult.ErrorMessage}");
                }
            }

            ProgressChanged?.Invoke(this, "Recover Editor Status");
            if (_savedEditorSnapshot != null)
            {
                _hierarchyService.RestoreEditorSnapshot(_savedEditorSnapshot);
            }

            ProgressChanged?.Invoke(this, "Cleaning");
            await Task.Delay(350, cancellationToken: _cts.Token);

            FinalizeExitPlayMode();
        }
        catch (OperationCanceledException)
        {
            FinalizeExitPlayMode();
        }
        catch (Exception ex)
        {
            FinalizeExitPlayMode();
            PlayModeError?.Invoke(this, $"Error exiting play mode: {ex.Message}");
        }
    }

    private void EnterPlayModeSyncInternal()
    {
        try
        {
            ProgressChanged?.Invoke(this, "Export Json");
            _sceneSnapshotJson = _snapshotExporter.ExportToJson();
            _savedEditorSnapshot = _hierarchyService.SaveEditorSnapshot();

            ProgressChanged?.Invoke(this, "Parse Description File");
            _cachedData = _exporter.ExportPlaybackCacheSync();

            if (_cachedData == null || _cachedData.Notes == null || _cachedData.Notes.Count == 0)
            {
                ClearSnapshotData();
                _state = PlaybackState.Stopped;
                StateChanged?.Invoke(this, _state);
                PlayModeError?.Invoke(this, "No playable notes found.");
                return;
            }

            ProgressChanged?.Invoke(this, "Generate Temporary Data");
            _noteInstances = _parser.PrecomputePositionsSync(_cachedData, _baseSpeedMultiplier);

            ProgressChanged?.Invoke(this, "Ready");

            FinalizeEnterPlayMode();
        }
        catch (Exception ex)
        {
            ClearSnapshotData();
            _state = PlaybackState.Stopped;
            StateChanged?.Invoke(this, _state);
            PlayModeError?.Invoke(this, $"Failed to enter play mode: {ex.Message}");
        }
    }

    private void ExitPlayModeSyncInternal()
    {
        try
        {
            ProgressChanged?.Invoke(this, "Stop playing");
            _timer.Stop();
            _stopwatch.Stop();

            ProgressChanged?.Invoke(this, "Restore Scene");
            if (!string.IsNullOrEmpty(_sceneSnapshotJson))
            {
                var importResult = _snapshotImporter.ImportFromJsonStringSync(
                    _sceneSnapshotJson,
                    skipProjectProperties: true);

                if (!importResult.Success)
                {
                    PlayModeError?.Invoke(this, $"Failed to restore scene: {importResult.ErrorMessage}");
                }
            }

            ProgressChanged?.Invoke(this, "Recover Editor Status");
            if (_savedEditorSnapshot != null)
            {
                _hierarchyService.RestoreEditorSnapshot(_savedEditorSnapshot);
            }

            ProgressChanged?.Invoke(this, "Cleaning");

            FinalizeExitPlayMode();
        }
        catch (Exception ex)
        {
            FinalizeExitPlayMode();
            PlayModeError?.Invoke(this, $"Error exiting play mode: {ex.Message}");
        }
    }

    private void FinalizeEnterPlayMode()
    {
        float noteMaxTime = _cachedData.TotalTime;
        _totalTimeSeconds = CalculateMaxDuration(noteMaxTime);
        _currentTimeSeconds = 0f;

        foreach (var note in _noteInstances)
        {
            note.IsSpawned = false;
            note.IsDestroyed = false;
        }

        if (!string.IsNullOrEmpty(_waveformService?.CurrentFilePath))
        {
            LoadAudioForPlayback(_waveformService.CurrentFilePath);
        }

        _state = PlaybackState.Paused;
        StateChanged?.Invoke(this, _state);
        TimeUpdated?.Invoke(this, _currentTimeSeconds);
        PlayModeEntered?.Invoke(this, EventArgs.Empty);
    }

    private void FinalizeExitPlayMode()
    {
        _audioManager?.Stop();
        _audioManager?.UnloadAudio();
        _loadedAudioPath = null;

        ClearPlaybackData();
        ClearSnapshotData();

        _state = PlaybackState.Stopped;
        StateChanged?.Invoke(this, _state);
        PlayModeExited?.Invoke(this, EventArgs.Empty);
    }

    private void ClearSnapshotData()
    {
        _sceneSnapshotJson = null;
        _savedEditorSnapshot = null;
    }

    private void ClearPlaybackData()
    {
        _cachedData = null;
        _noteInstances = null;
        _currentTimeSeconds = 0f;
        _totalTimeSeconds = 0f;
    }

    public async UniTask RecalculatePositionsAsync()
    {
        if (_cachedData == null || _state == PlaybackState.Stopped)
        {
            return;
        }

        bool wasPlaying = _state == PlaybackState.Playing;
        if (wasPlaying)
        {
            Pause();
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        try
        {
            _noteInstances = await _parser.PrecomputePositionsAsync(
                _cachedData,
                _baseSpeedMultiplier,
                _cts.Token);
            UpdateNoteStatesForTime(_currentTimeSeconds);

            if (wasPlaying)
            {
                Play();
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Play()
    {
        if (_state == PlaybackState.Stopped)
        {
            return;
        }

        if (_state == PlaybackState.Playing)
        {
            return;
        }

        _state = PlaybackState.Playing;
        _playStartBaseTime = _currentTimeSeconds;
        _stopwatch.Restart();
        _timer.Start();
        if (_audioManager?.HasAudio == true)
        {
            _audioManager.Seek(_currentTimeSeconds);
            _audioManager.Play();
        }
        UpdateNoteStatesForTime(_currentTimeSeconds);
        StateChanged?.Invoke(this, _state);
    }

    public void Pause()
    {
        if (_state != PlaybackState.Playing)
        {
            return;
        }

        _state = PlaybackState.Paused;
        _stopwatch.Stop();
        _timer.Stop();
        _audioManager?.Pause();
        StateChanged?.Invoke(this, _state);
    }

    public void Stop()
    {
        _state = PlaybackState.Paused;
        _timer.Stop();
        _stopwatch.Stop();
        _audioManager?.Stop();
        StateChanged?.Invoke(this, _state);
        TimeUpdated?.Invoke(this, _currentTimeSeconds);
    }

    public void Reset()
    {
        if (_state == PlaybackState.Stopped)
        {
            return;
        }

        bool wasPlaying = _state == PlaybackState.Playing;
        if (wasPlaying)
        {
            _timer.Stop();
            _stopwatch.Stop();
        }

        _currentTimeSeconds = 0f;
        _audioManager?.Seek(0f);

        if (_noteInstances != null)
        {
            foreach (var note in _noteInstances)
            {
                if (note.IsSpawned)
                {
                    NoteDestroyed?.Invoke(this, note);
                }
                note.IsSpawned = false;
                note.IsDestroyed = false;
            }
        }

        ClearLoopRegion();
        _state = PlaybackState.Paused;
        StateChanged?.Invoke(this, _state);
        TimeUpdated?.Invoke(this, _currentTimeSeconds);
    }

    public void Restart()
    {
        Reset();
        Play();
    }

    public void Seek(float timeSeconds)
    {
        if (_state == PlaybackState.Stopped)
        {
            return;
        }

        _currentTimeSeconds = Math.Clamp(timeSeconds, 0f, _totalTimeSeconds);
        _audioManager?.Seek(_currentTimeSeconds);
        if (_state == PlaybackState.Playing)
        {
            _playStartBaseTime = _currentTimeSeconds;
            _stopwatch.Restart();
        }
        if (_state == PlaybackState.Paused)
        {
            _stopwatch.Stop();
            _stopwatch.Reset();
            _playStartBaseTime = _currentTimeSeconds;
        }
        UpdateNoteStatesForTime(_currentTimeSeconds);
        TimeUpdated?.Invoke(this, _currentTimeSeconds);
        SeekPreviewRequested?.Invoke(this, _currentTimeSeconds);
    }

    public void PreviewSeek(float timeSeconds)
    {
        if (_state == PlaybackState.Stopped)
        {
            return;
        }

        float clampedTime = Math.Clamp(timeSeconds, 0f, _totalTimeSeconds);
        _currentTimeSeconds = clampedTime;

        _audioManager?.Seek(clampedTime);

        if (_state == PlaybackState.Playing)
        {
            _playStartBaseTime = clampedTime;
            _stopwatch.Restart();
        }

        UpdateNoteStatesForTime(clampedTime);
        SeekPreviewRequested?.Invoke(this, clampedTime);
    }

    private void OnTimerTick(object s, object e)
    {
        if (_state != PlaybackState.Playing)
        {
            return;
        }

        _currentTimeSeconds = _playStartBaseTime + (float)_stopwatch.Elapsed.TotalSeconds;
        if (IsLoopEnabled && _loopOutPoint.HasValue && _loopInPoint.HasValue)
        {
            if (_currentTimeSeconds >= _loopOutPoint.Value && _playStartBaseTime >= _loopInPoint.Value)
            {
                SeekToLoopInPoint();
                return;
            }
        }

        if (_currentTimeSeconds >= _totalTimeSeconds)
        {
            _currentTimeSeconds = _totalTimeSeconds;
            Pause();
            PlaybackEnded?.Invoke(this, EventArgs.Empty);
        }

        UpdateNoteStatesForTime(_currentTimeSeconds);
        TimeUpdated?.Invoke(this, _currentTimeSeconds);
    }

    private void SeekToLoopInPoint()
    {
        if (!_loopInPoint.HasValue)
        {
            return;
        }

        _currentTimeSeconds = _loopInPoint.Value;
        _playStartBaseTime = _currentTimeSeconds;
        _stopwatch.Restart();

        _audioManager?.Seek(_currentTimeSeconds);

        if (_noteInstances != null)
        {
            foreach (var note in _noteInstances)
            {
                if (_loopInPoint.HasValue && _loopOutPoint.HasValue &&
                    note.TargetTime >= _loopInPoint.Value && note.TargetTime <= _loopOutPoint.Value)
                {
                    if (note.IsSpawned)
                    {
                        NoteDestroyed?.Invoke(this, note);
                    }
                    note.IsSpawned = false;
                    note.IsDestroyed = false;
                }
            }
        }

        UpdateNoteStatesForTime(_currentTimeSeconds);
        TimeUpdated?.Invoke(this, _currentTimeSeconds);
    }

    private void UpdateNoteStatesForTime(float time)
    {
        if (_noteInstances == null)
        {
            return;
        }

        foreach (var note in _noteInstances)
        {
            bool shouldBeVisible = time >= note.StartTime && time < note.TargetTime;

            if (shouldBeVisible)
            {
                if (!note.IsSpawned || note.IsDestroyed)
                {
                    note.IsSpawned = true;
                    note.IsDestroyed = false;
                    NoteSpawned?.Invoke(this, note);
                }
            }
            else if (time >= note.TargetTime)
            {
                if (note is { IsSpawned: true, IsDestroyed: false })
                {
                    note.IsDestroyed = true;
                    NoteDestroyed?.Invoke(this, note);
                }
            }
            else
            {
                if (note.IsSpawned)
                {
                    note.IsSpawned = false;
                    note.IsDestroyed = false;
                    NoteDestroyed?.Invoke(this, note);
                }
            }
        }
    }

    public (float x, float y, float z) GetNotePosition(PlaybackNoteInstance note)
    {
        return PlaybackCacheParser.InterpolatePosition(note.PositionTimeline, _currentTimeSeconds);
    }

    public IEnumerable<(PlaybackNoteInstance note, float x, float y, float z)> GetVisibleNotes()
    {
        if (_noteInstances == null)
        {
            yield break;
        }

        foreach (var note in _noteInstances)
        {
            if (note.IsSpawned && !note.IsDestroyed)
            {
                var pos = GetNotePosition(note);
                yield return (note, pos.x, pos.y, pos.z);
            }
        }
    }

    public override void Shutdown()
    {
        _cts?.Cancel();
        _timer?.Stop();
        _stopwatch.Stop();

        _audioManager?.Dispose();
        _audioManager = null;

        ClearPlaybackData();
        ClearSnapshotData();
        base.Shutdown();
    }

    public void Dispose() => Shutdown();
}
public enum PlaybackState
{
    Stopped,
    Playing,
    Paused
}

public sealed class PlaybackCacheData
{
    public string Version { get; set; }
    public DateTime ExportTime { get; set; }
    public float TotalTime { get; set; }
    public List<PlaybackNoteData> Notes { get; set; }
}

public sealed class PlaybackNoteData
{
    public int EntityId { get; set; }
    public string Name { get; set; }
    public int RailIndex { get; set; }
    public float Radius { get; set; } = 7.5f;
    public NoteTypeEnum NoteType { get; set; }
    public float StartTime { get; set; }
    public float TargetTime { get; set; }

    public DescriptionRotationData Rotation { get; set; }
    public DescriptionVelocitySampleData VelocitySamples { get; set; }
}

public sealed class PlaybackNoteInstance
{
    public int EntityId { get; set; }
    public string Name { get; set; }
    public int RailIndex { get; set; }
    public float Radius { get; set; } = 7.5f;
    public NoteTypeEnum NoteType { get; set; }
    public float StartTime { get; set; }
    public float TargetTime { get; set; }
    public DescriptionRotationData Rotation { get; set; }

    public List<PositionTimePoint> PositionTimeline { get; set; }
    public bool IsSpawned { get; set; }
    public bool IsDestroyed { get; set; }
}

public struct PositionTimePoint
{
    public float Time { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
}