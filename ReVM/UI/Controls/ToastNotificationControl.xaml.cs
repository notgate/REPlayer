using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace ReVM;

public partial class ToastNotificationControl : UserControl
{
    public static readonly DependencyProperty ToastProperty = DependencyProperty.Register(
        nameof(Toast),
        typeof(ToastNotificationViewModel),
        typeof(ToastNotificationControl),
        new PropertyMetadata(null, OnToastChanged));

    public event RoutedEventHandler? DismissRequested;

    public ToastNotificationViewModel? Toast
    {
        get => (ToastNotificationViewModel?)GetValue(ToastProperty);
        set => SetValue(ToastProperty, value);
    }

    public ToastNotificationControl()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateProgressWidth();
        SizeChanged += (_, _) => UpdateProgressWidth();
    }

    public async Task ShowAnimatedFromBottomAsync()
    {
        UpdateProgressWidth();
        await AnimateAsync(fromY: 28, toY: 0, fromOpacity: 0, toOpacity: 1, milliseconds: 260, EasingMode.EaseOut);
    }

    public async Task HideAnimatedToBottomAsync()
    {
        await AnimateAsync(fromY: ToastTranslate.Y, toY: 28, fromOpacity: ToastRoot.Opacity, toOpacity: 0, milliseconds: 210, EasingMode.EaseIn);
    }

    private Task AnimateAsync(double fromY, double toY, double fromOpacity, double toOpacity, int milliseconds, EasingMode easingMode)
    {
        var tcs = new TaskCompletionSource<object?>();
        var easing = new QuarticEase { EasingMode = easingMode };
        var duration = TimeSpan.FromMilliseconds(milliseconds);
        var yAnimation = new DoubleAnimation(fromY, toY, duration) { EasingFunction = easing };
        var opacityAnimation = new DoubleAnimation(fromOpacity, toOpacity, duration) { EasingFunction = easing };
        var completed = false;
        opacityAnimation.Completed += (_, _) =>
        {
            if (completed) return;
            completed = true;
            tcs.TrySetResult(null);
        };
        ToastTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, yAnimation, HandoffBehavior.SnapshotAndReplace);
        ToastRoot.BeginAnimation(OpacityProperty, opacityAnimation, HandoffBehavior.SnapshotAndReplace);
        return tcs.Task;
    }

    private static void OnToastChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (ToastNotificationControl)d;
        if (e.OldValue is ToastNotificationViewModel oldVm)
            oldVm.PropertyChanged -= control.OnToastPropertyChanged;
        if (e.NewValue is ToastNotificationViewModel newVm)
            newVm.PropertyChanged += control.OnToastPropertyChanged;
        control.DataContext = e.NewValue;
        control.UpdateProgressWidth();
    }

    private void OnToastPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ToastNotificationViewModel.ProgressPercent) or nameof(ToastNotificationViewModel.Width) or nameof(ToastNotificationViewModel.ShowProgress))
            Dispatcher.InvokeAsync(UpdateProgressWidth);
    }

    private void UpdateProgressWidth()
    {
        if (Toast == null || ProgressFill == null || ProgressTrack == null) return;
        var trackWidth = Math.Max(1.0, ProgressTrack.ActualWidth);
        ProgressFill.Width = Math.Clamp(Toast.ProgressPercent, 0, 100) / 100.0 * trackWidth;
    }

    private void Dismiss_Click(object sender, RoutedEventArgs e) => DismissRequested?.Invoke(this, e);
}
