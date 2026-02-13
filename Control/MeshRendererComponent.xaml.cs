using FluentDesigner.ECS.Components;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;

namespace FluentDesigner.Control
{
    public sealed partial class MeshRendererComponent : UserControl
    {
        private MeshRenderer _meshRenderer;
        private int _entityId = -1;
        private bool _isUpdating;
        private bool _isRotateModeOnly;
        public event EventHandler<NoteTypeEnum> TypeChanged;
        public event EventHandler<bool> VisibilityChanged;
        public event EventHandler<int> SortingLayerChanged;
        public event EventHandler<int> OrderInLayerChanged;

        public MeshRendererComponent()
        {
            InitializeComponent();
        }

        public void BindMeshRenderer(MeshRenderer meshRenderer, int id)
        {
            _meshRenderer = meshRenderer;
            _entityId = id;
            _isUpdating = true;
            _isRotateModeOnly = meshRenderer.Type is NoteTypeEnum.RotateL or NoteTypeEnum.RotateR;
            SetNoteTypeSelection(meshRenderer.Type);
            VisibilityToggle.IsOn = meshRenderer.Visibility;
            SortingLayerBox.Value = meshRenderer.SortingLayer;
            OrderInLayerBox.Value = meshRenderer.OrderInLayer;
            _isUpdating = false;
        }

        public void SetRotateModeOnly(bool rotateOnly, NoteTypeEnum? defaultType = null)
        {
            _isRotateModeOnly = rotateOnly;
            _isUpdating = true;

            if (rotateOnly)
            {
                foreach (var item in NoteTypeComboBox.Items)
                {
                    if (item is ComboBoxItem comboItem)
                    {
                        var tag = comboItem.Tag?.ToString();
                        comboItem.IsEnabled = tag is "RotateL" or "RotateR";
                    }
                }

                if (defaultType.HasValue)
                {
                    SetNoteTypeSelection(defaultType.Value);
                }
            }
            else
            {
                foreach (var item in NoteTypeComboBox.Items)
                {
                    if (item is ComboBoxItem comboItem)
                    {
                        comboItem.IsEnabled = true;
                    }
                }
            }

            _isUpdating = false;
        }

        public void RefreshDisplay()
        {
            if (_meshRenderer == null)
            {
                return;
            }

            _isUpdating = true;

            SetNoteTypeSelection(_meshRenderer.Type);
            VisibilityToggle.IsOn = _meshRenderer.Visibility;
            SortingLayerBox.Value = _meshRenderer.SortingLayer;
            OrderInLayerBox.Value = _meshRenderer.OrderInLayer;

            _isUpdating = false;
        }

        private void SetNoteTypeSelection(NoteTypeEnum type)
        {
            var typeString = type.ToString();
            for (int i = 0; i < NoteTypeComboBox.Items.Count; i++)
            {
                if (NoteTypeComboBox.Items[i] is ComboBoxItem item &&
                    item.Tag?.ToString() == typeString)
                {
                    NoteTypeComboBox.SelectedIndex = i;
                    break;
                }
            }
        }

        private void OnNoteTypeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdating || _meshRenderer == null)
            {
                return;
            }

            if (NoteTypeComboBox.SelectedItem is ComboBoxItem item &&
                Enum.TryParse<NoteTypeEnum>(item.Tag?.ToString(), out var type))
            {
                TypeChanged?.Invoke(this, type);
            }
        }

        private void OnVisibilityToggled(object sender, RoutedEventArgs e)
        {
            if (_isUpdating || _meshRenderer == null)
            {
                return;
            }

            _meshRenderer.Visibility = VisibilityToggle.IsOn;
            VisibilityChanged?.Invoke(this, VisibilityToggle.IsOn);
        }

        private void OnSortingLayerChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (_isUpdating || _meshRenderer == null)
            {
                return;
            }

            if (!double.IsNaN(args.NewValue))
            {
                int value = (int)args.NewValue;
                _meshRenderer.SortingLayer = value;
                SortingLayerChanged?.Invoke(this, value);
            }
        }

        private void OnOrderInLayerChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (_isUpdating || _meshRenderer == null)
            {
                return;
            }

            if (!double.IsNaN(args.NewValue))
            {
                int value = (int)args.NewValue;
                _meshRenderer.OrderInLayer = value;
                OrderInLayerChanged?.Invoke(this, value);
            }
        }
    }
}
