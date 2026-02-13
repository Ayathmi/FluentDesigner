using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using System;
using System.Threading;

namespace FluentDesigner.ECS.System;

public sealed class AudioPlaybackManager : IMMNotificationClient, IDisposable
{
    private readonly object _lock = new();
    private readonly MMDeviceEnumerator _deviceEnumerator;

    private IWavePlayer _wavePlayer;
    private AudioFileReader _audioReader;
    private string _currentFilePath;
    private bool _isDisposed;
    private float _pendingSeekPosition = -1;

    public bool IsPlaying => _wavePlayer?.PlaybackState == NAudio.Wave.PlaybackState.Playing;
    public bool HasAudio => _audioReader != null;
    public float CurrentPosition => _audioReader != null ? (float)_audioReader.CurrentTime.TotalSeconds : 0f;
    public float Duration => _audioReader != null ? (float)_audioReader.TotalTime.TotalSeconds : 0f;

    public event EventHandler<string> DeviceChanged;
    public event EventHandler DeviceLost;
    public event EventHandler<string> PlaybackError;

    public AudioPlaybackManager()
    {
        _deviceEnumerator = new MMDeviceEnumerator();
        _deviceEnumerator.RegisterEndpointNotificationCallback(this);
    }

    public bool LoadAudio(string filePath)
    {
        lock (_lock)
        {
            try
            {
                DisposeCurrentPlayback();

                _audioReader = new AudioFileReader(filePath);
                _currentFilePath = filePath;

                InitializeWavePlayer();
                return true;
            }
            catch (Exception ex)
            {
                PlaybackError?.Invoke(this, $"Failed to load audio: {ex.Message}");
                return false;
            }
        }
    }

    private void InitializeWavePlayer()
    {
        try
        {
            var device = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _wavePlayer = new WasapiOut(device, AudioClientShareMode.Shared, true, 100);
            _wavePlayer.Init(_audioReader);
            _wavePlayer.PlaybackStopped += OnPlaybackStopped;
        }
        catch (Exception ex)
        {
            PlaybackError?.Invoke(this, $"Failed to initialize audio device: {ex.Message}");
            _wavePlayer = null;
        }
    }

    private void OnPlaybackStopped(object sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            PlaybackError?.Invoke(this, $"Playback error: {e.Exception.Message}");
            TryRecoverToNewDevice();
        }
    }

    public void Play()
    {
        lock (_lock)
        {
            if (_wavePlayer == null || _audioReader == null)
            {
                return;
            }

            try
            {
                _wavePlayer.Play();
            }
            catch (Exception ex)
            {
                PlaybackError?.Invoke(this, $"Play failed: {ex.Message}");
                TryRecoverToNewDevice();
            }
        }
    }

    public void Pause()
    {
        lock (_lock)
        {
            try
            {
                _wavePlayer?.Pause();
            }
            catch { }
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            try
            {
                _wavePlayer?.Stop();
            }
            catch { }
        }
    }

    public void Seek(float timeSeconds)
    {
        lock (_lock)
        {
            if (_audioReader == null)
            {
                return;
            }

            try
            {
                var targetTime = TimeSpan.FromSeconds(Math.Clamp(timeSeconds, 0, Duration));
                _audioReader.CurrentTime = targetTime;
            }
            catch (Exception ex)
            {
                PlaybackError?.Invoke(this, $"Seek failed: {ex.Message}");
            }
        }
    }

    private void TryRecoverToNewDevice()
    {
        lock (_lock)
        {
            if (string.IsNullOrEmpty(_currentFilePath))
            {
                return;
            }

            float currentPos = CurrentPosition;
            bool wasPlaying = IsPlaying;

            try
            {
                _pendingSeekPosition = currentPos;

                if (_wavePlayer != null)
                {
                    _wavePlayer.PlaybackStopped -= OnPlaybackStopped;
                    _wavePlayer.Dispose();
                    _wavePlayer = null;
                }

                Thread.Sleep(500);

                var newDevice = GetAvailableDevice();
                if (newDevice == null)
                {
                    DeviceLost?.Invoke(this, EventArgs.Empty);
                    return;
                }

                _wavePlayer = new WasapiOut(newDevice, AudioClientShareMode.Shared, true, 100);
                _wavePlayer.Init(_audioReader);
                _wavePlayer.PlaybackStopped += OnPlaybackStopped;

                if (_pendingSeekPosition >= 0)
                {
                    Seek(_pendingSeekPosition);
                    _pendingSeekPosition = -1;
                }

                DeviceChanged?.Invoke(this, newDevice.FriendlyName);

                if (wasPlaying)
                {
                    _wavePlayer.Play();
                }
            }
            catch (Exception ex)
            {
                PlaybackError?.Invoke(this, $"Failed to recover: {ex.Message}");
                DeviceLost?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private MMDevice GetAvailableDevice()
    {
        try
        {
            return _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        catch
        {
            try
            {
                var devices = _deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                return devices.Count > 0 ? devices[0] : null;
            }
            catch
            {
                return null;
            }
        }
    }

    private void DisposeCurrentPlayback()
    {
        if (_wavePlayer != null)
        {
            _wavePlayer.PlaybackStopped -= OnPlaybackStopped;
            _wavePlayer.Stop();
            _wavePlayer.Dispose();
            _wavePlayer = null;
        }

        _audioReader?.Dispose();
        _audioReader = null;
        _currentFilePath = null;
    }

    public void UnloadAudio()
    {
        lock (_lock)
        {
            DisposeCurrentPlayback();
        }
    }

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Render && role == Role.Multimedia && HasAudio)
        {
            TryRecoverToNewDevice();
        }
    }

    public void OnDeviceRemoved(string deviceId)
    {
        if (HasAudio)
        {
            TryRecoverToNewDevice();
        }
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState)
    {
        if (newState == DeviceState.NotPresent || newState == DeviceState.Unplugged)
        {
            if (HasAudio)
            {
                TryRecoverToNewDevice();
            }
        }
    }

    public void OnDeviceAdded(string pwstrDeviceId) { }
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        lock (_lock)
        {
            DisposeCurrentPlayback();
        }

        try
        {
            _deviceEnumerator.UnregisterEndpointNotificationCallback(this);
            _deviceEnumerator.Dispose();
        }
        catch { }
    }
}