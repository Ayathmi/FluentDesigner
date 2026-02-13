using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using System;
using Windows.System;

namespace FluentDesigner.Control;

public sealed partial class TimeRulerPanel : UserControl
{
    private double _currentTime;

    public event EventHandler<double> SetInPointRequested;
    public event EventHandler<double> SetOutPointRequested;
    public event EventHandler ClearSelectionRequested;
    public event EventHandler<bool> SnapToggleChanged;
    public event EventHandler<double> TimeJumpRequested;
    public event EventHandler<bool> LoopToggleChanged;
    public bool IsLoopEnabled => LoopToggleButton.IsChecked == true;
    public bool IsSnapEnabled => SnapToggleButton.IsChecked == true;

    public TimeRulerPanel()
    {
        InitializeComponent();
    }

    public void UpdateTimeDisplay(double timeSeconds)
    {
        _currentTime = timeSeconds;
        TimeDisplayButton.Content = FormatTime(timeSeconds);
    }
    private void OnLoopToggleChanged(object sender, RoutedEventArgs e)
    {
        LoopToggleChanged?.Invoke(this, LoopToggleButton.IsChecked == true);
    }

    private void OnTimeDisplayClick(object sender, RoutedEventArgs e)
    {
        TimeDisplayButton.Visibility = Visibility.Collapsed;
        TimeInputBox.Visibility = Visibility.Visible;
        TimeInputBox.Text = FormatTime(_currentTime);
        TimeInputBox.SelectAll();
        TimeInputBox.Focus(FocusState.Programmatic);
    }

    private void OnTimeInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            CommitTimeInput();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            CancelTimeInput();
            e.Handled = true;
        }
    }

    private void OnTimeInputLostFocus(object sender, RoutedEventArgs e)
    {
        CommitTimeInput();
    }

    private void CommitTimeInput()
    {
        if (TryParseTime(TimeInputBox.Text, out double seconds))
        {
            TimeJumpRequested?.Invoke(this, seconds);
        }

        TimeInputBox.Visibility = Visibility.Collapsed;
        TimeDisplayButton.Visibility = Visibility.Visible;
    }

    private void CancelTimeInput()
    {
        TimeInputBox.Visibility = Visibility.Collapsed;
        TimeDisplayButton.Visibility = Visibility.Visible;
    }

    private void OnSetInPointClick(object sender, RoutedEventArgs e)
    {
        SetInPointRequested?.Invoke(this, _currentTime);
    }

    private void OnSetOutPointClick(object sender, RoutedEventArgs e)
    {
        SetOutPointRequested?.Invoke(this, _currentTime);
    }

    private void OnClearSelectionClick(object sender, RoutedEventArgs e)
    {
        ClearSelectionRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnSnapToggleChanged(object sender, RoutedEventArgs e)
    {
        SnapToggleChanged?.Invoke(this, SnapToggleButton.IsChecked == true);
    }

    private static string FormatTime(double seconds)
    {
        if (seconds < 0) seconds = 0;
        int totalMs = (int)(seconds * 1000);
        int min = totalMs / 60000;
        int sec = (totalMs % 60000) / 1000;
        int ms = totalMs % 1000;
        return $"{min:D2}:{sec:D2}:{ms:D3}";
    }

    private static bool TryParseTime(string input, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        input = input.Trim().Replace('.', ':');
        var parts = input.Split(':');

        try
        {
            if (parts.Length == 3)
            {
                int min = int.Parse(parts[0]);
                int sec = int.Parse(parts[1]);
                int ms = int.Parse(parts[2].PadRight(3, '0').Substring(0, 3));
                seconds = min * 60 + sec + ms / 1000.0;
                return true;
            }
            else if (parts.Length == 2)
            {
                int sec = int.Parse(parts[0]);
                int ms = int.Parse(parts[1].PadRight(3, '0').Substring(0, 3));
                seconds = sec + ms / 1000.0;
                return true;
            }
            else if (parts.Length == 1)
            {
                seconds = double.Parse(parts[0]);
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }
}