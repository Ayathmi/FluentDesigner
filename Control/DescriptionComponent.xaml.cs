using FluentDesigner.ECS.Components;
using Microsoft.UI.Xaml.Controls;
using System;

namespace FluentDesigner.Control
{
    public sealed partial class DescriptionComponent : UserControl
    {
        private Description _description;
        private int _entityId = -1;
        private bool _isUpdating;

        public event EventHandler<string> TextChanged;

        public DescriptionComponent()
        {
            InitializeComponent();
        }

        public void BindDescription(Description description, int entityId)
        {
            _description = description;
            _entityId = entityId;
            _isUpdating = true;

            DescriptionText.Text = description.Text ?? string.Empty;

            _isUpdating = false;
        }

        public void RefreshDisplay()
        {
            if (_description == null)
            {
                return;
            }

            _isUpdating = true;

            DescriptionText.Text = _description.Text ?? string.Empty;

            _isUpdating = false;
        }

        private void OnDescriptionTextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdating || _description == null)
            {
                return;
            }

            _description.Text = DescriptionText.Text;
            TextChanged?.Invoke(this, DescriptionText.Text);
        }
    }
}