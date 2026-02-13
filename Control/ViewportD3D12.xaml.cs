using FluentDesigner.ECS.System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Numerics;

namespace FluentDesigner.Control
{
    public sealed partial class ViewportD3D12 : UserControl
    {
        private RenderService _renderService;
        private RenderLoopService _renderLoop;
        private EcsWorldService _ecsWorld;
        private TextureService _textureService;
        private ConstantBufferService _cbufferSevice;
        private InstanceBufferService _ibufferService;
        private GridService _gridService;
        private CameraService _cameraService;
        private OutlineService _outlineService;
        private SceneLayoutService _sceneLayoutService;
        private bool _isRightMouseDown = false;
        private bool _isMiddleMouseDown = false;
        private bool _isInitialized = false;
        private Windows.Foundation.Point _lastMousePosition;
        private ScenePickerService _pickingService;
        private bool _isBoxSelecting = false;
        private Windows.Foundation.Point _boxSelectStart;
        private Windows.Foundation.Rect _boxSelectRect;
        private PlaybackService _playbackService;


        public ViewportD3D12()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;

            SwapChainPanelD3D12.PointerPressed += OnPointerPressed;
            SwapChainPanelD3D12.PointerReleased += OnPointerReleased;
            SwapChainPanelD3D12.PointerMoved += OnPointerMoved;
            SwapChainPanelD3D12.PointerWheelChanged += OnPointerWheelChanged;
            SwapChainPanelD3D12.PointerCaptureLost += OnPointerCaptureLost;
        }

        private void OnLoaded(object s, RoutedEventArgs e)
        {
            if (!_isInitialized)
            {
                InitializeRendering();
            }

            if (_playbackService == null)
            {
                var framework = AyaMvcFramework<AyaMvcFrameworkImpl>.EntryPoint;
                _playbackService = framework.GetService<PlaybackService>();
                if (_playbackService != null)
                {
                    _playbackService.PlayModeEntered += OnPlayModeEntered;
                    _playbackService.PlayModeExited += OnPlayModeExited;
                }
            }
        }

        private void OnUnloaded(object s, RoutedEventArgs e)
        {
            if (_renderLoop != null)
            {
                _renderLoop.StopRenderLoop();
            }

            if (_playbackService == null)
            {
                var framework = AyaMvcFramework<AyaMvcFrameworkImpl>.EntryPoint;
                _playbackService = framework.GetService<PlaybackService>();
                if (_playbackService != null)
                {
                    _playbackService.PlayModeEntered += OnPlayModeEntered;
                    _playbackService.PlayModeExited += OnPlayModeExited;
                }
            }

            _isInitialized = false;
        }

        private void InitializeRendering()
        {
            try
            {
                var framework = AyaMvcFramework<AyaMvcFrameworkImpl>.EntryPoint;

                _renderService = framework.GetService<RenderService>();
                _renderService.Initialize();
                _cbufferSevice = framework.GetService<ConstantBufferService>();
                _cbufferSevice.Initialize();
                _ibufferService = framework.GetService<InstanceBufferService>();
                _ibufferService.Initialize();
                _textureService = framework.GetService<TextureService>();
                _textureService.Initialize();
                _gridService = framework.GetService<GridService>();
                _gridService.Initialize();
                _ecsWorld = framework.GetService<EcsWorldService>();
                _ecsWorld.Initialize();
                _renderLoop = framework.GetService<RenderLoopService>();
                _renderLoop.Initialize();
                _cameraService = framework.GetService<CameraService>();
                _cameraService.Initialize();
                _pickingService = framework.GetService<ScenePickerService>();
                _pickingService.Initialize();
                _outlineService = framework.GetService<OutlineService>();
                _outlineService.Initialize();
                _sceneLayoutService = framework.GetService<SceneLayoutService>();
                _sceneLayoutService.Initialize();
                var playbackService = framework.GetService<PlaybackService>();
                playbackService.Initialize();
                var waveformService = framework.GetService<WaveformService>();
                waveformService.Initialize();
                var csvMarkerService = framework.GetService<CsvMarkerService>();
                csvMarkerService.Initialize();

                _renderService.SetSwapChainTarget(SwapChainPanelD3D12);

                _cameraService.SetAngles(45f, 30f);
                _cameraService.SetDistance(20f);

                _renderLoop.StartRenderLoopWithComposition();

                _isInitialized = true;
            }
            catch (Exception ex)
            {
                var innerEx = ex.InnerException ?? ex;
                System.Diagnostics.Debug.WriteLine($"Rendering initialization failed: {innerEx.GetType().Name}: {innerEx.Message}");
                System.Diagnostics.Debug.WriteLine($"Stack trace: {innerEx.StackTrace}");
            }
        }

        private void OnGridVisibilityToggled(object sender, RoutedEventArgs e)
        {
            if (_renderLoop != null && sender is ToggleSwitch toggle)
            {
                _renderLoop.GridVisible = toggle.IsOn;
            }
        }

        private void OnGridSpacingChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (_renderLoop == null)
            {
                return;
            }

            float spacing = double.IsNaN(args.NewValue) || args.NewValue < 1 ? 1f : (float)args.NewValue;
            _renderLoop.GridSpacing = spacing;
            ResetCameraToOrigin();
        }

        private void ResetCameraToOrigin()
        {
            if (_cameraService == null)
            {
                return;
            }

            _cameraService.SetTarget(Vector3.Zero);
            float dis = 50f;
            if (_renderLoop == null)
            {
                return;
            }

            _cameraService.SetDistance(dis);
            _cameraService.SetAngles(45f, 30f);
            UpdateCameraFromService();
        }

        public void SetGridSpacing(float spacing)
        {
            if (_renderLoop != null)
            {
                _renderLoop.GridSpacing = spacing;
            }

            GridSpacingInput.Value = spacing < 1 ? 1 : spacing;
            ResetCameraToOrigin();
        }

        public void SetGridVisibility(bool isVisible)
        {
            if (_renderLoop != null)
            {
                _renderLoop.GridVisible = isVisible;
            }

            GridVisibilityToggle.IsOn = isVisible;
        }

        public void ResetCamera()
        {
            _cameraService?.Reset();
            ResetCameraToOrigin();
            UpdateCameraFromService();
        }

        public void FocusOn(Vector3 target)
        {
            _cameraService?.SetTarget(target);
            UpdateCameraFromService();
        }

        private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var pointer = e.GetCurrentPoint(SwapChainPanelD3D12);
            _lastMousePosition = pointer.Position;

            if (pointer.Properties.IsLeftButtonPressed)
            {
                _pickingService?.UpdateViewport(
                    (float)SwapChainPanelD3D12.ActualWidth,
                    (float)SwapChainPanelD3D12.ActualHeight);

                var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
                    Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
                var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
                    Windows.System.VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

                if (shift)
                {
                    _isBoxSelecting = true;
                    _boxSelectStart = pointer.Position;
                    ShowBoxSelectionRect(_boxSelectStart, _boxSelectStart);
                    SwapChainPanelD3D12.CapturePointer(e.Pointer);
                }
                else
                {
                    _pickingService?.ClickSelect(
                        (float)pointer.Position.X,
                        (float)pointer.Position.Y,
                        additive: ctrl,
                        toggle: ctrl);
                }
                e.Handled = true;
            }
            else if (pointer.Properties.IsRightButtonPressed)
            {
                _isRightMouseDown = true;
                SwapChainPanelD3D12.CapturePointer(e.Pointer);
                e.Handled = true;
            }
            else if (pointer.Properties.IsMiddleButtonPressed)
            {
                _isMiddleMouseDown = true;
                SwapChainPanelD3D12.CapturePointer(e.Pointer);
                e.Handled = true;
            }
        }

        private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            var pointer = e.GetCurrentPoint(SwapChainPanelD3D12);

            if (_isBoxSelecting)
            {
                _isBoxSelecting = false;
                HideBoxSelectionRect();

                var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
                    Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

                _pickingService?.BoxSelect(
                    (float)_boxSelectStart.X,
                    (float)_boxSelectStart.Y,
                    (float)pointer.Position.X,
                    (float)pointer.Position.Y,
                    additive: ctrl);

                SwapChainPanelD3D12.ReleasePointerCapture(e.Pointer);
                e.Handled = true;
                return;
            }

            if (!pointer.Properties.IsRightButtonPressed)
            {
                _isRightMouseDown = false;
            }

            if (!pointer.Properties.IsMiddleButtonPressed)
            {
                _isMiddleMouseDown = false;
            }

            if (!_isRightMouseDown && !_isMiddleMouseDown)
            {
                SwapChainPanelD3D12.ReleasePointerCapture(e.Pointer);
            }

            e.Handled = true;
        }

        private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_cameraService == null)
            {
                return;
            }

            var pointer = e.GetCurrentPoint(SwapChainPanelD3D12);
            var currentPosition = pointer.Position;

            float deltaX = (float)(currentPosition.X - _lastMousePosition.X);
            float deltaY = (float)(currentPosition.Y - _lastMousePosition.Y);

            if (_isBoxSelecting)
            {
                UpdateBoxSelectionRect(_boxSelectStart, currentPosition);
                e.Handled = true;
            }
            else if (_isRightMouseDown)
            {
                _cameraService.Rotate(-deltaX, -deltaY);
                UpdateCameraFromService();
                e.Handled = true;
            }
            else if (_isMiddleMouseDown)
            {
                _cameraService.Pan(deltaX, deltaY);
                UpdateCameraFromService();
                e.Handled = true;
            }

            _lastMousePosition = currentPosition;
        }

        private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            if (_cameraService == null)
            {
                return;
            }

            var pointer = e.GetCurrentPoint(SwapChainPanelD3D12);
            int delta = pointer.Properties.MouseWheelDelta;

            _cameraService.Zoom(delta / 120f);
            UpdateCameraFromService();

            e.Handled = true;
        }

        private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            _isRightMouseDown = false;
            _isMiddleMouseDown = false;
        }

        private void OnPlayModeEntered(object sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                GridSettingPanel.Visibility = Visibility.Collapsed;
            });
        }

        private void OnPlayModeExited(object sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                GridSettingPanel.Visibility = Visibility.Visible;
            });
        }

        private void UpdateCameraFromService()
        {
            if (_renderLoop == null || _cameraService == null)
            {
                return;
            }

            _renderLoop.SetCamera(
                _cameraService.Position,
                _cameraService.Target,
                _cameraService.Up);
        }

        private void ShowBoxSelectionRect(Windows.Foundation.Point start, Windows.Foundation.Point end)
        {
            BoxSelectionRect.Visibility = Visibility.Visible;
            UpdateBoxSelectionRect(start, end);
        }

        private void UpdateBoxSelectionRect(Windows.Foundation.Point start, Windows.Foundation.Point end)
        {
            double left = Math.Min(start.X, end.X);
            double top = Math.Min(start.Y, end.Y);
            double width = Math.Abs(end.X - start.X);
            double height = Math.Abs(end.Y - start.Y);

            Canvas.SetLeft(BoxSelectionRect, left);
            Canvas.SetTop(BoxSelectionRect, top);
            BoxSelectionRect.Width = Math.Max(1, width);
            BoxSelectionRect.Height = Math.Max(1, height);
        }

        private void HideBoxSelectionRect()
        {
            BoxSelectionRect.Visibility = Visibility.Collapsed;
        }
    }
}
