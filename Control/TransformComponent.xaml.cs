using FluentDesigner.ECS.Components;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using RangeBaseValueChangedEventArgs = Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs;


namespace FluentDesigner.Control
{
    public sealed partial class TransformComponent : UserControl
    {
        private Transform _transform;
        private MeshRenderer _meshRenderer;
        private int _entityId = -1;
        private bool _isUpdating;
        private bool _isRotateMode;

        public event EventHandler<(int railIndex, bool isRotateMode)> RailIndexChanged;
        public event EventHandler<NoteTypeEnum> RotateModeChanged;
        public event EventHandler<float> RadiusChanged;

        public TransformComponent()
        {
            InitializeComponent();
        }

        public void BindTransform(Transform transform, int id, string parent, MeshRenderer mesh)
        {
            _transform = transform;
            _meshRenderer = mesh;
            _entityId = id;
            _isUpdating = true;

            PositionX.Text = transform.Position.X.ToString("F3");
            PositionY.Text = transform.Position.Y.ToString("F3");
            PositionZ.Text = transform.Position.Z.ToString("F3");
            RotationX.Text = transform.Rotation.X.ToString("F3");
            RotationY.Text = transform.Rotation.Y.ToString("F3");
            RotationZ.Text = transform.Rotation.Z.ToString("F3");
            RotationW.Text = transform.Rotation.W.ToString("F3");
            ScaleX.Text = transform.Scale.X.ToString("F3");
            ScaleY.Text = transform.Scale.Y.ToString("F3");
            ScaleZ.Text = transform.Scale.Z.ToString("F3");

            var railIndex = transform.Index.Index;
            _isRotateMode = mesh != null && (mesh.Type == NoteTypeEnum.RotateL || mesh.Type == NoteTypeEnum.RotateR);
            RotateModeCheckBox.IsChecked = _isRotateMode;
            UpdateRotateModeUI();

            if (!_isRotateMode)
            {
                RailIndexSlider.Value = Math.Clamp(railIndex, 0, 359);
                RailIndexText.Text = railIndex.ToString();
            }

            if (!string.IsNullOrEmpty(parent))
            {
                ParentName.Text = parent;
                ParentPanel.Visibility = Visibility.Visible;
            }
            else
            {
                ParentPanel.Visibility = Visibility.Collapsed;
            }

            _isUpdating = false;
        }

        public void RefreshDisplay()
        {
            if (_transform == null)
            {
                return;
            }

            _isUpdating = true;

            PositionX.Text = _transform.Position.X.ToString("F3");
            PositionY.Text = _transform.Position.Y.ToString("F3");
            PositionZ.Text = _transform.Position.Z.ToString("F3");

            RotationX.Text = _transform.Rotation.X.ToString("F3");
            RotationY.Text = _transform.Rotation.Y.ToString("F3");
            RotationZ.Text = _transform.Rotation.Z.ToString("F3");
            RotationW.Text = _transform.Rotation.W.ToString("F3");

            ScaleX.Text = _transform.Scale.X.ToString("F3");
            ScaleY.Text = _transform.Scale.Y.ToString("F3");
            ScaleZ.Text = _transform.Scale.Z.ToString("F3");

            if (!_isRotateMode)
            {
                RailIndexSlider.Value = _transform.Index.Index;
                RailIndexText.Text = _transform.Index.Index.ToString();
            }

            _isUpdating = false;
        }

        private void OnRadiusChanged(object s, RangeBaseValueChangedEventArgs e)
        {
            if (_isUpdating || _transform == null)
            {
                return;
            }

            float newRadius = (float)Math.Round(e.NewValue, 1);
            RadiusText.Text = newRadius.ToString("F1");
            _transform.Radius = newRadius;
            RadiusChanged?.Invoke(this, newRadius);
        }

        private void OnRadiusTextKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != Windows.System.VirtualKey.Enter || _isUpdating || _transform == null)
            {
                return;
            }

            if (float.TryParse(RadiusText.Text, out float value))
            {
                float clamped = Math.Clamp(value, 0.1f, 7.5f);
                _isUpdating = true;
                RadiusSlider.Value = clamped;
                RadiusText.Text = clamped.ToString("F1");
                _isUpdating = false;

                _transform.Radius = clamped;
                RadiusChanged?.Invoke(this, clamped);
            }
        }

        private void OnRailIndexChanged(object s, RangeBaseValueChangedEventArgs e)
        {
            if (_isUpdating || _transform == null)
            {
                return;
            }

            int newValue = (int)e.NewValue;
            RailIndexText.Text = newValue.ToString();
            _transform.Index = new RailIndex { Index = newValue };
            RailIndexChanged?.Invoke(this, (newValue, _isRotateMode));
        }

        private void OnRotateModeChecked(object s, RoutedEventArgs e)
        {
            if (_isUpdating)
            {
                return;
            }

            _isRotateMode = true;
            UpdateRotateModeUI();

            _transform.Index = new RailIndex { Index = 360 };
            RailIndexChanged?.Invoke(this, (360, true));
            RotateModeChanged?.Invoke(this, NoteTypeEnum.RotateL);
        }

        private void OnRotateModeUnchecked(object s, RoutedEventArgs e)
        {
            if (_isUpdating)
            {
                return;
            }

            _isRotateMode = false;
            UpdateRotateModeUI();
            _isUpdating = true;
            RailIndexSlider.Value = 0;
            _transform.Index = new RailIndex { Index = 0 };
            RailIndexText.Text = "0";
            _isUpdating = false;
            RailIndexChanged?.Invoke(this, (0, false));
            RotateModeChanged?.Invoke(this, NoteTypeEnum.Click);
        }

        private void UpdateRotateModeUI()
        {
            if (_isRotateMode)
            {
                RailIndexSlider.IsEnabled = false;
                RailIndexText.Text = "Rotate";
            }
            else
            {
                RailIndexSlider.IsEnabled = true;
            }
        }
    }
}
