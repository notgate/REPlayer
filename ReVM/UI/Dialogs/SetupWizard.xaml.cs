using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace ReVM;

public partial class SetupWizard : Window
{
    private readonly IAndroidRuntimeBackend? _engine;
    private bool _bootMode;
    private bool _setupComplete;
    private bool _setupStarted;
    private int _lastProgress;
    private const int MaxDebugLines = 240;
    private readonly Queue<string> _debugLines = new();
    private string _lastDebugPayload = string.Empty;
    private string _headlineTarget = "Preparing REPlayer";
    private CancellationTokenSource? _headlineAnimationCts;
    private readonly TaskCompletionSource<bool> _setupCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool SetupComplete => _setupComplete;
    internal bool IsBootMode => _bootMode;
    public event Action? BootCancelRequested;

    public SetupWizard() : this(false)
    {
    }

    internal SetupWizard(bool bootMode)
    {
        InitializeComponent();
        _bootMode = bootMode;
        if (bootMode)
        {
            _setupCompletion.TrySetResult(true);
        }
        else
        {
            _engine = RuntimeBackendFactory.Create();
            _engine.StatusChanged += msg => Dispatcher.BeginInvoke(() => HandleEngineStatus(msg));
        }
        Loaded += SetupWizard_Loaded;
        Closed += (_, _) => _headlineAnimationCts?.Cancel();
        SizeChanged += (_, _) => UpdateSetupProgressGeometry(_lastProgress);
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void ReplaceText(string currentText, string replacement)
    {
        ReplaceTextCore(SetupRoot, currentText, replacement);
    }

    private static bool ReplaceTextCore(DependencyObject parent, string currentText, string replacement)
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is TextBlock textBlock && string.Equals(textBlock.Text, currentText, StringComparison.Ordinal))
            {
                textBlock.Text = replacement;
                return true;
            }
            if (ReplaceTextCore(child, currentText, replacement)) return true;
        }
        return false;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_bootMode)
            BootCancelRequested?.Invoke();
        else
            _setupCompletion.TrySetResult(false);
        Close();
    }

    private async void SetupWizard_Loaded(object sender, RoutedEventArgs e)
    {
        if (_setupStarted) return;
        _setupStarted = true;
        if (_bootMode) ApplyBootModeCopy();
        await PlayEntranceAnimationAsync();
        if (_bootMode)
        {
            SetSetupProgress(2, "Preparing cold boot");
            return;
        }
        await VerifyBaseImageAsync();
    }

    private async Task PlayEntranceAnimationAsync()
    {
        try
        {
            SetupTitleBar.Opacity = 0;
            SetupBody.Opacity = 0;
            SetupTitleBarTranslate.Y = -18;
            SetupBodyTranslate.Y = 16;
            EntranceRevealScale.ScaleX = 0.015;
            EntranceRevealScale.ScaleY = 0.015;

            var revealEase = new QuarticEase { EasingMode = EasingMode.EaseOut };
            var contentEase = new CubicEase { EasingMode = EasingMode.EaseOut };
            var revealDuration = TimeSpan.FromMilliseconds(420);
            var contentDuration = TimeSpan.FromMilliseconds(360);

            EntranceRevealScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty,
                new DoubleAnimation(1, revealDuration) { EasingFunction = revealEase });
            EntranceRevealScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty,
                new DoubleAnimation(1, revealDuration) { EasingFunction = revealEase });

            await Task.Delay(90);

            SetupTitleBar.BeginAnimation(OpacityProperty,
                new DoubleAnimation(1, contentDuration) { EasingFunction = contentEase });
            SetupBody.BeginAnimation(OpacityProperty,
                new DoubleAnimation(1, contentDuration) { EasingFunction = contentEase });
            SetupTitleBarTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
                new DoubleAnimation(0, contentDuration) { EasingFunction = contentEase });
            SetupBodyTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
                new DoubleAnimation(0, contentDuration) { EasingFunction = contentEase });

            await Task.Delay(390);
            SetupTitleBar.Opacity = 1;
            SetupBody.Opacity = 1;
            SetupTitleBarTranslate.Y = 0;
            SetupBodyTranslate.Y = 0;
        }
        catch
        {
            SetupTitleBar.Opacity = 1;
            SetupBody.Opacity = 1;
            SetupTitleBarTranslate.Y = 0;
            SetupBodyTranslate.Y = 0;
        }
    }

    private async Task VerifyBaseImageAsync()
    {
        try
        {
            BtnFinish.Visibility = Visibility.Collapsed;
            BtnCancel.Visibility = Visibility.Visible;
            SetSetupProgress(3, "Preparing Android runtime");
            SetupStatusText.Text = "Checking Google Android Emulator runtime...";

            var (success, message) = await (_engine ?? throw new InvalidOperationException("Setup runtime is unavailable.")).EnsureBaseImageAsync();
            if (!success)
            {
                SetupStatusText.Text = message;
                SetSetupProgress(Math.Max(_lastProgress, 8), "Runtime setup failed");
                BtnFinish.Content = "Retry";
                BtnFinish.Visibility = Visibility.Visible;
                BtnFinish.Click -= Finish_Click;
                BtnFinish.Click += Retry_Click;
                return;
            }

            SetSetupProgress(38, "Finalizing runtime");
            SetupStatusText.Text = "Android runtime verified.";
            await Task.Delay(50);

            _setupComplete = true;
            SetupStatusText.Text = "Runtime Ready";
            SetSetupProgress(40, "Runtime ready · starting Android");
            TransitionToBootMode();
            _setupCompletion.TrySetResult(true);
        }
        catch (Exception ex)
        {
            SetupStatusText.Text = ex.Message;
            SetSetupProgress(Math.Max(_lastProgress, 8), "Runtime setup failed");
            BtnFinish.Content = "Retry";
            BtnFinish.Visibility = Visibility.Visible;
            BtnFinish.IsEnabled = true;
            BtnFinish.Click -= Finish_Click;
            BtnFinish.Click -= Retry_Click;
            BtnFinish.Click += Retry_Click;
        }
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        BtnFinish.Click -= Retry_Click;
        await VerifyBaseImageAsync();
    }

    private void Finish_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    internal Task<bool> WaitForSetupCompletionAsync() => _setupCompletion.Task;

    internal void TransitionToBootMode()
    {
        if (!_bootMode)
        {
            _lastProgress = 0;
            UpdateSetupProgressGeometry(0);
            SetupProgressPercent.Text = "0%";
        }
        _bootMode = true;
        if (IsLoaded) ApplyBootModeCopy();
    }

    private void ApplyBootModeCopy()
    {
        Title = "REPlayer · Starting Android";
        SetupSubtitleText.Text = "Your Android workspace is starting.";
        SetupHelperText.Text = "This usually takes a moment.";
        QueueHeadline("Preparing Android");
        BtnFinish.Visibility = Visibility.Collapsed;
        BtnCancel.Visibility = Visibility.Visible;
        BtnCancel.Content = "Cancel";
    }

    internal async Task CompleteSetupOnlyAsync()
    {
        SetSetupProgress(100, "REPlayer ready");
        await Task.Delay(160);
        Close();
    }


    private async Task EnsureReverseEngineeringToolsAsync()
    {
        var fridaOk = await CommandSucceedsAsync("frida", "--version", 6000);
        var mitmOk = await CommandSucceedsAsync("mitmweb", "--version", 6000) ||
                     await CommandSucceedsAsync("mitmproxy", "--version", 6000);
        if (fridaOk && mitmOk)
        {
            SetupStatusText.Text = "Frida and mitmproxy tools already available.";
            return;
        }

        SetupStatusText.Text = "Installing frida-tools and mitmproxy with Python pip...";
        var packages = "install --user --upgrade frida-tools mitmproxy";
        var installed = await CommandSucceedsAsync("py", "-3 -m pip " + packages, 180000) ||
                        await CommandSucceedsAsync("python", "-m pip " + packages, 180000) ||
                        await CommandSucceedsAsync("python3", "-m pip " + packages, 180000);
        if (!installed)
        {
            SetupStatusText.Text = "Frida/mitmproxy install skipped: Python pip is not available. Install Python and run: py -3 -m pip install --user frida-tools mitmproxy";
            return;
        }

        fridaOk = await CommandSucceedsAsync("frida", "--version", 6000);
        mitmOk = await CommandSucceedsAsync("mitmweb", "--version", 6000) ||
                 await CommandSucceedsAsync("mitmproxy", "--version", 6000);
        SetupStatusText.Text = fridaOk && mitmOk
            ? "Frida and mitmproxy tools installed."
            : "Frida/mitmproxy installed; restart REPlayer or add the Python Scripts folder to PATH if buttons still report missing tools.";
    }

    private static async Task<bool> CommandSucceedsAsync(string fileName, string arguments, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            var waitTask = proc.WaitForExitAsync();
            var done = await Task.WhenAny(waitTask, Task.Delay(timeoutMs));
            if (done != waitTask)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return false;
            }
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    internal static string UserFacingBootStage(int percent) => percent switch
    {
        >= 100 => "Android ready",
        >= 90 => "Almost ready",
        >= 32 => "Android is booting",
        _ => "Preparing Android"
    };

    private static string UserFacingSetupStage(int percent) => percent switch
    {
        >= 38 => "Ready to launch",
        >= 12 => "Setting things up",
        _ => "Preparing REPlayer"
    };

    private void QueueHeadline(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || string.Equals(_headlineTarget, text, StringComparison.Ordinal)) return;
        _headlineTarget = text;
        _headlineAnimationCts?.Cancel();
        _headlineAnimationCts?.Dispose();
        _headlineAnimationCts = new CancellationTokenSource();
        _ = AnimateHeadlineToAsync(text, _headlineAnimationCts.Token);
    }

    // Typewriter transition: delete the previous state, then rewrite the next one.
    private async Task AnimateHeadlineToAsync(string text, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!Dispatcher.CheckAccess())
            {
                await Dispatcher.InvokeAsync(() => AnimateHeadlineToAsync(text, cancellationToken));
                return;
            }

            while (SetupHeadlineText.Text.Length > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SetupHeadlineText.Text = SetupHeadlineText.Text[..^1];
                await Task.Delay(14, cancellationToken);
            }
            foreach (var character in text)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SetupHeadlineText.Text += character;
                await Task.Delay(character == ' ' ? 24 : 34, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void HandleEngineStatus(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        AppendDebugLine(message);
        if (message.StartsWith("progress:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = message.Split('|', 3);
            if (parts.Length >= 3 && int.TryParse(parts[1], out var percent))
            {
                var displayedPercent = _bootMode ? percent : Math.Clamp(percent * 38 / 100, 3, 38);
                SetSetupProgress(displayedPercent, parts[2]);
                SetupStatusText.Text = parts[2];
                return;
            }
            if (parts.Length >= 3)
            {
                SetupStatusText.Text = parts[2];
                return;
            }
        }

        // Full backend diagnostics remain in runtime logs; the end-user surface
        // intentionally stays on one of the concise staged messages.
        SetupStatusText.Text = message;
    }

    public void ReportBootDebug(string message)
    {
        if (!_bootMode || string.IsNullOrWhiteSpace(message)) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ReportBootDebug(message));
            return;
        }
        AppendDebugLine(message);
    }

    private void AppendDebugLine(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        var payload = message.Trim().Replace("\r", " ").Replace("\n", " ");
        if (payload.StartsWith("progress:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = payload.Split('|', 3);
            if (parts.Length >= 3) payload = $"{parts[1]}%  {parts[2]}";
        }
        else if (payload.StartsWith("boot-debug:|", StringComparison.OrdinalIgnoreCase))
        {
            payload = payload["boot-debug:|".Length..];
        }
        if (string.Equals(payload, _lastDebugPayload, StringComparison.Ordinal)) return;
        _lastDebugPayload = payload;
        _debugLines.Enqueue($"[{DateTime.Now:HH:mm:ss.fff}] {payload}");
        while (_debugLines.Count > MaxDebugLines) _debugLines.Dequeue();
        SetupDebugText.Text = string.Join(Environment.NewLine, _debugLines);
        SetupDebugText.ScrollToEnd();
    }

    private void DebugDetails_Click(object sender, RoutedEventArgs e)
    {
        var show = DebugDetailsPanel.Visibility != Visibility.Visible;
        DebugDetailsPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        BtnDebugDetails.Content = show ? "Hide details" : "Debug details";
        if (show) SetupDebugText.ScrollToEnd();
    }

    private void CopyDebug_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(SetupDebugText.Text))
            Clipboard.SetText(SetupDebugText.Text);
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = Path.Combine(RevmPaths.BaseDir, "logs", "google-emulator-runtime.log");
            if (!File.Exists(path))
            {
                AppendDebugLine("Runtime log has not been created yet.");
                return;
            }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendDebugLine("Could not open runtime log: " + ex.Message);
        }
    }

    private void SetSetupProgress(int percent, string diagnosticLabel)
    {
        _lastProgress = Math.Max(_lastProgress, Math.Clamp(percent, 0, 100));
        var stage = _bootMode ? UserFacingBootStage(_lastProgress) : UserFacingSetupStage(_lastProgress);
        SetupProgressPercent.Text = $"{_lastProgress}%";
        SetupProgressLabel.Text = stage;
        QueueHeadline(stage);
        UpdateSetupProgressGeometry(_lastProgress);
        UpdateAndroidProgressVisual(_lastProgress);
    }

    private void UpdateAndroidProgressVisual(int percent)
    {
        var progress = Math.Clamp(percent, 0, 100) / 100.0;
        AndroidLogoTranslate.Y = 3 + progress * 10;
        AndroidProgressHalo.Opacity = 0.14 + progress * 0.64;
        AndroidLogoGlowPath.Opacity = 0.18 + progress * 0.66;
        AndroidLogoGlass.Opacity = 0.74 + progress * 0.26;

        if (AndroidProgressHalo.Effect is BlurEffect haloBlur)
            haloBlur.Radius = 18 + progress * 10;
        if (AndroidLogoGlowPath.Effect is BlurEffect logoBlur)
            logoBlur.Radius = 8 + progress * 14;
        if (AndroidLogoGlass.Effect is DropShadowEffect glassGlow)
        {
            glassGlow.BlurRadius = 10 + progress * 18;
            glassGlow.Opacity = 0.22 + progress * 0.55;
        }
    }

    public void ReportBootProgress(int percent, string label)
    {
        if (!_bootMode) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ReportBootProgress(percent, label));
            return;
        }
        SetSetupProgress(percent, label);
    }

    public async Task CompleteBootAsync()
    {
        if (!_bootMode) return;
        ReportBootProgress(100, "Android ready");
        await Task.Delay(160);
        _setupComplete = true;
        Close();
    }

    public void FailBoot(string diagnosticMessage)
    {
        if (!_bootMode) return;
        _headlineAnimationCts?.Cancel();
        _headlineTarget = "Android could not start";
        SetupHeadlineText.Text = _headlineTarget;
        SetupSubtitleText.Text = "Close REPlayer and try again.";
        SetupHelperText.Text = "Details were saved to the runtime log.";
        SetupStatusText.Text = diagnosticMessage;
        SetupProgressLabel.Text = "Unable to start Android";
        BtnCancel.Content = "Close";
    }

    private void UpdateSetupProgressGeometry(int percent)
    {
        try
        {
            var width = Math.Max(1, ActualWidth);
            SetupProgressTrack.Width = width;
            var fillWidth = Math.Clamp(width * percent / 100.0, 0, width);
            SetupProgressFill.Width = fillWidth;
            var pillLeft = Math.Clamp(fillWidth - (SetupProgressPercentPill.Width / 2.0), 0, Math.Max(0, width - SetupProgressPercentPill.Width));
            Canvas.SetLeft(SetupProgressPercentPill, pillLeft);
        }
        catch { }
    }
}
