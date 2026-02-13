using FluentDesigner.ECS.System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;

namespace FluentDesigner.Control;

public sealed partial class MultiSelectPanel : UserControl
{
    private MultiSelectController _controller;
    private bool _isLoaded;
    private RailIndexCurvePanel _indexCurvePanel;

    public event EventHandler SelectionModified;

    public MultiSelectPanel()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        if (_controller != null)
        {
            RailIndexCurveControl?.BindController(_controller);
        }
    }

    public void BindController(MultiSelectController controller)
    {
        _controller = controller;
        RefreshDisplay();
        if (_isLoaded && RailIndexCurveControl != null)
        {
            RailIndexCurveControl.BindController(controller);
            RailIndexCurveControl.RailIndexChanged -= OnRailIndexChanged;
            RailIndexCurveControl.RailIndexChanged += OnRailIndexChanged;
        }
    }

    public void RefreshDisplay()
    {
        if (_controller == null)
        {
            return;
        }

        var info = _controller.GetSelectionInfo();

        NoteCountText.Text = info.NoteCount.ToString();
        GroupCountText.Text = info.GroupCount.ToString();
        StartTimeText.Text = FormatTime(info.StartMin, info.StartSecond, info.StartTick);
        EndTimeText.Text = FormatTime(info.EndMin, info.EndSecond, info.EndTick);

        StartTimeInput.Text = StartTimeText.Text;
        EndTimeInput.Text = EndTimeText.Text;
        DistributeStartInput.Text = StartTimeText.Text;
        DistributeEndInput.Text = EndTimeText.Text;

        if (_isLoaded)
        {
            RailIndexCurveControl?.RefreshData();
        }
    }

    private void OnRailIndexChanged(object sender, EventArgs e)
    {
        SelectionModified?.Invoke(this, EventArgs.Empty);
    }

    private static string FormatTime(float min, float sec, float tick)
    {
        return $"{(int)min:D2}:{(int)sec:D2}:{(int)tick:D3}";
    }

    private static (float min, float sec, float tick)? ParseTime(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var parts = input.Split(':');
        if (parts.Length != 3)
        {
            return null;
        }

        if (float.TryParse(parts[0], out var min) &&
            float.TryParse(parts[1], out var sec) &&
            float.TryParse(parts[2], out var tick))
        {
            return (min, sec, tick);
        }

        return null;
    }

    private void OnMinDecrease(object sender, RoutedEventArgs e)
    {
        _controller?.OffsetAllTargetTime(-1, 0, 0);
        RefreshDisplay();
        SelectionModified?.Invoke(this, EventArgs.Empty);
        _controller.NotifyTimingChanged();
    }

    private void OnMinIncrease(object sender, RoutedEventArgs e)
    {
        _controller?.OffsetAllTargetTime(1, 0, 0);
        RefreshDisplay();
        SelectionModified?.Invoke(this, EventArgs.Empty);
        _controller.NotifyTimingChanged();
    }

    private void OnSecDecrease(object sender, RoutedEventArgs e)
    {
        _controller?.OffsetAllTargetTime(0, -1, 0);
        RefreshDisplay();
        SelectionModified?.Invoke(this, EventArgs.Empty);
        _controller.NotifyTimingChanged();
    }

    private void OnSecIncrease(object sender, RoutedEventArgs e)
    {
        _controller?.OffsetAllTargetTime(0, 1, 0);
        RefreshDisplay();
        SelectionModified?.Invoke(this, EventArgs.Empty);
        _controller.NotifyTimingChanged();
    }

    private void OnTickDecrease(object sender, RoutedEventArgs e)
    {
        _controller?.OffsetAllTargetTime(0, 0, -1);
        RefreshDisplay();
        SelectionModified?.Invoke(this, EventArgs.Empty);
        _controller.NotifyTimingChanged();
    }

    private void OnTickIncrease(object sender, RoutedEventArgs e)
    {
        _controller?.OffsetAllTargetTime(0, 0, 1);
        RefreshDisplay();
        SelectionModified?.Invoke(this, EventArgs.Empty);
        _controller.NotifyTimingChanged();
    }

    private void OnApplyStartTime(object sender, RoutedEventArgs e)
    {
        var parsed = ParseTime(StartTimeInput.Text);
        if (parsed == null)
        {
            return;
        }

        _controller?.SetStartTime(parsed.Value.min, parsed.Value.sec, parsed.Value.tick);
        RefreshDisplay();
        SelectionModified?.Invoke(this, EventArgs.Empty);
    }

    private void OnApplyEndTime(object sender, RoutedEventArgs e)
    {
        var parsed = ParseTime(EndTimeInput.Text);
        if (parsed == null)
        {
            return;
        }

        _controller?.SetEndTime(parsed.Value.min, parsed.Value.sec, parsed.Value.tick);
        RefreshDisplay();
        SelectionModified?.Invoke(this, EventArgs.Empty);
    }

    private void OnDistributeClick(object sender, RoutedEventArgs e)
    {
        var startParsed = ParseTime(DistributeStartInput.Text);
        var endParsed = ParseTime(DistributeEndInput.Text);

        if (startParsed == null || endParsed == null)
        {
            return;
        }

        _controller?.DistributeEvenly(
            startParsed.Value.min, startParsed.Value.sec, startParsed.Value.tick,
            endParsed.Value.min, endParsed.Value.sec, endParsed.Value.tick);

        RefreshDisplay();
        SelectionModified?.Invoke(this, EventArgs.Empty);
    }
}