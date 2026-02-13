using FluentDesigner.ECS.Components;
using FluentDesigner.ECS.System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.ComponentModel;
using WinRT.Interop;

namespace FluentDesigner.Control
{
    public sealed partial class InspectorView : UserControl
    {
        public InspectorController _controller;
        private MultiSelectController _multiSelectController;
        private bool _isUpdatingUI;

        private TransformComponent _transformControl;
        private MeshRendererComponent _meshRendererControl;
        private TimingComponent _timingControl;
        private DescriptionComponent _descriptionControl;
        private VelocityCurveComponent _velocityCurveControl;
        private bool _isTransformUpdatedSubscribed;
        private int _currentBoundEntityId = -1;
        private PlaybackService _playbackService;
        private bool _isPlaybackMode;

        public InspectorView()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Initialize();
            var framework = AyaMvcFramework<AyaMvcFrameworkImpl>.EntryPoint;
            _playbackService = framework.GetService<PlaybackService>();
            if (_playbackService != null)
            {
                _playbackService.PlayModeEntered += OnPlayModeEntered;
                _playbackService.PlayModeExited += OnPlayModeExited;
            }
            _controller.CsvImportFailed += OnCsvImportFailed;
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (_controller != null)
            {
                _controller.ModeChanged -= OnModeChanged;
                _controller.ProjectProperties.PropertyChanged -= OnProjectPropertiesChanged;
                _controller.Statistics.PropertyChanged -= OnStatisticsChanged;
                _controller.FileIO.PropertyChanged -= OnFileIOChanged;
                _controller.ExportCompleted -= OnExportCompleted;
                _controller.ExportFailed -= OnExportFailed;
                _controller.ImportCompleted -= OnImportCompleted;
                _controller.ImportFailed -= OnImportFailed;
                _controller.CsvImportFailed -= OnCsvImportFailed;
                if (_isTransformUpdatedSubscribed)
                {
                    _controller.TransformUpdated -= OnTransformUpdated;
                    _isTransformUpdatedSubscribed = false;
                }
            }

            if (_playbackService != null)
            {
                _playbackService.PlayModeEntered -= OnPlayModeEntered;
                _playbackService.PlayModeExited -= OnPlayModeExited;
            }
        }
        
        private void OnCsvImportFailed(object sender, CsvImportResult result)
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                var dialog = new ContentDialog
                {
                    Title = "CSV Import Failed",
                    Content = result.ErrorLineNumber > 0
                        ? $"Error at line {result.ErrorLineNumber} ({result.ErrorFieldName}):\n{result.ErrorMessage}"
                        : result.ErrorMessage,
                    CloseButtonText = "OK",
                    XamlRoot = this.XamlRoot
                };
                await dialog.ShowAsync();
            });
        }

        private void OnPlayModeEntered(object sender, System.EventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _isPlaybackMode = true;
                SetEditingEnabled(false);
            });
        }

        private void OnPlayModeExited(object sender, System.EventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _isPlaybackMode = false;
                SetEditingEnabled(true);
            });
        }

        private void OnExportCompleted(object sender, string filePath)
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                var dialog = new ContentDialog
                {
                    Title = "Export Success",
                    Content = $"File has been exported to:\n{filePath}",
                    CloseButtonText = "Confirm",
                    XamlRoot = this.XamlRoot
                };
                await dialog.ShowAsync();
            });
        }

        private void OnExportFailed(object sender, string errorMessage)
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                var dialog = new ContentDialog
                {
                    Title = "Export Failed",
                    Content = $"Error occupied:\n{errorMessage}",
                    CloseButtonText = "Confirm",
                    XamlRoot = this.XamlRoot
                };
                await dialog.ShowAsync();
            });
        }

        private void OnImportCompleted(object sender, ImportResult result)
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                var dialog = new ContentDialog
                {
                    Title = "Import Success",
                    Content = $"Successfully imported {result.ImportedEntityCount} entities ({result.ImportedGroupCount} groups).",
                    CloseButtonText = "Confirm",
                    XamlRoot = this.XamlRoot
                };
                await dialog.ShowAsync();
            });
        }

        private void OnImportFailed(object sender, string errorMessage)
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                var dialog = new ContentDialog
                {
                    Title = "Import Failed",
                    Content = $"Error occurred:\n{errorMessage}",
                    CloseButtonText = "Confirm",
                    XamlRoot = this.XamlRoot
                };
                await dialog.ShowAsync();
            });
        }

        private void Initialize()
        {
            var framework = AyaMvcFramework<AyaMvcFrameworkImpl>.EntryPoint;
            _controller = new InspectorController(framework);

            _controller.ModeChanged += OnModeChanged;
            _controller.ProjectProperties.PropertyChanged += OnProjectPropertiesChanged;
            _controller.Statistics.PropertyChanged += OnStatisticsChanged;
            _controller.FileIO.PropertyChanged += OnFileIOChanged;
            _controller.ExportCompleted += OnExportCompleted;
            _controller.ExportFailed += OnExportFailed;
            _controller.ImportCompleted += OnImportCompleted;
            _controller.ImportFailed += OnImportFailed;

            LoadProjectProperties();
            RefreshStatistics();
            UpdatePanelVisibility(InspectorMode.None);
        }

        private void OnModeChanged(object sender, InspectorMode mode)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                UpdatePanelVisibility(mode);
                if (mode == InspectorMode.Note)
                {
                    LoadNoteComponents();
                }
                else if (mode == InspectorMode.Group)
                {
                    LoadGroupComponents();
                }
                else if (mode == InspectorMode.MultiSelect)
                {
                    LoadMultiSelectPanel();
                }
            });
        }

        private void LoadMultiSelectPanel()
        {
            if (_multiSelectController == null)
            {
                var framework = AyaMvcFramework<AyaMvcFrameworkImpl>.EntryPoint;
                _multiSelectController = new MultiSelectController(framework);
            }

            MultiSelectControl.BindController(_multiSelectController);
            MultiSelectControl.SelectionModified -= OnMultiSelectModified;
            MultiSelectControl.SelectionModified += OnMultiSelectModified;
        }

        private void OnMultiSelectModified(object sender, EventArgs e)
        {
            _controller?.RefreshStatistics();
        }

        private void UpdatePanelVisibility(InspectorMode mode)
        {
            ProjectPropertiesPanel.Visibility = mode == InspectorMode.None ? Visibility.Visible : Visibility.Collapsed;
            NoteComponentsPanel.Visibility = mode == InspectorMode.Note ? Visibility.Visible : Visibility.Collapsed;
            GroupComponentsPanel.Visibility = mode == InspectorMode.Group ? Visibility.Visible : Visibility.Collapsed;
            MultiSelectPanel.Visibility = mode == InspectorMode.MultiSelect ? Visibility.Visible : Visibility.Collapsed;

            if (mode == InspectorMode.None)
            {
                _currentBoundEntityId = -1;
                RefreshStatistics();
            }
        }

        private void SetEditingEnabled(bool enabled)
        {

            TitleTextBox.IsEnabled = enabled;
            MusicArtistTextBox.IsEnabled = enabled;
            ChartAuthorTextBox.IsEnabled = enabled;
            BpmTextBox.IsEnabled = enabled;
            DifficultyConstantBox.IsEnabled = enabled;
            DifficultyComboBox.IsEnabled = enabled;
            ProjectDescriptionTextBox.IsEnabled = enabled;

            if (_transformControl != null)
            {
                _transformControl.IsEnabled = enabled;
            }

            if (_meshRendererControl != null)
            {
                _meshRendererControl.IsEnabled = enabled;
            }

            if (_timingControl != null)
            {
                _timingControl.IsEnabled = enabled;
            }

            if (_descriptionControl != null)
            {
                _descriptionControl.IsEnabled = enabled;
            }

            if (_velocityCurveControl != null)
            {
                _velocityCurveControl.IsEnabled = enabled;
            }

            ExportDescriptionButton.IsEnabled = enabled;
            ExportExchangeButton.IsEnabled = enabled;
            ImportExchangeButton.IsEnabled = enabled;
        }

        private void LoadNoteComponents()
        {
            ComponentsContainer.Children.Clear();

            var entityId = _controller.CurrentEntityId;
            if (entityId == -1)
            {
                return;
            }

            _currentBoundEntityId = entityId;

            var transform = _controller.GetTransform();
            var meshRenderer = _controller.GetMeshRenderer();
            var parentName = _controller.GetParentName();

            if (transform != null)
            {
                _transformControl = new TransformComponent();
                _transformControl.BindTransform(transform, entityId, parentName, meshRenderer);
                _transformControl.RailIndexChanged += OnRailIndexChanged;
                _transformControl.RotateModeChanged += OnRotateModeChanged;
                _transformControl.RadiusChanged += OnRadiusChanged;

                var transformSection = CreateComponentSection(_transformControl);
                ComponentsContainer.Children.Add(transformSection);
            }

            if (meshRenderer != null)
            {
                _meshRendererControl = new MeshRendererComponent();
                _meshRendererControl.BindMeshRenderer(meshRenderer, entityId);
                _meshRendererControl.TypeChanged += OnNoteTypeChanged;
                _meshRendererControl.VisibilityChanged += OnVisibilityChanged;
                _meshRendererControl.SortingLayerChanged += OnSortingLayerChanged;
                _meshRendererControl.OrderInLayerChanged += OnOrderInLayerChanged;

                if (meshRenderer.Type == NoteTypeEnum.RotateL || meshRenderer.Type == NoteTypeEnum.RotateR)
                {
                    _meshRendererControl.SetRotateModeOnly(true);
                }

                var meshSection = CreateComponentSection(_meshRendererControl);
                ComponentsContainer.Children.Add(meshSection);
            }

            var timing = _controller.GetTiming();
            if (timing != null)
            {
                _timingControl = new TimingComponent();
                _timingControl.BindTiming(timing, entityId);
                _timingControl.TargetTimeChanged += OnTargetTimeChanged;

                var timingSection = CreateComponentSection(_timingControl);
                ComponentsContainer.Children.Add(timingSection);
            }

            var description = _controller.GetDescription();
            if (description != null)
            {
                _descriptionControl = new DescriptionComponent();
                _descriptionControl.BindDescription(description, entityId);
                _descriptionControl.TextChanged += OnDescriptionTextChanged;

                var descriptionSection = CreateComponentSection(_descriptionControl);
                ComponentsContainer.Children.Add(descriptionSection);
            }

            var velocityCurve = _controller.GetVelocityCurve();
            if (velocityCurve != null && timing != null)
            {
                _velocityCurveControl = new VelocityCurveComponent();
                _velocityCurveControl.BindVelocityCurve(velocityCurve, timing, entityId);
                _velocityCurveControl.CurveChanged += OnVelocityCurveChanged;
                var curveSection = CreateComponentSection(_velocityCurveControl);
                ComponentsContainer.Children.Add(curveSection);
            }

            UpdateAddComponentButton();
            if (!_isTransformUpdatedSubscribed)
            {
                _controller.TransformUpdated += OnTransformUpdated;
                _isTransformUpdatedSubscribed = true;
            }

            UpdateAddComponentButton();
            if (!_isTransformUpdatedSubscribed)
            {
                _controller.TransformUpdated += OnTransformUpdated;
                _isTransformUpdatedSubscribed = true;
            }
        }

        private void OnTransformUpdated(object sender, EventArgs e)
        {
            var expectedEntityId = _controller.CurrentEntityId;

            DispatcherQueue.TryEnqueue(() =>
            {
                if (_currentBoundEntityId == expectedEntityId && _transformControl != null)
                {
                    _transformControl.RefreshDisplay();
                }
            });
        }

        private void OnTargetTimeChanged(object sender, (float min, float second, float tick) e)
        {
            _controller?.UpdateTargetTime(e.min, e.second, e.tick);
            _velocityCurveControl?.RefreshDisplay();
        }

        private void OnDescriptionTextChanged(object sender, string text)
        {
            _controller?.UpdateDescription(text);
        }

        private void UpdateAddComponentButton()
        {
            if (AddComponentFlyout == null || AddComponentButton == null)
            {
                return;
            }

            AddComponentFlyout.Items.Clear();
            if (_controller.IsCurrentEntityGroup())
            {
                AddComponentButton.IsEnabled = false;
                return;
            }

            var availableComponents = _controller.GetAvailableComponents();

            if (availableComponents.Count == 0)
            {
                AddComponentButton.IsEnabled = false;
                return;
            }

            AddComponentButton.IsEnabled = true;

            foreach (var componentType in availableComponents)
            {
                var menuItem = new MenuFlyoutItem
                {
                    Text = GetComponentDisplayName(componentType),
                    Tag = componentType
                };
                menuItem.Click += OnAddComponentClicked;
                AddComponentFlyout.Items.Add(menuItem);
            }
        }

        private string GetComponentDisplayName(Type componentType)
        {
            if (componentType == typeof(Timing))
            {
                return "Timing";
            }

            if (componentType == typeof(Description))
            {
                return "Description";
            }

            if (componentType == typeof(NoteType))
            {
                return "Note Type";
            }

            if (componentType == typeof(VelocityCurve))
            {
                return "Velocity Curve";
            }

            return componentType.Name;
        }

        private void OnAddComponentClicked(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuFlyoutItem menuItem)
            {
                return;
            }

            if (menuItem.Tag is not Type componentType)
            {
                return;
            }

            if (componentType == typeof(Timing))
            {
                _controller.AddComponent<Timing>();
            }
            else if (componentType == typeof(Description))
            {
                _controller.AddComponent<Description>();
            }
            else if (componentType == typeof(NoteType))
            {
                _controller.AddComponent<NoteType>();
            }
            else if (componentType == typeof(VelocityCurve))
            {
                _controller.AddComponent<VelocityCurve>();
            }

            LoadNoteComponents();
        }

        private void OnNoteTypeChanged(object sender, NoteTypeEnum type)
        {
            _controller?.UpdateNoteType(type);
            if (_transformControl != null)
            {
                var isRotateType = type is NoteTypeEnum.RotateL or NoteTypeEnum.RotateR;
            }

            _transformControl?.RefreshDisplay();
        }

        private void LoadGroupComponents()
        {
            GroupComponentsContainer.Children.Clear();
            _currentBoundEntityId = _controller.CurrentEntityId;

            var entityId = _controller.CurrentEntityId;
            if (entityId == -1)
            {
                return;
            }

            var timing = _controller.GetGroupTiming();
            if (timing != null)
            {
                _timingControl = new TimingComponent();
                _timingControl.BindTiming(timing, entityId, isReadOnly: true);

                var timingSection = CreateComponentSection(_timingControl);
                GroupComponentsContainer.Children.Add(timingSection);
            }

            var description = _controller.GetDescription();
            if (description != null)
            {
                _descriptionControl = new DescriptionComponent();
                _descriptionControl.BindDescription(description, entityId);
                _descriptionControl.TextChanged += OnDescriptionTextChanged;

                var descriptionSection = CreateComponentSection(_descriptionControl);
                GroupComponentsContainer.Children.Add(descriptionSection);
            }
        }

        private Border CreateComponentSection(UIElement content)
        {
            return new Border
            {
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12),
                Child = content
            };
        }

        private void LoadProjectProperties()
        {
            if (_controller == null)
            {
                return;
            }

            _isUpdatingUI = true;
            var props = _controller.ProjectProperties;

            TitleTextBox.Text = props.Title;
            MusicArtistTextBox.Text = props.MusicArtist;
            ChartAuthorTextBox.Text = props.ChartAuthor;
            BpmTextBox.Text = props.BPM;
            DifficultyConstantBox.Value = props.DifficultyConstant;
            ProjectDescriptionTextBox.Text = props.Description;

            for (int i = 0; i < DifficultyComboBox.Items.Count; i++)
            {
                if (DifficultyComboBox.Items[i] is ComboBoxItem item &&
                    item.Tag?.ToString() == props.Difficulty.ToString())
                {
                    DifficultyComboBox.SelectedIndex = i;
                    break;
                }
            }

            _isUpdatingUI = false;
        }

        private void OnRadiusChanged(object sender, float radius)
        {
            _controller?.UpdateRadius(radius);
        }

        private void OnRailIndexChanged(object sender, (int railIndex, bool isRotateMode) e)
        {
            _controller?.UpdateRailIndex(e.railIndex);
        }

        private void OnRotateModeChanged(object sender, NoteTypeEnum type)
        {
            _controller?.UpdateNoteType(type);
            if (_meshRendererControl != null)
            {
                bool isRotateMode = type == NoteTypeEnum.RotateL || type == NoteTypeEnum.RotateR;
                _meshRendererControl.SetRotateModeOnly(isRotateMode, type);
            }
        }

        private void OnVisibilityChanged(object sender, bool visible)
        {
            _controller?.UpdateVisibility(visible);
        }

        private void OnSortingLayerChanged(object sender, int layer)
        {
            _controller?.UpdateSortingLayer(layer);
        }

        private void OnOrderInLayerChanged(object sender, int order)
        {
            _controller?.UpdateOrderInLayer(order);
        }

        private void OnProjectPropertiesChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_isUpdatingUI)
            {
                return;
            }

            DispatcherQueue.TryEnqueue(LoadProjectProperties);
        }

        private void OnTitleChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingUI || _controller == null)
            {
                return;
            }

            _controller.ProjectProperties.Title = TitleTextBox.Text;
        }

        private void OnMusicArtistChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingUI || _controller == null)
            {
                return;
            }

            _controller.ProjectProperties.MusicArtist = MusicArtistTextBox.Text;
        }

        private void OnChartAuthorChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingUI || _controller == null)
            {
                return;
            }

            _controller.ProjectProperties.ChartAuthor = ChartAuthorTextBox.Text;
        }

        private void OnBpmChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingUI || _controller == null)
            {
                return;
            }

            _controller.ProjectProperties.BPM = BpmTextBox.Text;
        }

        private void OnDifficultyConstantChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (_isUpdatingUI || _controller == null)
            {
                return;
            }

            if (!double.IsNaN(args.NewValue))
            {
                _controller.ProjectProperties.DifficultyConstant = (float)args.NewValue;
            }
        }

        private void OnDifficultyChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingUI || _controller == null)
            {
                return;
            }

            if (DifficultyComboBox.SelectedItem is ComboBoxItem item &&
                Enum.TryParse<DifficultyCategory>(item.Tag?.ToString(), out var category))
            {
                _controller.ProjectProperties.Difficulty = category;
            }
        }

        private void OnProjectDescriptionChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingUI || _controller == null)
            {
                return;
            }

            _controller.ProjectProperties.Description = ProjectDescriptionTextBox.Text;
        }

        private void OnFileIOChanged(object sender, PropertyChangedEventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_controller == null)
                {
                    return;
                }

                if (_controller.FileIO.HasWavFile)
                {
                    WavFileNameText.Text = _controller.FileIO.WavFileName;
                    WavFileNameText.Visibility = Visibility.Visible;
                }
                else
                {
                    WavFileNameText.Visibility = Visibility.Collapsed;
                }

                if (_controller.FileIO.HasCsvFile)
                {
                    CsvFileNameText.Text = _controller.FileIO.CsvFileName;
                    CsvFileNameText.Visibility = Visibility.Visible;
                }
                else
                {
                    CsvFileNameText.Visibility = Visibility.Collapsed;
                }
            });
        }

        private async void OnImportWavClicked(object sender, RoutedEventArgs e)
        {
            if (_controller == null)
            {
                return;
            }

            var hwnd = WindowNative.GetWindowHandle(App.MainWindow);
            await _controller.ImportWavFile(hwnd, DispatcherQueue);
        }

        private async void OnImportCsvClicked(object sender, RoutedEventArgs e)
        {
            if (_controller == null)
            {
                return;
            }

            var hwnd = WindowNative.GetWindowHandle(App.MainWindow);
            await _controller.ImportCsvFile(hwnd);
        }

        private async void OnImportLevelClicked(object sender, RoutedEventArgs e)
        {
            if (_controller == null)
            {
                return;
            }

            var confirmDialog = new ContentDialog
            {
                Title = "Import Exchange File",
                Content = "Do you want to clear the existing scene for importing?",
                PrimaryButtonText = "Clear and Import",
                CloseButtonText = "Cancel",
                XamlRoot = this.XamlRoot
            };

            var result = await confirmDialog.ShowAsync();

            if (result == ContentDialogResult.None)
            {
                return;
            }

            var hwnd = WindowNative.GetWindowHandle(App.MainWindow);
            await _controller.ImportExchangeFileAsync(hwnd);
        }

        private async void OnExportExchangeClicked(object sender, RoutedEventArgs e)
        {
            if (_controller == null)
            {
                return;
            }

            var hwnd = WindowNative.GetWindowHandle(App.MainWindow);
            await _controller.ExportExchangeFileAsync(hwnd);
        }

        private async void OnExportDescriptionClicked(object sender, RoutedEventArgs e)
        {
            if (_controller == null)
            {
                return;
            }

            var hwnd = WindowNative.GetWindowHandle(App.MainWindow);
            await _controller.ExportDescriptionFileAsync(hwnd);
        }

        private void OnStatisticsChanged(object sender, PropertyChangedEventArgs e)
        {
            DispatcherQueue.TryEnqueue(RefreshStatisticsUI);
        }

        private void OnVelocityCurveChanged(object sender, VelocityCurve curve)
        {
            _controller?.UpdateVelocityCurve(curve);
        }

        private void RefreshStatistics()
        {
            _controller?.RefreshStatistics();
            RefreshStatisticsUI();
        }

        private void RefreshStatisticsUI()
        {
            if (_controller == null)
            {
                return;
            }

            var stats = _controller.Statistics;
            TotalObjectsText.Text = stats.TotalObjectCount.ToString();
            TotalComponentsText.Text = stats.TotalComponentCount.ToString();
            MusicLengthText.Text = stats.MusicLength;
            CsvMarkersText.Text = stats.CsvMarkerCount.ToString();
        }
    }
}