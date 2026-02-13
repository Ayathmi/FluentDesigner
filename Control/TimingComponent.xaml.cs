using FluentDesigner.ECS.Components;
using Microsoft.UI.Xaml.Controls;
using System;

namespace FluentDesigner.Control
{
    public sealed partial class TimingComponent : UserControl
    {
        private Timing _timing;
        private int _entityId = -1;
        private bool _isUpdating;
        private bool _isReadOnly;

        public event EventHandler<(float min, float second, float tick)> TargetTimeChanged;

        public TimingComponent()
        {
            InitializeComponent();
        }

        public void BindTiming(Timing timing, int entityId, bool isReadOnly = false)
        {
            _timing = timing;
            _entityId = entityId;
            _isReadOnly = isReadOnly;
            _isUpdating = true;

            TargetMin.Value = timing.TargetMin;
            TargetSecond.Value = timing.TargetSecond;
            TargetTick.Value = timing.TargetTick;

            TargetMin.IsEnabled = !isReadOnly;
            TargetSecond.IsEnabled = !isReadOnly;
            TargetTick.IsEnabled = !isReadOnly;

            _isUpdating = false;
        }

        public void RefreshDisplay()
        {
            if (_timing == null)
            {
                return;
            }

            _isUpdating = true;

            TargetMin.Value = _timing.TargetMin;
            TargetSecond.Value = _timing.TargetSecond;
            TargetTick.Value = _timing.TargetTick;

            _isUpdating = false;
        }

        private void OnTargetMinChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (_isUpdating || _timing == null)
            {
                return;
            }

            if (double.IsNaN(args.NewValue))
            {
                return;
            }

            _timing.TargetMin = (float)args.NewValue;
            TargetTimeChanged?.Invoke(this, (_timing.TargetMin, _timing.TargetSecond, _timing.TargetTick));
        }

        private void OnTargetSecondChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (_isUpdating || _timing == null)
            {
                return;
            }

            if (double.IsNaN(args.NewValue))
            {
                return;
            }

            int newSecond = (int)args.NewValue;
            if (newSecond < 0 && _timing.TargetMin > 0)
            {
                _isUpdating = true;
                _timing.TargetMin -= 1;
                _timing.TargetSecond = 59;
                TargetMin.Value = _timing.TargetMin;
                TargetSecond.Value = _timing.TargetSecond;
                _isUpdating = false;
            }
            else if (newSecond >= 0)
            {
                _timing.TargetSecond = (float)newSecond;
            }

            TargetTimeChanged?.Invoke(this, (_timing.TargetMin, _timing.TargetSecond, _timing.TargetTick));
        }

        private void OnTargetTickChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (_isUpdating || _timing == null)
            {
                return;
            }

            if (double.IsNaN(args.NewValue))
            {
                return;
            }

            int newTick = (int)args.NewValue;
            if (newTick < 0 && _timing.TargetSecond > 0)
            {
                _isUpdating = true;
                _timing.TargetSecond -= 1;
                _timing.TargetTick = 999;
                TargetSecond.Value = _timing.TargetSecond;
                TargetTick.Value = _timing.TargetTick;
                _isUpdating = false;
            }
            else if (newTick < 0 && _timing is { TargetSecond: 0, TargetMin: > 0 })
            {
                _isUpdating = true;
                _timing.TargetMin -= 1;
                _timing.TargetSecond = 59;
                _timing.TargetTick = 999;
                TargetMin.Value = _timing.TargetMin;
                TargetSecond.Value = _timing.TargetSecond;
                TargetTick.Value = _timing.TargetTick;
                _isUpdating = false;
            }
            else if (newTick >= 0)
            {
                _timing.TargetTick = (float)newTick;
            }

            TargetTimeChanged?.Invoke(this, (_timing.TargetMin, _timing.TargetSecond, _timing.TargetTick));
        }
    }
}