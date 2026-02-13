using Microsoft.UI.Xaml;
using System;
using System.Numerics;

namespace FluentDesigner.ECS.System;

public sealed class CameraService : Service, IDisposable
{
    private Vector3 _target = Vector3.Zero;
    private float _distance = 10f;
    private float _yaw = 135f;
    private float _pitch = 30f;

    private const float MinDistance = 1f;
    private const float MaxDistance = 500f;
    private const float MinPitch = -89f;
    private const float MaxPitch = 89f;
    private float _rotateSensitivity = 0.2f;
    private float _zoomSensitivity = 0.015f;
    private float _panSensitivity = 0.001f;
    private Vector3 _pos;
    private Vector3 _up = Vector3.UnitY;
    private bool _isDirty = true;

    private bool _isPlaybackMode;
    private Vector3 _playbackPosition = new(0f, 0f, -29f);
    private Vector3 _playbackTarget = Vector3.Zero;
    private Vector3 _playbackUp = Vector3.UnitY;
    private float _playbackFovDegrees = 22f;
    private float _playbackNearPlane = 0.3f;
    private float _playbackFarPlane = 768f;

    private DispatcherTimer _animationTimer;
    private Vector3 _animStartTarget;
    private Vector3 _animEndTarget;
    private float _animStartDistance;
    private float _animEndDistance;
    private float _animStartYaw;
    private float _animEndYaw;
    private float _animStartPitch;
    private float _animEndPitch;
    private float _animProgress;
    private const float AnimationDuration = 0.35f;
    private const float AnimationTickInterval = 8f;
    private float _animElapsed;
    private bool _isAnimating;
    private RenderLoopService _renderLoopService;

    public float PlaybackFovRadians => _playbackFovDegrees * MathF.PI / 180f;
    public float PlaybackNearPlane => _playbackNearPlane;
    public float PlaybackFarPlane => _playbackFarPlane;
    public bool IsPlaybackMode => _isPlaybackMode;

    public Vector3 Position => _isPlaybackMode ? _playbackPosition : GetPosition();
    public Vector3 Target => _isPlaybackMode ? _playbackTarget : _target;
    public Vector3 Up => _isPlaybackMode ? _playbackUp : _up;
    public float Distance => _distance;
    public float Yaw => _yaw;
    public float Pitch => _pitch;

    public event Action<Vector3, Vector3, Vector3> CameraChanged; 

    public float RotateSensitivity
    {
        get => _rotateSensitivity;
        set => _rotateSensitivity = MathF.Max(0.01f, value);
    }

    public float ZoomSensitivity
    {
        get => _zoomSensitivity;
        set => _zoomSensitivity = MathF.Max(0.001f, value);
    }

    public override void Initialize()
    {
        base.Initialize();
        UpdatePosition();
        //_renderLoopService = AyaMvcFramework<AyaMvcFrameworkImpl>.EntryPoint.GetService<RenderLoopService>();

        _animationTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(AnimationTickInterval)
        };
        _animationTimer.Tick += OnAnimationTick;
    }

    public void AnimateTo(Vector3 target, float distance, float yaw, float pitch)
    {
        if (_isPlaybackMode)
        {
            return;
        }

        StopAnimation();

        _animStartTarget = _target;
        _animStartDistance = _distance;
        _animStartYaw = _yaw;
        _animStartPitch = _pitch;

        _animEndTarget = target;
        _animEndDistance = Math.Clamp(distance, MinDistance, MaxDistance);
        _animEndYaw = yaw % 360f;
        if (_animEndYaw < 0) _animEndYaw += 360f;
        _animEndPitch = Math.Clamp(pitch, MinPitch, MaxPitch);

        float yawDiff = _animEndYaw - _animStartYaw;
        if (yawDiff > 180f)
        {
            _animStartYaw += 360f;
        }
        else if (yawDiff < -180f)
        {
            _animEndYaw += 360f;
        }

        _animElapsed = 0f;
        _animProgress = 0f;
        _isAnimating = true;
        _animationTimer.Start();
    }

    public void StopAnimation()
    {
        if (_isAnimating)
        {
            _animationTimer.Stop();
            _isAnimating = false;
        }
    }

    public void EnterPlaybackMode()
    {
        _isPlaybackMode = true;
    }

    private void OnAnimationTick(object sender, object e)
    {
        _animElapsed += (float)AnimationTickInterval / 1000f;
        _animProgress = Math.Clamp(_animElapsed / AnimationDuration, 0f, 1f);

        float easedProgress = EaseOutCubic(_animProgress);

        _target = Vector3.Lerp(_animStartTarget, _animEndTarget, easedProgress);
        _distance = Lerp(_animStartDistance, _animEndDistance, easedProgress);
        _yaw = Lerp(_animStartYaw, _animEndYaw, easedProgress);
        _pitch = Lerp(_animStartPitch, _animEndPitch, easedProgress);

        _yaw %= 360f;
        if (_yaw < 0) _yaw += 360f;

        _isDirty = true;

        SyncToRenderLoop();

        if (_animProgress >= 1f)
        {
            StopAnimation();

            _target = _animEndTarget;
            _distance = _animEndDistance;
            _yaw = _animEndYaw % 360f;
            if (_yaw < 0) _yaw += 360f;
            _pitch = _animEndPitch;
            _isDirty = true;
            SyncToRenderLoop();
        }
    }

    private void SyncToRenderLoop()
    {
        if (_isPlaybackMode)
        {
            return;
        }

        var position = GetPosition();
        CameraChanged?.Invoke(position, _target, _up);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float EaseOutCubic(float t) => 1f - MathF.Pow(1f - t, 3f);

    public void ExitPlaybackMode()
    {
        _isPlaybackMode = false;
        _isDirty = true;
    }

    public void SetPlaybackCamera(
        Vector3 position,
        Vector3 target,
        float fovDegrees,
        float nearPlane,
        float farPlane)
    {
        _playbackPosition = position;
        _playbackTarget = target;
        _playbackFovDegrees = fovDegrees;
        _playbackNearPlane = nearPlane;
        _playbackFarPlane = farPlane;
    }

    public void SetDefaultPlaybackCamera()
    {
        _playbackPosition = new Vector3(0f, 0f, -29f);
        _playbackTarget = Vector3.Zero;
        _playbackUp = Vector3.UnitY;
        _playbackFovDegrees = 28f;
        _playbackNearPlane = 28f;
        _playbackFarPlane = 384f;
    }

    public void Rotate(float deltaX, float deltaY)
    {
        _yaw += deltaX * _rotateSensitivity;
        _pitch -= deltaY * _rotateSensitivity;
        _pitch = Math.Clamp(_pitch, MinPitch, MaxPitch);

        _yaw %= 360f;
        if (_yaw < 0)
        {
            _yaw += 360f;
        }

        _isDirty = true;
    }

    public void Zoom(float delta)
    {
        float factor = 1f - delta * _zoomSensitivity;
        _distance *= factor;
        _distance = Math.Clamp(_distance, MinDistance, MaxDistance);
        _isDirty = true;
    }

    public void Pan(float deltaX, float deltaY)
    {
        var position = GetPosition();
        var forward = Vector3.Normalize(_target - position);
        var right = Vector3.Normalize(Vector3.Cross(forward, _up));
        var up = Vector3.Cross(right, forward);

        float speed = _distance * _panSensitivity;
        _target -= right * deltaX * speed;
        _target += up * deltaY * speed;

        _isDirty = true;
    }

    public void SetTarget(Vector3 target)
    {
        _target = target;
        _isDirty = true;
    }

    public void SetDistance(float distance)
    {
        _distance = Math.Clamp(distance, MinDistance, MaxDistance);
        _isDirty = true;
    }

    public void SetAngles(float yaw, float pitch)
    {
        _yaw = yaw % 360f;
        if (_yaw < 0)
        {
            _yaw += 360f;
        }

        _pitch = Math.Clamp(pitch, MinPitch, MaxPitch);
        _isDirty = true;
    }

    public void Reset()
    {
        _target = Vector3.Zero;
        _distance = 15f;
        _yaw = 45f;
        _pitch = 30f;
        _isDirty = true;
    }

    private Vector3 GetPosition()
    {
        if (_isDirty)
        {
            UpdatePosition();
        }

        return _pos;
    }

    private void UpdatePosition()
    {
        float yawRad = _yaw * MathF.PI / 180f;
        float pitchRad = _pitch * MathF.PI / 180f;

        float cosPitch = MathF.Cos(pitchRad);
        _pos = new Vector3(
            _target.X + _distance * cosPitch * MathF.Sin(yawRad),
            _target.Y + _distance * MathF.Sin(pitchRad),
            _target.Z + _distance * cosPitch * MathF.Cos(yawRad)
        );

        _isDirty = false;
    }

    public Matrix4x4 GetViewMatrix()
    {
        if (_isPlaybackMode)
        {
            return Matrix4x4.CreateLookAt(_playbackPosition, _playbackTarget, _playbackUp);
        }

        var position = GetPosition();
        return Matrix4x4.CreateLookAt(position, _target, _up);
    }

    public Matrix4x4 GetProjectionMatrix(float aspectRatio)
    {
        if (_isPlaybackMode)
        {
            return Matrix4x4.CreatePerspectiveFieldOfView(
                PlaybackFovRadians,
                aspectRatio,
                _playbackNearPlane,
                _playbackFarPlane);
        }

        return Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 4f,
            aspectRatio,
            0.1f,
            1024f);
    }

    public void Dispose() { }
}