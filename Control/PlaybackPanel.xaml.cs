using FluentDesigner.ECS.System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using System;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace FluentDesigner.Control
{
    public sealed partial class PlaybackPanel : UserControl
    {
        private PlaybackService _playbackService;
        private bool _isSliderDragging;
        private float _pendingSeekTime;
        private float _totalTimeSeconds;
        private bool _isInitialized;
        private ContentDialog _progressDialog;
        private TextBlock _progressTextBlock;
        private DateTime _lastPreviewTime = DateTime.MinValue;
        private const double PreviewIntervalMs = 16.67;
        private InspectorService _inspectorService;

        private const float BaseCountdownSeconds = 5.0f;
        private float _audioDelaySeconds;
        private DispatcherTimer _countdownTimer;
        private float _countdownRemaining;
        private ContentDialog _countdownDialog;
        private TextBlock _countdownDisplayText;

        public PlaybackPanel()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_isInitialized)
            {
                return;
            }

            var framework = AyaMvcFramework<AyaMvcFrameworkImpl>.EntryPoint;
            _playbackService = framework.GetService<PlaybackService>();
            _inspectorService = framework.GetService<InspectorService>();

            if (_playbackService != null)
            {
                _playbackService.TimeUpdated += OnTimeUpdated;
                _playbackService.StateChanged += OnStateChanged;
                _playbackService.PlaybackEnded += OnPlaybackEnded;
                _playbackService.PlayModeEntered += OnPlayModeEntered;
                _playbackService.PlayModeExited += OnPlayModeExited;
                _playbackService.PlayModeError += OnPlayModeError;
                _playbackService.ProgressChanged += OnProgressChanged;

                float initialSpeed = (float)Math.Round(BaseSpeedSlider.Value, 1);
                _playbackService.BaseSpeedMultiplier = initialSpeed;
                BaseSpeedText.Text = $"{initialSpeed:F1}x";
            }

            if (_inspectorService != null)
            {
                _inspectorService.Statistics.PropertyChanged += OnStatisticsChanged;
                UpdateDurationInfo();
            }

            _countdownTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _countdownTimer.Tick += OnCountdownTick;

            UpdateEffectiveCountdown();

            _isInitialized = true;
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _countdownTimer?.Stop();

            if (_playbackService != null)
            {
                _playbackService.TimeUpdated -= OnTimeUpdated;
                _playbackService.StateChanged -= OnStateChanged;
                _playbackService.PlaybackEnded -= OnPlaybackEnded;
                _playbackService.PlayModeEntered -= OnPlayModeEntered;
                _playbackService.PlayModeExited -= OnPlayModeExited;
                _playbackService.PlayModeError -= OnPlayModeError;
                _playbackService.ProgressChanged -= OnProgressChanged;
            }

            if (_inspectorService != null)
            {
                _inspectorService.Statistics.PropertyChanged -= OnStatisticsChanged;
            }
        }

        private void OnTimeUpdated(object sender, float currentTime)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_isSliderDragging)
                {
                    TimeInputBox.Text = FormatTime(currentTime);
                }
            });
        }

        private void OnStatisticsChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ProjectStatistics.MaxDurationSeconds) ||
                e.PropertyName == nameof(ProjectStatistics.AudioDurationSeconds) ||
                e.PropertyName == nameof(ProjectStatistics.CsvMaxTimeSeconds) ||
                e.PropertyName == nameof(ProjectStatistics.TargetTimeMaxSeconds))
            {
                DispatcherQueue.TryEnqueue(UpdateDurationInfo);
            }
        }

        private void UpdateDurationInfo()
        {
            if (_inspectorService == null)
            {
                return;
            }

            var stats = _inspectorService.Statistics;

            _totalTimeSeconds = stats.MaxDurationSeconds;
            TotalTimeText.Text = $"/ {FormatTime(_totalTimeSeconds)}";
        }

        private void OnStateChanged(object sender, PlaybackState state)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                UpdateButtonStates(state);
            });
        }

        private void OnPlaybackEnded(object sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                PlaybackInfoBar.Title = "Playback Ended";
                PlaybackInfoBar.Message = "Playback has reached the end.";
                PlaybackInfoBar.Severity = InfoBarSeverity.Success;
            });
        }

        private void OnPlayModeEntered(object sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _totalTimeSeconds = _playbackService?.TotalTime ?? 0f;
                if (_totalTimeSeconds <= 0 && _inspectorService != null)
                {
                    _totalTimeSeconds = _inspectorService.Statistics.MaxDurationSeconds;
                }

                TotalTimeText.Text = $"/ {FormatTime(_totalTimeSeconds)}";

                PlaybackInfoBar.IsOpen = true;
                PlaybackInfoBar.Title = "Playback Mode";
                PlaybackInfoBar.Message = "Editing is disabled during playback.";
                TotalTimeText.Text = "/" + FormatTime(_playbackService.TotalTime);
                PlaybackInfoBar.Severity = InfoBarSeverity.Informational;

                UpdateButtonStates(_playbackService?.State ?? PlaybackState.Stopped);
            });
        }

        private void OnPlayModeExited(object sender, EventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _totalTimeSeconds = 0f;
                TimeInputBox.Text = "00:00:000";
                TotalTimeText.Text = "/ 00:00:000";

                PlaybackInfoBar.IsOpen = false;

                UpdateButtonStates(PlaybackState.Stopped);
            });
        }

        private void OnPlayModeError(object sender, string errorMessage)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                PlaybackInfoBar.IsOpen = true;
                PlaybackInfoBar.Title = "Error";
                PlaybackInfoBar.Message = errorMessage;
                PlaybackInfoBar.Severity = InfoBarSeverity.Error;

                if (_playbackService?.State == PlaybackState.Stopped)
                {
                    EnterPlayModeButton.IsEnabled = true;
                }
            });
        }

        private void OnProgressChanged(object sender, string progressText)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_progressTextBlock != null)
                {
                    _progressTextBlock.Text = progressText;
                }
            });
        }

        private async Task ShowProgressDialogAsync(string title)
        {
            _progressTextBlock = new TextBlock
            {
                Text = "Preparing",
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap
            };

            _progressDialog = new ContentDialog
            {
                Title = title,
                Content = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new ProgressRing
                        {
                            IsActive = true,
                            Width = 32,
                            Height = 32,
                            HorizontalAlignment = HorizontalAlignment.Center
                        },
                        _progressTextBlock
                    }
                },
                XamlRoot = this.XamlRoot
            };
            _ = _progressDialog.ShowAsync();
        }

        private void CloseProgressDialog()
        {
            try
            {
                _progressDialog?.Hide();
            }
            catch { }
            finally
            {
                _progressDialog = null;
                _progressTextBlock = null;
            }
        }

        private async void OnEnterPlayModeClicked(object sender, RoutedEventArgs e)
        {
            if (_playbackService == null)
            {
                PlaybackInfoBar.IsOpen = true;
                PlaybackInfoBar.Title = "Error";
                PlaybackInfoBar.Message = "Playback service not initialized.";
                PlaybackInfoBar.Severity = InfoBarSeverity.Error;
                return;
            }

            EnterPlayModeButton.IsEnabled = false;

            try
            {
                ShowProgressDialog("Prepare To Play");
                await Task.Yield();
                await _playbackService.EnterPlayModeAsync();
            }
            catch (Exception ex)
            {
                PlaybackInfoBar.IsOpen = true;
                PlaybackInfoBar.Title = "Error";
                PlaybackInfoBar.Message = ex.Message;
                PlaybackInfoBar.Severity = InfoBarSeverity.Error;

                EnterPlayModeButton.IsEnabled = true;
            }
            finally
            {
                CloseProgressDialog();
            }
        }

        private async void OnPlayClicked(object sender, RoutedEventArgs e)
        {
            await StartCountdownAsync();
        }

        private void OnPauseClicked(object sender, RoutedEventArgs e)
        {
            _playbackService?.Pause();
        }

        private void OnResetClicked(object sender, RoutedEventArgs e)
        {
            _playbackService?.Reset();
        }

        private void OnRestartClicked(object sender, RoutedEventArgs e)
        {
            _playbackService?.Restart();
        }

        private async void OnExitPlayModeClicked(object sender, RoutedEventArgs e)
        {
            if (_playbackService == null)
            {
                return;
            }

            ExitPlayModeButton.IsEnabled = false;

            try
            {
                ShowProgressDialog("Exit Play Mode");
                await Task.Yield();
                await _playbackService.ExitPlayModeAsync();
            }
            catch (Exception ex)
            {
                PlaybackInfoBar.IsOpen = true;
                PlaybackInfoBar.Title = "Error";
                PlaybackInfoBar.Message = ex.Message;
                PlaybackInfoBar.Severity = InfoBarSeverity.Error;
            }
            finally
            {
                CloseProgressDialog();
            }
        }

        private void OnAudioDelayChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (double.IsNaN(args.NewValue))
            {
                return;
            }

            _audioDelaySeconds = (float)Math.Round(args.NewValue, 1);
            if (_inspectorService != null) { }

            UpdateEffectiveCountdown();
        }

        private void UpdateEffectiveCountdown()
        {
            float effectiveCountdown = BaseCountdownSeconds + _audioDelaySeconds;
            effectiveCountdown = Math.Max(0f, effectiveCountdown);
        }

        private async Task StartCountdownAsync()
        {
            float effectiveCountdown = BaseCountdownSeconds + _audioDelaySeconds;
            effectiveCountdown = Math.Max(0f, effectiveCountdown);

            if (effectiveCountdown <= 0)
            {
                _playbackService?.Play();
                return;
            }

            _countdownRemaining = effectiveCountdown;
            _countdownDisplayText = new TextBlock
            {
                Text = _countdownRemaining.ToString("F1"),
                FontSize = 48,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            _countdownDialog = new ContentDialog
            {
                Title = "Starting Playback...",
                Content = new StackPanel
                {
                    Spacing = 16,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Children =
                    {
                        _countdownDisplayText,
                        new TextBlock
                        {
                            Text = "Press ESC to cancel",
                            FontSize = 12,
                            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                            HorizontalAlignment = HorizontalAlignment.Center
                        }
                    }
                },
                CloseButtonText = "Cancel",
                XamlRoot = this.XamlRoot
            };

            PlayButton.IsEnabled = false;
            _countdownTimer.Start();
            var result = await _countdownDialog.ShowAsync();

            _countdownTimer.Stop();

            if (result == ContentDialogResult.None && _countdownRemaining <= 0)
            {
                _playbackService?.Play();
            }
            else
            {
                PlayButton.IsEnabled = _playbackService?.State != PlaybackState.Playing;
            }

            _countdownDialog = null;
            _countdownDisplayText = null;
        }

        private void OnCountdownTick(object sender, object e)
        {
            _countdownRemaining -= 0.1f;

            if (_countdownDisplayText != null)
            {
                _countdownDisplayText.Text = Math.Max(0, _countdownRemaining).ToString("F1");
            }

            if (_countdownRemaining <= 0)
            {
                _countdownTimer.Stop();
                _countdownDialog?.Hide();
            }
        }


        private void ShowProgressDialog(string title)
        {
            if (this.XamlRoot == null)
            {
                return;
            }

            _progressTextBlock = new TextBlock
            {
                Text = "Preparing",
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            _progressDialog = new ContentDialog
            {
                Title = title,
                Content = new StackPanel
                {
                    Spacing = 12,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Children =
                    {
                        new ProgressRing
                        {
                            IsActive = true,
                            Width = 32,
                            Height = 32,
                            HorizontalAlignment = HorizontalAlignment.Center
                        },
                        _progressTextBlock
                    }
                },
                XamlRoot = this.XamlRoot
            };
            _ = _progressDialog.ShowAsync();
        }

        private void UpdateButtonStates(PlaybackState state)
        {
            bool isInPlayMode = state != PlaybackState.Stopped;
            bool isPlaying = state == PlaybackState.Playing;

            EnterPlayModeButton.IsEnabled = !isInPlayMode;
            PlayButton.IsEnabled = isInPlayMode && !isPlaying;
            PauseButton.IsEnabled = isInPlayMode && isPlaying;
            ResetButton.IsEnabled = isInPlayMode;
            RestartButton.IsEnabled = isInPlayMode;
            ExitPlayModeButton.IsEnabled = isInPlayMode;
            TimeInputBox.IsEnabled = isInPlayMode;
        }

        private void OnTimeInputKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                TrySeekFromInput();
                e.Handled = true;
            }
        }

        private void OnTimeInputLostFocus(object sender, RoutedEventArgs e)
        {
            TrySeekFromInput();
        }

        private void TrySeekFromInput()
        {
            float time = ParseTime(TimeInputBox.Text);
            if (time >= 0 && _playbackService != null)
            {
                _playbackService.Seek(time);
            }
            else
            {
                TimeInputBox.Text = FormatTime(_playbackService?.CurrentTime ?? 0f);
            }
        }

        private void OnSliderPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _isSliderDragging = true;
            (sender as UIElement)?.CapturePointer(e.Pointer);
        }

        private void OnSliderPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_isSliderDragging)
            {
                _isSliderDragging = false;
                _lastPreviewTime = DateTime.MinValue;
                TimeInputBox.Text = FormatTime(_pendingSeekTime);
                _playbackService?.Seek(_pendingSeekTime);
            }
            (sender as UIElement)?.ReleasePointerCapture(e.Pointer);
        }

        private void OnSliderPointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            if (_isSliderDragging)
            {
                _isSliderDragging = false;
                _lastPreviewTime = DateTime.MinValue;
                TimeInputBox.Text = FormatTime(_pendingSeekTime);
                _playbackService?.Seek(_pendingSeekTime);
            }
        }

        private void OnSliderValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_totalTimeSeconds <= 0)
            {
                return;
            }

            float seekTime = (float)(e.NewValue * _totalTimeSeconds);
            _pendingSeekTime = seekTime;

            TimeInputBox.Text = FormatTime(seekTime);

            if (_isSliderDragging)
            {
                var now = DateTime.UtcNow;
                if ((now - _lastPreviewTime).TotalMilliseconds >= PreviewIntervalMs)
                {
                    _lastPreviewTime = now;
                    _playbackService?.PreviewSeek(seekTime);
                }
            }
        }

        private async void OnBaseSpeedChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (double.IsNaN(e.NewValue))
            {
                return;
            }

            if (BaseSpeedText == null)
            {
                return;
            }

            float newSpeed = (float)Math.Round(e.NewValue, 1);
            BaseSpeedText.Text = $"{newSpeed:F1}x";
            if (_playbackService != null && _playbackService.IsInPlayMode)
            {
                _playbackService.BaseSpeedMultiplier = newSpeed;
                await _playbackService.RecalculatePositionsAsync();
            }
        }

        private static string FormatTime(float seconds)
        {
            if (float.IsNaN(seconds) || float.IsInfinity(seconds))
            {
                seconds = 0f;
            }

            int totalMs = (int)(seconds * 1000);
            int min = totalMs / 60000;
            int sec = (totalMs % 60000) / 1000;
            int ms = totalMs % 1000;
            return $"{min:D2}:{sec:D2}:{ms:D3}";
        }

        private static float ParseTime(string timeString)
        {
            var match = Regex.Match(timeString ?? "", @"^(\d{1,2}):(\d{1,2}):(\d{1,3})$");
            if (!match.Success)
            {
                return -1f;
            }

            int min = int.Parse(match.Groups[1].Value);
            int sec = int.Parse(match.Groups[2].Value);
            int ms = int.Parse(match.Groups[3].Value);

            return min * 60f + sec + ms / 1000f;
        }
    }
}