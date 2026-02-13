using Cysharp.Threading.Tasks;
using FluentDesigner.ECS.Components;
using R3;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using Windows.Storage.Pickers;
using WinRT.Interop;
using Microsoft.UI.Dispatching;
using NAudio.Wave;

namespace FluentDesigner.ECS.System
{
    public sealed class InspectorController : IDisposable
    {
        private readonly IAyaMvcFramework _framework;
        private readonly InspectorService _inspectorService;
        private readonly EcsWorldService _ecsWorld;
        private readonly IDisposable _modeSubscription;
        private readonly HierarchyService _hierarchyService;
        private readonly TextureService _textureService;
        private readonly SceneLayoutService _sceneLayoutService;
        private string _lastImportedExchangePath;

        
        public event EventHandler<InspectorMode> ModeChanged;
        public event EventHandler<IEnumerable<InspectorComponentInfo>> ComponentsUpdated;
        public event EventHandler TransformUpdated;
        public event EventHandler<ImportResult> ImportCompleted;
        public event EventHandler<string> ImportFailed;
        public event EventHandler<string> ExportCompleted;
        public event EventHandler<string> ExportFailed;
        public event EventHandler<string> WavImportStarted;
        public event EventHandler<string> WavImportCompleted;
        public event EventHandler<string> CsvImportStarted;
        public event EventHandler<string> CsvImportCompleted;
        public event EventHandler<CsvImportResult> CsvImportFailed;

        public FileIOState FileIO { get; set; }
        public InspectorMode CurrentMode => _inspectorService.CurrentMode.CurrentValue;
        public int CurrentEntityId => _inspectorService.CurrentId.CurrentValue;
        public ProjectProperties ProjectProperties => _inspectorService.ProjectProperties;
        public ProjectStatistics Statistics => _inspectorService.Statistics;
        public bool HasImportedExchangePath => !string.IsNullOrEmpty(_lastImportedExchangePath);
        public string LastImportedExchangePath => _lastImportedExchangePath;


        public InspectorController(IAyaMvcFramework framework)
        {
            _framework = framework;
            _inspectorService = framework.GetService<InspectorService>();
            _inspectorService.Initialize();
            _ecsWorld = framework.GetService<EcsWorldService>();
            _hierarchyService = framework.GetService<HierarchyService>();
            _textureService = framework.GetService<TextureService>();
            _sceneLayoutService = framework.GetService<SceneLayoutService>();
            _modeSubscription = _inspectorService.CurrentMode.Subscribe(OnModeChanged);
            FileIO = _inspectorService.FileIO;
        }

        private void OnModeChanged(InspectorMode mode)
        {
            ModeChanged?.Invoke(this, mode);
            RefreshComponents();
        }

        public void RefreshComponents()
        {
            var components = _inspectorService.GetVisibleComponent();
            ComponentsUpdated?.Invoke(this, components);
        }

        public void RefreshStatistics()
        {
            _inspectorService.RefreshStatistics();
        }

        public Transform GetTransform()
        {
            var entityId = CurrentEntityId;
            return entityId == -1 ? null : _ecsWorld.GetComponent<Transform>(entityId);
        }

        public MeshRenderer GetMeshRenderer()
        {
            var entityId = CurrentEntityId;
            return entityId == -1 ? null : _ecsWorld.GetComponent<MeshRenderer>(entityId);
        }

        public Timing GetTiming()
        {
            var entityId = CurrentEntityId;
            return entityId == -1 ? null : _ecsWorld.GetComponent<Timing>(entityId);
        }

        public Timing GetGroupTiming()
        {
            var entityId = CurrentEntityId;
            if (entityId == -1)
            {
                return null;
            }

            if (!IsCurrentEntityGroup())
            {
                return null;
            }

            _hierarchyService.CalculateGroupTiming(entityId);
            return _ecsWorld.GetComponent<Timing>(entityId);
        }

        public Description GetDescription()
        {
            var entityId = CurrentEntityId;
            return entityId == -1 ? null : _ecsWorld.GetComponent<Description>(entityId);
        }

        public VelocityCurve GetVelocityCurve()
        {
            var entityId = CurrentEntityId;
            return entityId == -1 ? null : _ecsWorld.GetComponent<VelocityCurve>(entityId);
        }

        public string GetParentName()
        {
            var entityId = CurrentEntityId;
            if (entityId == -1)
            {
                return null;
            }

            var transform = _ecsWorld.GetComponent<Transform>(entityId);
            if (transform == null || transform.Parents == -1)
            {
                return null;
            }

            if (_hierarchyService.NodeMapping.TryGetValue(transform.Parents, out var parentNode))
            {
                return parentNode.Name;
            }

            return null;
        }

        public void UpdatePosition(float x, float y, float z)
        {
            var entityId = CurrentEntityId;
            if (entityId == -1)
            {
                return;
            }

            var transform = _ecsWorld.GetComponent<Transform>(entityId);
            if (transform == null)
            {
                return;
            }

            var oldPos = transform.Position;
            transform.Position = new Position { X = x, Y = y, Z = z };
            _inspectorService.NotifyPropertyChanged("Transform", "Position", oldPos, transform.Position);
        }

        public void UpdateRotation(float x, float y, float z, float w)
        {
            var entityId = CurrentEntityId;
            if (entityId == -1)
            {
                return;
            }

            var transform = _ecsWorld.GetComponent<Transform>(entityId);
            if (transform == null)
            {
                return;
            }

            var oldRot = transform.Rotation;
            transform.Rotation = new Rotation { X = x, Y = y, Z = z, W = w };
            _inspectorService.NotifyPropertyChanged("Transform", "Rotation", oldRot, transform.Rotation);
        }

        public void UpdateScale(float x, float y, float z)
        {
            var entityId = CurrentEntityId;
            if (entityId == -1)
            {
                return;
            }

            var transform = _ecsWorld.GetComponent<Transform>(entityId);
            if (transform == null)
            {
                return;
            }

            var oldScale = transform.Scale;
            transform.Scale = new Scale { X = x, Y = y, Z = z };
            _inspectorService.NotifyPropertyChanged("Transform", "Scale", oldScale, transform.Scale);
        }

        public void UpdateRailIndex(int index)
        {
            var entityId = CurrentEntityId;
            if (entityId == -1)
            {
                return;
            }

            var transform = _ecsWorld.GetComponent<Transform>(entityId);
            if (transform == null)
            {
                return;
            }

            var oldIndex = transform.Index;
            transform.Index = new RailIndex { Index = index };
            _inspectorService.NotifyPropertyChanged("Transform", "RailIndex", oldIndex, transform.Index);

            _sceneLayoutService?.OnRailIndexUpdated(entityId);
            TransformUpdated?.Invoke(this, EventArgs.Empty);
        }

        public void UpdateRadius(float radius)
        {
            var entityId = CurrentEntityId;
            if (entityId == -1)
            {
                return;
            }

            var transform = _ecsWorld.GetComponent<Transform>(entityId);
            if (transform == null)
            {
                return;
            }

            var oldRadius = transform.Radius;
            transform.Radius = Math.Clamp(radius, 0.1f, 7.5f);
            _inspectorService.NotifyPropertyChanged("Transform", "Radius", oldRadius, transform.Radius);

            _sceneLayoutService?.OnRadiusUpdated(entityId);
            TransformUpdated?.Invoke(this, EventArgs.Empty);
        }

        public void UpdateColor(float r, float g, float b, float a)
        {
            var entityId = CurrentEntityId;
            if (entityId == -1)
            {
                return;
            }

            var renderer = _ecsWorld.GetComponent<MeshRenderer>(entityId);
            if (renderer == null)
            {
                return;
            }

            var oldColor = renderer.Color;
            renderer.Color = new Vector4(r, g, b, a);
            _inspectorService.NotifyPropertyChanged("MeshRenderer", "Color", oldColor, renderer.Color);
        }

        public void UpdateNoteType(NoteTypeEnum type)
        {
            var entityId = CurrentEntityId;
            if (entityId == -1)
            {
                return;
            }

            var renderer = _ecsWorld.GetComponent<MeshRenderer>(entityId);
            var transform = _ecsWorld.GetComponent<Transform>(entityId);
            if (renderer == null)
            {
                return;
            }

            var oldType = renderer.Type;
            _hierarchyService.UpdateNoteType(entityId, type);
            _sceneLayoutService?.OnNoteTypeChanged(entityId, type);
            if (transform != null)
            {
                TransformUpdated?.Invoke(this, EventArgs.Empty);
            }

            _inspectorService.NotifyPropertyChanged("MeshRenderer", "Type", oldType, type);
        }

        public void UpdateVisibility(bool visible)
        {
            var entityId = CurrentEntityId;
            if (entityId == -1)
            {
                return;
            }

            var renderer = _ecsWorld.GetComponent<MeshRenderer>(entityId);
            if (renderer == null)
            {
                return;
            }

            _hierarchyService.SetVisibility(entityId, visible);
            var oldValue = renderer.Visibility;
            renderer.Visibility = visible;
            _inspectorService.NotifyPropertyChanged("MeshRenderer", "Visibility", oldValue, visible);
        }

        public void UpdateSortingLayer(int layer)
        {
            var entityId = CurrentEntityId;
            if (entityId == -1)
            {
                return;
            }

            var renderer = _ecsWorld.GetComponent<MeshRenderer>(entityId);
            if (renderer == null)
            {
                return;
            }

            var oldValue = renderer.SortingLayer;
            renderer.SortingLayer = layer;
            _inspectorService.NotifyPropertyChanged("MeshRenderer", "SortingLayer", oldValue, layer);
        }

        public void UpdateOrderInLayer(int order)
        {
            var entityId = CurrentEntityId;
            if (entityId == -1)
            {
                return;
            }

            var renderer = _ecsWorld.GetComponent<MeshRenderer>(entityId);
            if (renderer == null)
            {
                return;
            }

            var oldValue = renderer.OrderInLayer;
            renderer.OrderInLayer = order;
            _inspectorService.NotifyPropertyChanged("MeshRenderer", "OrderInLayer", oldValue, order);
        }

        public void UpdateTargetTime(float min, float second, float tick)
        {
            var entityId = CurrentEntityId;
            if (entityId == -1)
            {
                return;
            }

            var timing = _ecsWorld.GetComponent<Timing>(entityId);
            if (timing == null)
            {
                return;
            }

            float oldTargetTime = timing.TargetMin * 60f + timing.TargetSecond + timing.TargetTick / 1000f;
            float newTargetTime = min * 60f + second + tick / 1000f;
            float timeDelta = newTargetTime - oldTargetTime;

            timing.TargetMin = min;
            timing.TargetSecond = second;
            timing.TargetTick = tick;

            var velocityCurve = _ecsWorld.GetComponent<VelocityCurve>(entityId);
            if (velocityCurve != null && Math.Abs(timeDelta) > 0.0001f)
            {
                velocityCurve.OffsetAllTimes(timeDelta, newTargetTime);
            }

            _inspectorService.NotifyPropertyChanged("Timing", "TargetTime", null, (min, second, tick));
            _sceneLayoutService?.OnTimingUpdated(entityId);
            TransformUpdated?.Invoke(this, EventArgs.Empty);
        }

        public void UpdateDescription(string text)
        {
            var entityId = CurrentEntityId;
            if (entityId == -1)
            {
                return;
            }

            var description = _ecsWorld.GetComponent<Description>(entityId);
            if (description == null)
            {
                return;
            }

            var oldText = description.Text;
            description.Text = text;
            _inspectorService.NotifyPropertyChanged("Description", "Text", oldText, text);
        }

        public void UpdateVelocityCurve(VelocityCurve curve)
        {
            var entityId = CurrentEntityId;
            if (entityId == -1)
            {
                return;
            }

            var velocityCurve = _ecsWorld.GetComponent<VelocityCurve>(entityId);
            if (velocityCurve == null)
            {
                return;
            }

            var oldCurve = velocityCurve.Curve;
            velocityCurve.Curve = curve.Curve;
            _inspectorService.NotifyPropertyChanged("VelocityCurve", "Curve", oldCurve, curve.Curve);
        }

        public void AddComponent<T>() where T : class, new()
        {
            var entityId = CurrentEntityId;
            if (entityId == -1)
            {
                return;
            }

            var component = _ecsWorld.AddComponent<T>(entityId);
            _inspectorService.NotifyPropertyChanged("Entity", "AddComponent", null, typeof(T).Name);
            RefreshComponents();
        }

        public bool HasComponent<T>() where T : class, new()
        {
            var entityId = CurrentEntityId;
            if (entityId == -1)
            {
                return false;
            }

            return _ecsWorld.GetComponent<T>(entityId) != null;
        }

        public List<Type> GetAvailableComponents()
        {
            var available = new List<Type>();
            var entityId = CurrentEntityId;

            if (entityId == -1)
            {
                return available;
            }

            if (_hierarchyService.NodeMapping.TryGetValue(entityId, out var node) && node.IsGroup)
            {
                return available;
            }

            if (!HasComponent<Timing>())
            {
                available.Add(typeof(Timing));
            }

            if (!HasComponent<Description>())
            {
                available.Add(typeof(Description));
            }

            if (!HasComponent<VelocityCurve>())
            {
                available.Add(typeof(VelocityCurve));
            }

            return available;
        }

        public bool HasAllComponents() => GetAvailableComponents().Count == 0;

        public bool IsCurrentEntityGroup()
        {
            var entityId = CurrentEntityId;
            if (entityId == -1)
            {
                return false;
            }

            return _hierarchyService.NodeMapping.TryGetValue(entityId, out var node) && node.IsGroup;
        }

        public async UniTask ImportWavFile(nint windowHandle, DispatcherQueue dispatcherQueue)
        {
            try
            {
                var picker = new FileOpenPicker();
                InitializeWithWindow.Initialize(picker, windowHandle);

                picker.SuggestedStartLocation = PickerLocationId.MusicLibrary;
                picker.FileTypeFilter.Add(".wav");

                var file = await picker.PickSingleFileAsync();
                if (file == null)
                {
                    return;
                }

                var waveformService = _framework.GetService<WaveformService>();
                if (waveformService == null)
                {
                    ImportFailed?.Invoke(this, "Waveform Service not available.");
                    return;
                }

                WavImportStarted?.Invoke(this, file.Name);

                await waveformService.GenerateAllCacheLevelsAsync(
                    new AudioFileReader(file.Path),
                    dispatcherQueue,
                    CancellationToken.None,
                    file.Path);

                FileIO.WavFileName = file.Name;

                var cache = waveformService.Cache;
                if (cache != null)
                {
                    Statistics.MusicLength = FormatDuration(cache.Duration);
                }

                WavImportCompleted?.Invoke(this, file.Path);
            }
            catch (Exception ex)
            {
                ImportFailed?.Invoke(this, ex.Message);
            }
        }

        private static string FormatDuration(TimeSpan duration)
        {
            return duration.TotalHours >= 1
                ? $"{(int)duration.TotalHours:D2}:{duration.Minutes:D2}:{duration.Seconds:D2}"
                : $"{duration.Minutes:D2}:{duration.Seconds:D2}";
        }

        public async UniTask ImportCsvFile(nint windowHandle)
        {
            try
            {
                var picker = new FileOpenPicker();
                InitializeWithWindow.Initialize(picker, windowHandle);

                picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
                picker.FileTypeFilter.Add(".csv");
                picker.FileTypeFilter.Add(".txt");

                var file = await picker.PickSingleFileAsync();
                if (file == null)
                {
                    return;
                }

                var csvMarkerService = _framework.GetService<CsvMarkerService>();
                if (csvMarkerService == null)
                {
                    ImportFailed?.Invoke(this, "CSV Marker Service not available.");
                    return;
                }

                CsvImportStarted?.Invoke(this, file.Name);

                var result = await csvMarkerService.LoadFromFileAsync(file.Path);

                if (result.Success)
                {
                    FileIO.CsvFileName = file.Name;
                    Statistics.CsvMarkerCount = result.MarkerCount;
                    Statistics.CsvMaxTimeSeconds = result.MaxTimeSeconds;

                    CsvImportCompleted?.Invoke(this, file.Path);
                }
                else
                {
                    string errorMessage = result.ErrorLineNumber > 0
                        ? $"Line {result.ErrorLineNumber}: {result.ErrorMessage}"
                        : result.ErrorMessage;

                    CsvImportFailed?.Invoke(this, result);
                    ImportFailed?.Invoke(this, errorMessage);
                }
            }
            catch (Exception ex)
            {
                ImportFailed?.Invoke(this, ex.Message);
            }
        }
        public void ClearCsvMarkers()
        {
            var csvMarkerService = _framework.GetService<CsvMarkerService>();
            csvMarkerService?.ClearMarkers();

            FileIO.CsvFileName = "";
            Statistics.CsvMarkerCount = 0;
            Statistics.CsvMaxTimeSeconds = 0f;
        }

        public async UniTask ExportExchangeFileAsync(nint windowHandle)
        {
            try
            {
                var picker = new FileSavePicker();
                InitializeWithWindow.Initialize(picker, windowHandle);

                picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
                picker.SuggestedFileName = $"{ProjectProperties.Title}_exchange";
                picker.FileTypeChoices.Add("JSON Exchange File", [".json"]);

                var file = await picker.PickSaveFileAsync();
                if (file == null)
                {
                    return;
                }

                var exporter = new ExchangeFileExporter(_ecsWorld, _hierarchyService, ProjectProperties);
                await exporter.ExportToJsonAsync(file.Path);

                ExportCompleted?.Invoke(this, file.Path);
            }
            catch (Exception ex)
            {
                ExportFailed?.Invoke(this, ex.Message);
            }
        }

        public async UniTask ExportExchangeToPathAsync(string filePath)
        {
            try
            {
                var waveformService = _framework.GetService<WaveformService>();
                var csvMarkerService = _framework.GetService<CsvMarkerService>();
                var exporter = new ExchangeFileExporter(_ecsWorld, _hierarchyService, ProjectProperties, waveformService, csvMarkerService);
                await exporter.ExportToJsonAsync(filePath);
                ExportCompleted?.Invoke(this, filePath);
            }
            catch (Exception ex)
            {
                ExportFailed?.Invoke(this, ex.Message);
            }
        }

        public async UniTask QuickSaveExchangeFileAsync()
        {
            if (!string.IsNullOrEmpty(_lastImportedExchangePath))
            {
                await ExportExchangeToPathAsync(_lastImportedExchangePath);
            }
        }

        public async UniTask ExportDescriptionFileAsync(nint windowHandle)
        {
            try
            {
                var picker = new FileSavePicker();
                InitializeWithWindow.Initialize(picker, windowHandle);

                picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
                picker.SuggestedFileName = $"{ProjectProperties.Title}_description";
                picker.FileTypeChoices.Add("JSON Description File", [".json"]);

                var file = await picker.PickSaveFileAsync();
                if (file == null)
                {
                    return;
                }

                var exporter = new DescriptionFileExporter(_ecsWorld, _hierarchyService, ProjectProperties);
                await exporter.ExportToJsonAsync(file.Path);

                ExportCompleted?.Invoke(this, file.Path);
            }
            catch (Exception ex)
            {
                ExportFailed?.Invoke(this, ex.Message);
            }
        }

        public async UniTask ImportExchangeFileAsync(nint windowHandle)
        {
            try
            {
                var picker = new FileOpenPicker();
                InitializeWithWindow.Initialize(picker, windowHandle);
                picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
                picker.FileTypeFilter.Add(".json");
                var file = await picker.PickSingleFileAsync();
                if (file == null)
                {
                    return;
                }

                var importer = new ExchangeFileImporter(_ecsWorld, _hierarchyService, ProjectProperties, _textureService);
                var result = await importer.ImportFromJsonAsync(file.Path);
                if (result.Success)
                {
                    _lastImportedExchangePath = file.Path; 
                    ImportCompleted?.Invoke(this, result);
                    RefreshStatistics();
                }
                else
                {
                    ImportFailed?.Invoke(this, result.ErrorMessage);
                }
            }
            catch (Exception e)
            {
                ImportFailed?.Invoke(this, e.Message);
            }
        }

        public void Dispose()
        {
            _modeSubscription?.Dispose();
        }
    }
}
