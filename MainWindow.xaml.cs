using System;
using FluentDesigner.ECS.System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using WinRT.Interop;

namespace FluentDesigner
{
    public sealed partial class MainWindow : Window
    {
        private PlaybackService _playbackService;
        private InspectorController _inspectorController;

        public MainWindow()
        {
            InitializeComponent();
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            Activated += OnActivated;
        }

        private void OnActivated(object sender, WindowActivatedEventArgs args)
        {
            if (_playbackService != null)
            {
                return;
            }

            var framework = AyaMvcFramework<AyaMvcFrameworkImpl>.EntryPoint;
            _playbackService = framework.GetService<PlaybackService>();
            _inspectorController = InspectorViewControl?._controller;

            if (_playbackService != null)
            {
                _playbackService.PlayModeEntered += OnPlayModeEntered;
                _playbackService.PlayModeExited += OnPlayModeExited;
            }

            if (Content is UIElement rootElement)
            {
                rootElement.KeyDown += OnGlobalKeyDown;
            }
        }

        private async void OnGlobalKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (_inspectorController == null)
            {
                _inspectorController = InspectorViewControl?._controller;
            }

            if (_inspectorController == null)
            {
                return;
            }

            var ctrlPressed = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            var shiftPressed = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

            var windowHandle = WindowNative.GetWindowHandle(this);

            if (ctrlPressed && shiftPressed)
            {
                switch (e.Key)
                {
                    case VirtualKey.P:
                        await _inspectorController.ExportDescriptionFileAsync(windowHandle);
                        e.Handled = true;
                        break;
                    case VirtualKey.S:
                        await _inspectorController.ExportExchangeFileAsync(windowHandle);
                        e.Handled = true;
                        break;
                }
            }
            else if (ctrlPressed)
            {
                switch (e.Key)
                {
                    case VirtualKey.O:
                        var confirmDialog = new ContentDialog
                        {
                            Title = "Import Exchange File",
                            Content = "Do you want to clear the existing scene for importing?",
                            PrimaryButtonText = "Clear and Import",
                            CloseButtonText = "Cancel",
                            XamlRoot = HierarchyViewControl.XamlRoot
                        };

                        var result = await confirmDialog.ShowAsync();

                        if (result == ContentDialogResult.None)
                        {
                            return;
                        }
                        await _inspectorController.ImportExchangeFileAsync(windowHandle);
                        e.Handled = true;
                        break;
                    case VirtualKey.S:
                        if (_inspectorController.HasImportedExchangePath)
                        {
                            await _inspectorController.QuickSaveExchangeFileAsync();
                        }
                        else
                        {
                            await _inspectorController.ExportExchangeFileAsync(windowHandle);
                        }
                        e.Handled = true;
                        break;
                }
            }
        }

        private void OnPlayModeEntered(object sender, System.EventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                HierarchyViewControl.IsEnabled = false;
                InspectorViewControl.IsEnabled = false;
            });
        }

        private void OnPlayModeExited(object sender, System.EventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                HierarchyViewControl.IsEnabled = true;
                InspectorViewControl.IsEnabled = true;
            });
        }
    }
}
