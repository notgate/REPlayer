using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ReVM;

public sealed class ToastNotificationViewModel : INotifyPropertyChanged
{
    private string _title = string.Empty;
    private string _message = string.Empty;
    private string _iconData = string.Empty;
    private int _progressPercent;
    private bool _showProgress;
    private bool _isError;
    private double _width = 320;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Title
    {
        get => _title;
        set => SetField(ref _title, value);
    }

    public string Message
    {
        get => _message;
        set => SetField(ref _message, value);
    }

    public string IconData
    {
        get => _iconData;
        set => SetField(ref _iconData, value);
    }

    public int ProgressPercent
    {
        get => _progressPercent;
        set => SetField(ref _progressPercent, Math.Clamp(value, 0, 100));
    }

    public bool ShowProgress
    {
        get => _showProgress;
        set => SetField(ref _showProgress, value);
    }

    public bool IsError
    {
        get => _isError;
        set => SetField(ref _isError, value);
    }

    public double Width
    {
        get => _width;
        set => SetField(ref _width, Math.Max(220, value));
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
