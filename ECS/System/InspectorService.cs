using FluentDesigner.ECS.Components;
using R3;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace FluentDesigner.ECS.System
{
    public sealed class InspectorService : Service, IDisposable
    {
        private HierarchyService _hierarchyService;
        private EcsWorldService _ecsWorld;
        private ProjectPropertiesService _projectPropertiesService;
        private readonly ReactiveProperty<int> _currentId = new(-1);
        private readonly ReactiveProperty<InspectorMode> _currentMode = new(InspectorMode.None);
        private readonly Subject<PropertyChangedEvent> _propertyChanged = new();
        public ReadOnlyReactiveProperty<int> CurrentId => _currentId;
        public ReadOnlyReactiveProperty<InspectorMode> CurrentMode => _currentMode;
        public Observable<PropertyChangedEvent> OnPropertyChanged => _propertyChanged;
        public ProjectProperties ProjectProperties => _projectPropertiesService.ProjectProperties;
        public ProjectStatistics Statistics => _projectPropertiesService.Statistics;
        public FileIOState FileIO
        {
            get => _projectPropertiesService.FileIO;
            set => _projectPropertiesService.FileIO = value;
        }

        public override void Initialize()
        {
            base.Initialize();
            _hierarchyService = GetFramework().GetService<HierarchyService>();
            _ecsWorld = GetFramework().GetService<EcsWorldService>();
            _projectPropertiesService = GetFramework().GetService<ProjectPropertiesService>();
            _hierarchyService.SelectedEntitiesChanged += OnSelectionChanged;
        }

        public void RefreshStatistics()
        {
            Statistics.TotalObjectCount = _ecsWorld.EntityCount;
            Statistics.TotalComponentCount = CalculateTotalComponents();
        }

        private int CalculateTotalComponents()
        {
            int count = 0;
            var transPool = _ecsWorld.GetPool<Transform>();
            var meshPool = _ecsWorld.GetPool<MeshRenderer>();
            var notePool = _ecsWorld.GetPool<NoteType>();
            var timingPool = _ecsWorld.GetPool<Timing>();
            var descPool = _ecsWorld.GetPool<Description>();

            foreach (var id in transPool.GetEntities())
            {
                if (transPool.Contains(id))
                {
                    count++;
                }

                if (meshPool.Contains(id))
                {
                    count++;
                }

                if (notePool.Contains(id))
                {
                    count++;
                }

                if (timingPool.Contains(id))
                {
                    count++;
                }

                if (descPool.Contains(id))
                {
                    count++;
                }
            }
            return count;
        }

        private void OnSelectionChanged(object s, List<int> selectedIds)
        {
            if (selectedIds == null || selectedIds.Count == 0)
            {
                _currentId.Value = -1;
                _currentMode.Value = InspectorMode.None;
                return;
            }

            if (selectedIds.Count > 1)
            {
                _currentId.Value = -1;
                _currentMode.Value = InspectorMode.MultiSelect;
                return;
            }

            var first = selectedIds[0];
            _currentId.Value = first;
            if (_hierarchyService.NodeMapping.TryGetValue(first, out var node))
            {
                _currentMode.Value = node.IsGroup ? InspectorMode.Group : InspectorMode.Note;
            }
            else
            {
                _currentMode.Value = InspectorMode.None;
            }
        }

        public IEnumerable<InspectorComponentInfo> GetVisibleComponent()
        {
            var id = _currentId.Value;
            if (id == -1)
            {
                yield break;
            }

            var mode = _currentMode.Value;
            var timing = _ecsWorld.GetComponent<Timing>(id);
            if (timing != null)
            {
                yield return new InspectorComponentInfo
                {
                    Name = "Timing",
                    Type = typeof(Timing),
                    Component = timing,
                    IsVisible = true
                };
            }

            var description = _ecsWorld.GetComponent<Description>(id);
            if (description != null)
            {
                yield return new InspectorComponentInfo
                {
                    Name = "Description",
                    Type = typeof(Description),
                    Component = description,
                    IsVisible = true
                };
            }

            var transform = _ecsWorld.GetComponent<Transform>(id);
            if (transform != null)
            {
                yield return new InspectorComponentInfo
                {
                    Name = "Transform",
                    Type = typeof(Transform),
                    Component = transform,
                    IsVisible = true
                };
            }

            var meshRenderer = _ecsWorld.GetComponent<MeshRenderer>(id);
            if (meshRenderer != null)
            {
                yield return new InspectorComponentInfo
                {
                    Name = "Mesh Renderer",
                    Type = typeof(MeshRenderer),
                    Component = meshRenderer,
                    IsVisible = true
                };
            }

            var noteType = _ecsWorld.GetComponent<NoteType>(id);
            if (noteType != null)
            {
                yield return new InspectorComponentInfo
                {
                    Name = "Note Type",
                    Type = typeof(NoteType),
                    Component = noteType,
                    IsVisible = true
                };
            }
        }

        public bool IsComponentVisible<T>() where T : class, new()
        {
            var mode = _currentMode.Value;
            if (typeof(T) == typeof(Timing) || typeof(T) == typeof(Description))
            {
                return mode != InspectorMode.None;
            }

            return mode == InspectorMode.Note;
        }

        public void NotifyPropertyChanged(string name, string property, object old, object value)
        {
            _propertyChanged.OnNext(new PropertyChangedEvent(name, property, old, value));
        }

        public override void Shutdown()
        {
            if (_hierarchyService != null)
            {
                _hierarchyService.SelectedEntitiesChanged -= OnSelectionChanged;
            }

            _currentId.Dispose();
            _currentMode.Dispose();
            _propertyChanged.Dispose();
            base.Shutdown();
        }

        public void Dispose() => Shutdown();
    }

    public enum InspectorMode
    {
        None,
        Note,
        Group,
        MultiSelect
    }

    public class InspectorComponentInfo
    {
        public string Name { get; init; }
        public Type Type { get; init; }
        public object Component { get; init; }
        public bool IsVisible { get; init; }
    }

    public record PropertyChangedEvent(string Name, string Property, object Old, object New);

    public sealed class FileIOState : INotifyPropertyChanged
    {
        private string _wavFileName = "";
        private string _csvFileName = "";
        private bool _hasWavFile;
        private bool _hasCsvFile;
        private float _audioDelaySeconds;

        public string WavFileName
        {
            get => _wavFileName;
            set { _wavFileName = value; _hasWavFile = !string.IsNullOrEmpty(value); OnPropertyChanged(); OnPropertyChanged(nameof(HasWavFile)); }
        }

        public string CsvFileName
        {
            get => _csvFileName;
            set { _csvFileName = value; _hasCsvFile = !string.IsNullOrEmpty(value); OnPropertyChanged(); OnPropertyChanged(nameof(HasCsvFile)); }
        }

        public float AudioDelaySeconds
        {
            get => _audioDelaySeconds;
            set
            {
                _audioDelaySeconds = Math.Clamp(value, -2f, 2f);
                OnPropertyChanged();
            }
        }

        public bool HasWavFile => _hasWavFile;
        public bool HasCsvFile => _hasCsvFile;

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public sealed class ProjectStatistics : INotifyPropertyChanged
    {
        private int _totalObjectCount;
        private int _totalComponentCount;
        private string _musicLength = "00:00";
        private int _csvMarkerCount;
        private float _audioDurationSeconds;
        private float _csvMaxTimeSeconds;
        private float _targetTimeMaxSeconds;

        public int TotalObjectCount
        {
            get => _totalObjectCount;
            set { _totalObjectCount = value; OnPropertyChanged(); }
        }

        public int TotalComponentCount
        {
            get => _totalComponentCount;
            set { _totalComponentCount = value; OnPropertyChanged(); }
        }

        public string MusicLength
        {
            get => _musicLength;
            set { _musicLength = value; OnPropertyChanged(); }
        }

        public int CsvMarkerCount
        {
            get => _csvMarkerCount;
            set { _csvMarkerCount = value; OnPropertyChanged(); }
        }

        public float AudioDurationSeconds
        {
            get => _audioDurationSeconds;
            set { _audioDurationSeconds = value; OnPropertyChanged(); OnPropertyChanged(nameof(MaxDurationSeconds)); }
        }

        public float CsvMaxTimeSeconds
        {
            get => _csvMaxTimeSeconds;
            set { _csvMaxTimeSeconds = value; OnPropertyChanged(); OnPropertyChanged(nameof(MaxDurationSeconds)); }
        }

        public float TargetTimeMaxSeconds
        {
            get => _targetTimeMaxSeconds;
            set { _targetTimeMaxSeconds = value; OnPropertyChanged(); OnPropertyChanged(nameof(MaxDurationSeconds)); }
        }

        public float MaxDurationSeconds => Math.Max(Math.Max(_audioDurationSeconds, _csvMaxTimeSeconds), _targetTimeMaxSeconds);

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public enum DifficultyCategory
    {
        Astral,
        Orbital,
        Galaxa,
        Xenith,
        Infold,
        Unique,
        Glitch
    }

    public sealed class ProjectProperties : INotifyPropertyChanged
    {
        private string _title = "Untitled";
        private string _musicArtist = "";
        private string _chartAuthor = "";
        private float _difficultyConstant = 1.0f;
        private string _bpm = "120";
        private DifficultyCategory _difficulty = DifficultyCategory.Orbital;
        private string _description = "";

        public string Title
        {
            get => _title;
            set { _title = value; OnPropertyChanged(); }
        }

        public string MusicArtist
        {
            get => _musicArtist;
            set { _musicArtist = value; OnPropertyChanged(); }
        }

        public string ChartAuthor
        {
            get => _chartAuthor;
            set { _chartAuthor = value; OnPropertyChanged(); }
        }

        public float DifficultyConstant
        {
            get => _difficultyConstant;
            set { _difficultyConstant = MathF.Round(value, 1); OnPropertyChanged(); }
        }

        public string BPM
        {
            get => _bpm;
            set { _bpm = value; OnPropertyChanged(); }
        }

        public DifficultyCategory Difficulty
        {
            get => _difficulty;
            set { _difficulty = value; OnPropertyChanged(); }
        }

        public string Description
        {
            get => _description;
            set { _description = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
