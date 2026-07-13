using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using ReVM.Automation;

namespace ReVM;

public partial class MainWindow : Window
{
    private readonly IAndroidRuntimeBackend _engine;
    private readonly ObservableCollection<VmInstance> _instances = new();
    private VmInstance? _activeVm;
    private bool _isHorizontal;
    private IntPtr _windowHandle;
    private bool _baseImageReady;
    private bool _startInProgress;
    private bool _sidebarCollapsed;
    private bool _miniMode;
    private bool _debugStripVisible = false;
    private bool _rendererDisplayReady;
    private bool _androidPendingLatched;
    private bool _launcherPresentationActive;
    private bool _suppressingMainWindow;
    private System.Windows.Threading.DispatcherTimer? _nativeMainWindowGuardTimer;
    private SetupWizard? _coldBootLauncher;
    private SetupWizard? _wiredColdBootLauncher;
    private System.Windows.Threading.DispatcherTimer? _bootProgressTimer;
    private int _bootDisplayedProgress;
    private int _bootTargetProgress;
    private int _bootIdleTicks;
    private string _bootTargetLabel = "Preparing";
    private bool _shutdownStarted;
    private bool _closeConfirmed;
    private bool _orientationChanging;
    private bool _aspectSizingActive;
    private bool _viewportFitMaximized;
    private bool _embeddedWindowRepositionQueued;
    private System.Windows.Threading.DispatcherTimer? _embeddedContainmentTimer;
    private int _screenFlipRotation;
    private double _normalWidth;
    private double _normalHeight;
    private Rect _viewportRestoreBounds;
    private HwndSource? _mainWindowSource;
    private double _configuredViewportWidth;
    private double _configuredViewportHeight;
    private System.Timers.Timer? _resizeDebounce;
    private readonly ObservableCollection<string> _rendererDebugLines = new();
    private const int AndroidScanoutWidth = 1024;
    private const int AndroidScanoutHeight = 768;
    private const double TitleBarHeightDip = 26;
    private const double ToolbarWidthDip = 30;
    private const double DebugStripHeightDip = 118;
    private Int32Rect _androidDisplayRectPixels = new(0, 0, AndroidScanoutWidth, AndroidScanoutHeight);
    private IntPtr _activeNativeWindow;
    private Process? _recordingProcess;
    private AndroidAgentCoordinator? _agentCoordinator;
    private AndroidAgentInbox? _agentInbox;
    private AgentCenterDialog? _agentCenterDialog;
    private string? _agentCoordinatorSerial;
    private string? _recordingDevicePath;
    private string? _recordingOutputPath;
    private CancellationTokenSource? _androidBootToastHideCts;
    private int _apkInstallProgressPercent;
    private string _apkInstallApkName = string.Empty;
    private string _apkInstallSubtextValue = string.Empty;
    private readonly ToastNotificationViewModel _androidBootToast = new()
    {
        Title = "Android is booting",
        Message = "Please be patient as it loads",
        IconData = "M8,2 L13,5 L13,13 L3,13 L3,5 Z M6,7 L10,7 M8,5 L8,11",
        Width = AndroidBootToastWidthDip
    };
    private readonly ToastNotificationViewModel _apkInstallToast = new()
    {
        Title = "Installing APK",
        Message = "Preparing install",
        IconData = ApkInstallIcon,
        ShowProgress = true,
        Width = ApkInstallToastWidthDip
    };

    private const string PortraitOrientationIcon = "M4,2 L10,2 L10,14 L4,14 Z M5.5,12.5 L8.5,12.5 M11.5,4.5 L14,7 L11.5,9.5 M14,7 L11,7";
    private const string LandscapeOrientationIcon = "M2,5 L14,5 L14,11 L2,11 Z M12.5,2.5 L15,5 L12.5,7.5 M15,5 L12,5 M3.5,13.5 L1,11 L3.5,8.5 M1,11 L4,11";
    private const string ApkInstallIcon = "M4,2 L12,2 L14,4 L14,14 L2,14 L2,4 Z M4,2 L4,4 L2,4 M6,7 L10,7 M8,5 L8,11";
    private const string ToastErrorIcon = "M8,2 L14,13 L2,13 Z M8,5 L8,9 M8,11 L8,12";
    private const int AndroidBootToastVisibleMs = 5000;
    private const double AndroidBootToastWidthDip = 336.0;
    private const double ApkInstallToastWidthDip = 306.0;

    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_CHAR = 0x0102;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int WM_SIZING = 0x0214;
    private const uint MAPVK_VK_TO_VSC = 0;

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("user32.dll")]
    private static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_HIDE = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    public MainWindow()
    {
        InitializeComponent();
        Opacity = 0;
        ShowInTaskbar = false;
        ShowActivated = false;
        Activated += MainWindow_Activated;
        IsVisibleChanged += MainWindow_IsVisibleChanged;
        AndroidBootToastView.Toast = _androidBootToast;
        ApkInstallToastView.Toast = _apkInstallToast;
        InstanceList.ItemsSource = _instances;
        _engine = RuntimeBackendFactory.Create();
        _engine.WindowCreated += OnEngineWindowCreated;
        _engine.StatusChanged += msg => Dispatcher.BeginInvoke(() => HandleEngineStatus(msg));
        DisplayHost.AndroidTouchRequested += OnAndroidTouchRequested;
        Loaded += OnLoaded;
        StateChanged += OnStateChanged;
        SizeChanged += (_, _) =>
        {
            ApplyViewportSizingForWindowState();
            QueueEmbeddedWindowReposition();
        };
        LocationChanged += (_, _) => QueueEmbeddedWindowReposition();
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewKeyUp += OnPreviewKeyUp;
        PreviewTextInput += OnPreviewTextInput;
        Closing += OnWindowClosing;
    }

    private double GetWindowDpiScale()
    {
        try
        {
            if (_windowHandle != IntPtr.Zero)
                return Math.Max(0.25, GetDpiForWindow(_windowHandle) / 96.0);
            return Math.Max(0.25, VisualTreeHelper.GetDpi(this).DpiScaleX);
        }
        catch
        {
            return 1.0;
        }
    }


    private void OnAndroidTouchRequested(int fromX, int fromY, int toX, int toY, int durationMs)
    {
        if (_activeVm == null) return;
        var dpi = _windowHandle == IntPtr.Zero ? 96 : GetDpiForWindow(_windowHandle);
        var scale = dpi / 96.0;
        var rect = _androidDisplayRectPixels;
        static int Map(int value, int origin, int source, int target) =>
            Math.Clamp((value - origin) * target / Math.Max(1, source), 0, target - 1);
        var ax0 = Map(fromX, rect.X, rect.Width, AndroidScanoutWidth);
        var ay0 = Map(fromY, rect.Y, rect.Height, AndroidScanoutHeight);
        var ax1 = Map(toX, rect.X, rect.Width, AndroidScanoutWidth);
        var ay1 = Map(toY, rect.Y, rect.Height, AndroidScanoutHeight);
        _ = _engine.SendAndroidTouchAsync(_activeVm.Id, ax0, ay0, ax1, ay1, durationMs);
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            HideAndroidBootToast(immediate: true);
            HideApkInstallToast(immediate: true);
        }
        if (WindowState == WindowState.Maximized && !_aspectSizingActive)
        {
            Dispatcher.BeginInvoke(FitWindowToWorkArea);
            return;
        }
        UpdateMaximizeIcon();
        ApplyViewportSizingForWindowState();
        RepositionEmbeddedWindow();
    }

    private void UpdateMaximizeIcon()
    {
        if (MaxIcon == null) return;
        MaxIcon.Data = _viewportFitMaximized
            ? Geometry.Parse("M3,3 L7,3 L7,7 L3,7 Z M7,5 L11,5 L11,11 L7,11 Z")
            : Geometry.Parse("M2,2 L12,2 L12,12 L2,12 Z");
    }

    private double GetConfiguredViewportAspect()
    {
        var settings = ReadBackendSettingsForWindow();
        var nativeProfile = OrientedDisplaySize(settings);
        double width = nativeProfile.Width;
        double height = nativeProfile.Height;
        return Math.Clamp(width / Math.Max(1.0, height), 0.2, 5.0);
    }

    private IntPtr MainWindowWindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WM_SIZING || lParam == IntPtr.Zero || WindowState != WindowState.Normal ||
            _miniMode || _aspectSizingActive || ReadBackendSettingsForWindow().LockWindowSize)
            return IntPtr.Zero;

        var native = Marshal.PtrToStructure<RECT>(lParam);
        var bounds = new Int32Rect(native.Left, native.Top,
            Math.Max(1, native.Right - native.Left), Math.Max(1, native.Bottom - native.Top));
        var scale = GetWindowDpiScale();
        var chromeWidth = (int)Math.Round((_sidebarCollapsed ? 0 : ToolbarWidthDip) * scale);
        var chromeHeight = (int)Math.Round((TitleBarHeightDip + (_debugStripVisible ? DebugStripHeightDip : 0)) * scale);
        var displayProfile = OrientedDisplaySize(ReadBackendSettingsForWindow());
        var constrained = ConstrainWindowRectToViewportAspect(bounds, wParam.ToInt32(),
            displayProfile.Width, displayProfile.Height, chromeWidth, chromeHeight);
        native.Left = constrained.X;
        native.Top = constrained.Y;
        native.Right = constrained.X + constrained.Width;
        native.Bottom = constrained.Y + constrained.Height;
        Marshal.StructureToPtr(native, lParam, false);
        handled = true;
        return IntPtr.Zero;
    }

    private static Int32Rect ConstrainWindowRectToViewportAspect(
        Int32Rect bounds, int sizingEdge, int displayWidth, int displayHeight, int chromeWidth, int chromeHeight)
    {
        var outerWidth = Math.Max(chromeWidth + 1, bounds.Width);
        var outerHeight = Math.Max(chromeHeight + 1, bounds.Height);
        var contentWidth = Math.Max(1, outerWidth - chromeWidth);
        var contentHeight = Math.Max(1, outerHeight - chromeHeight);

        static int Gcd(int a, int b)
        {
            while (b != 0) (a, b) = (b, a % b);
            return Math.Max(1, Math.Abs(a));
        }

        displayWidth = Math.Max(1, displayWidth);
        displayHeight = Math.Max(1, displayHeight);
        var divisor = Gcd(displayWidth, displayHeight);
        var unitWidth = displayWidth / divisor;
        var unitHeight = displayHeight / divisor;
        var widthMultiplier = Math.Max(1, (int)Math.Round(contentWidth / (double)unitWidth,
            MidpointRounding.AwayFromZero));
        var heightMultiplier = Math.Max(1, (int)Math.Round(contentHeight / (double)unitHeight,
            MidpointRounding.AwayFromZero));
        var heightFromWidth = unitHeight * widthMultiplier + chromeHeight;
        var widthFromHeight = unitWidth * heightMultiplier + chromeWidth;

        var verticalEdge = sizingEdge is 3 or 6;
        var horizontalEdge = sizingEdge is 1 or 2;
        var adjustHeight = horizontalEdge || (!verticalEdge && Math.Abs(heightFromWidth - outerHeight) <= Math.Abs(widthFromHeight - outerWidth));
        var result = bounds;
        if (adjustHeight)
        {
            result.Width = unitWidth * widthMultiplier + chromeWidth;
            result.Height = unitHeight * widthMultiplier + chromeHeight;
        }
        else
        {
            result.Width = unitWidth * heightMultiplier + chromeWidth;
            result.Height = unitHeight * heightMultiplier + chromeHeight;
        }
        if (sizingEdge is 1 or 4 or 7)
            result.X = bounds.X + bounds.Width - result.Width;
        if (sizingEdge is 3 or 4 or 5)
            result.Y = bounds.Y + bounds.Height - result.Height;
        return result;
    }

    private void ApplyDebugStripVisibility()
    {
        DebugStatusRow.Height = _debugStripVisible ? new GridLength(118) : new GridLength(0);
        StatusBar.Visibility = _debugStripVisible ? Visibility.Visible : Visibility.Collapsed;
        RendererDebugPanel.Visibility = _debugStripVisible ? Visibility.Visible : Visibility.Collapsed;
        DebugToggleBtn.Tag = _debugStripVisible ? "Active" : null;
        BottomDebugToggleBtn.Tag = _debugStripVisible ? "Active" : null;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _windowHandle = new WindowInteropHelper(this).Handle;
        _mainWindowSource = HwndSource.FromHwnd(_windowHandle);
        _mainWindowSource?.AddHook(MainWindowWindowProc);
        ApplyDebugStripVisibility();
        DisplayHost.SizeChanged += (_, _) =>
        {
            RepositionEmbeddedWindow();
            if (AndroidBootToastPopup?.IsOpen == true) ShowAndroidBootToast();
            if (ApkInstallToastPopup?.IsOpen == true) ShowApkInstallToast(_apkInstallApkName, _apkInstallSubtextValue, _apkInstallProgressPercent);
            UpdateBootProgressGeometry((int)Math.Round(BootProgressFill.Width / Math.Max(1, BootProgressTrack.Width) * 100));
            if (_activeVm != null && !_orientationChanging && !UsesOfficialEmulatorNativeWindow())
            {
                _resizeDebounce?.Stop();
                _resizeDebounce?.Start();
            }
        };
        BootProgressOverlay.SizeChanged += (_, _) =>
            UpdateBootProgressGeometry((int)Math.Round(BootProgressFill.Width / Math.Max(1, BootProgressTrack.Width) * 100));

        _resizeDebounce = new System.Timers.Timer(1200) { AutoReset = false };
        _resizeDebounce.Elapsed += (_, _) => Dispatcher.Invoke(() =>
        {
            if (_activeVm == null || _orientationChanging || UsesOfficialEmulatorNativeWindow()) return;
            var dpi = GetDpiForWindow(_windowHandle);
            var scale = dpi / 96.0;
            int w = (int)(DisplayHost.ActualWidth * scale);
            int h = (int)(DisplayHost.ActualHeight * scale);
            if (w > 0 && h > 0)
                _ = _engine.SetAndroidResolutionAsync(_activeVm.Id, w, h);
        });

        try
        {
            StatusText.Text = "Checking Android runtime...";
            var wizard = new SetupWizard();
            _coldBootLauncher = wizard;
            WireColdBootLauncher(wizard);
            _launcherPresentationActive = true;
            wizard.Show();
            SuppressMainWindowForLauncher();

            var setupComplete = await wizard.WaitForSetupCompletionAsync();
            if (!setupComplete)
            {
                _baseImageReady = false;
                StatusText.Text = "Setup cancelled.";
                ShowMainWindowAfterLauncher();
                return;
            }
            _baseImageReady = _engine.CheckEngine() && _engine.IsBaseImageReady();
            if (!_baseImageReady)
            {
                StatusText.Text = "Setup incomplete. Run setup-google-emulator-runtime.ps1 or restart to retry.";
                wizard.FailBoot(StatusText.Text);
                return;
            }

            StatusText.Text = "Ready";
            RefreshInstances();

            // Continue directly from dependency setup into Android cold boot.
            // The same launcher stays on screen until Android is authoritative-ready.
            var firstVm = _instances.FirstOrDefault();
            var startupSettings = ReadBackendSettingsForWindow();
            if (firstVm != null && startupSettings.AutoStartAndroid)
            {
                if (!startupSettings.ColdBootLauncherEnabled)
                {
                    await wizard.CompleteSetupOnlyAsync();
                    if (ReferenceEquals(_coldBootLauncher, wizard)) _coldBootLauncher = null;
                    ShowMainWindowAfterLauncher();
                }
                ApplyStartupWindowOrientation();
                ApplyWindowSizeFromBackendSettings();
                await StartVmAsync(firstVm);
            }
            else
            {
                await wizard.CompleteSetupOnlyAsync();
                if (ReferenceEquals(_coldBootLauncher, wizard)) _coldBootLauncher = null;
                ShowMainWindowAfterLauncher();
                StatusText.Text = firstVm != null
                    ? "Ready · select Start to cold boot Android"
                    : "Ready";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            if (_coldBootLauncher is { IsVisible: true } launcher)
                launcher.FailBoot(ex.Message);
            else
                ShowMainWindowAfterLauncher();
        }

    }

    private void ShowMainWindowAfterLauncher()
    {
        if (_shutdownStarted || _closeConfirmed) return;
        if (_coldBootLauncher is { IsVisible: true })
        {
            SuppressMainWindowForLauncher();
            return;
        }
        _launcherPresentationActive = false;
        _nativeMainWindowGuardTimer?.Stop();
        ShowActivated = true;
        Opacity = 1;
        ShowInTaskbar = true;
        if (!IsVisible) Show();
        Activate();
    }

    private void MainWindow_Activated(object? sender, EventArgs e)
    {
        if (_launcherPresentationActive)
            Dispatcher.BeginInvoke(SuppressMainWindowForLauncher);
    }

    private void MainWindow_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_launcherPresentationActive && IsVisible)
            Dispatcher.BeginInvoke(SuppressMainWindowForLauncher);
    }

    private void SuppressMainWindowForLauncher()
    {
        if (!_launcherPresentationActive || _suppressingMainWindow) return;
        _suppressingMainWindow = true;
        try
        {
            ShowActivated = false;
            Opacity = 0;
            if (IsVisible) Hide();
            ShowInTaskbar = false;
            HideNativeMainWindow();
            EnsureNativeMainWindowGuard();
            if (_coldBootLauncher is { IsVisible: true } launcher)
                launcher.Activate();
        }
        finally
        {
            _suppressingMainWindow = false;
        }
    }

    private void EnsureNativeMainWindowGuard()
    {
        _nativeMainWindowGuardTimer ??= new System.Windows.Threading.DispatcherTimer(
            TimeSpan.FromMilliseconds(16),
            System.Windows.Threading.DispatcherPriority.Send,
            (_, _) =>
            {
                if (_launcherPresentationActive) HideNativeMainWindow();
                else _nativeMainWindowGuardTimer?.Stop();
            },
            Dispatcher);
        _nativeMainWindowGuardTimer.Start();
    }

    private void HideNativeMainWindow()
    {
        var hwnd = _windowHandle;
        if (hwnd == IntPtr.Zero)
        {
            try { hwnd = new WindowInteropHelper(this).Handle; }
            catch { return; }
        }
        if (hwnd != IntPtr.Zero) ShowWindow(hwnd, SW_HIDE);
    }

    private void ApplyStartupWindowOrientation()
    {
        try
        {
            var settingsPath = Path.Combine(RuntimeBackendFactory.GetBaseDir(), "runtime", "backend-settings.json");
            if (!File.Exists(settingsPath)) return;
            var settings = JsonSerializer.Deserialize<BackendSettings>(File.ReadAllText(settingsPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (settings == null || _miniMode) return;
            _isHorizontal = !string.Equals(settings.BootProfile, "api34-persona", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(settings.ResolutionMode, "portrait", StringComparison.OrdinalIgnoreCase);
            UpdateOrientationIcon();
        }
        catch { }
    }


    private void UpdateOrientationIcon()
    {
        if (OrientIcon != null)
            OrientIcon.Data = Geometry.Parse(_isHorizontal ? LandscapeOrientationIcon : PortraitOrientationIcon);
    }

    private BackendSettings ReadBackendSettingsForWindow()
    {
        try
        {
            var settingsPath = Path.Combine(RuntimeBackendFactory.GetBaseDir(), "runtime", "backend-settings.json");
            if (!File.Exists(settingsPath)) return BackendSettingsValidation.ValidateAndNormalize(new BackendSettings());
            var settings = JsonSerializer.Deserialize<BackendSettings>(File.ReadAllText(settingsPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new BackendSettings();
            return BackendSettingsValidation.ValidateAndNormalize(settings);
        }
        catch
        {
            return BackendSettingsValidation.ValidateAndNormalize(new BackendSettings());
        }
    }

    private static (int Width, int Height) OrientedDisplaySize(BackendSettings settings)
    {
        return string.Equals(settings.BootProfile, "api34-persona", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(settings.ResolutionMode, "portrait", StringComparison.OrdinalIgnoreCase)
            ? (1080, 2400)
            : (1920, 1200);
    }

    private void ApplyWindowSizeFromBackendSettings()
    {
        if (_miniMode) return;
        var settings = ReadBackendSettingsForWindow();
        var (displayW, displayH) = OrientedDisplaySize(settings);

        // DisplayWidth/DisplayHeight are Android physical pixels. WPF sizes are
        // DIPs, so divide by monitor DPI before locking the window; otherwise a
        // 720x1280 portrait skin becomes a 1080x1920 host on a 150% display and
        // the embedded native emulator appears offset/mis-sized.
        var dpiScale = GetWindowDpiScale();
        var displayDipW = displayW / dpiScale;
        var displayDipH = displayH / dpiScale;

        var toolbarW = _sidebarCollapsed ? 0 : ToolbarWidthDip;
        var debugH = _debugStripVisible ? DebugStripHeightDip : 0;
        var chromeW = toolbarW;
        var chromeH = TitleBarHeightDip + debugH;

        var windowMargin = displayH > displayW ? 260.0 : 120.0;
        var maxDisplayW = Math.Max(320, SystemParameters.WorkArea.Width - chromeW - windowMargin);
        var maxDisplayH = Math.Max(240, SystemParameters.WorkArea.Height - chromeH - windowMargin);
        var scale = Math.Min(1.0, Math.Min(maxDisplayW / displayDipW, maxDisplayH / displayDipH));
        var viewportW = Math.Round(displayDipW * scale);
        var viewportH = Math.Round(displayDipH * scale);
        // Snap the physical host up to the nearest exact reduced profile multiple.
        // A 423x939 portrait host otherwise rounds the 9:20 scanout down to
        // 414x920. Making the host 423x940 removes the border without stretching.
        static int Gcd(int a, int b)
        {
            while (b != 0) (a, b) = (b, a % b);
            return Math.Max(1, Math.Abs(a));
        }
        var divisor = Gcd(displayW, displayH);
        var unitW = displayW / divisor;
        var unitH = displayH / divisor;
        var physicalMultiplier = Math.Max(1, (int)Math.Round(
            Math.Min((viewportW * dpiScale) / unitW, (viewportH * dpiScale) / unitH),
            MidpointRounding.AwayFromZero));
        viewportW = unitW * physicalMultiplier / dpiScale;
        viewportH = unitH * physicalMultiplier / dpiScale;
        if (DisplayViewport != null)
        {
            _configuredViewportWidth = viewportW;
            _configuredViewportHeight = viewportH;
            DisplayViewport.Width = viewportW;
            DisplayViewport.Height = viewportH;
        }
        if (BootProgressOverlay != null)
        {
            BootProgressOverlay.Width = viewportW;
            BootProgressOverlay.Height = viewportH;
        }
        var windowW = Math.Round(viewportW + chromeW);
        var windowH = Math.Round(viewportH + chromeH);

        MinWidth = 240;
        MinHeight = TitleBarHeightDip + 180;
        MaxWidth = double.PositiveInfinity;
        MaxHeight = double.PositiveInfinity;
        ResizeMode = settings.LockWindowSize ? ResizeMode.NoResize : ResizeMode.CanResize;
        if (_viewportFitMaximized)
        {
            FitWindowToWorkArea();
            return;
        }

        WindowState = WindowState.Normal;
        Width = Math.Max(MinWidth, windowW);
        Height = Math.Max(MinHeight, windowH);
        // WindowChrome, DPI resize borders, and the live toolbar consume more than
        // the nominal title/sidebar constants on some monitors. Measure that real
        // delta and correct the outer window so DisplayHost—not merely a centered
        // child inside it—lands on the exact native Android profile dimensions.
        for (var pass = 0; pass < 2; pass++)
        {
            UpdateLayout();
            DisplayHost.UpdateLayout();
            var measuredChromeW = Math.Max(0, ActualWidth - DisplayHost.ActualWidth);
            var measuredChromeH = Math.Max(0, ActualHeight - DisplayHost.ActualHeight);
            Width = Math.Max(MinWidth, viewportW + measuredChromeW);
            Height = Math.Max(MinHeight, viewportH + measuredChromeH);
        }
        Left = Math.Max(SystemParameters.WorkArea.Left, SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width - Width) / 2.0);
        Top = Math.Max(SystemParameters.WorkArea.Top, SystemParameters.WorkArea.Top + (SystemParameters.WorkArea.Height - Height) / 2.0);
        UpdateLayout();
        ApplyViewportSizingForWindowState();
        DisplayHost.UpdateLayout();
        RepositionEmbeddedWindow();
        Dispatcher.BeginInvoke((Action)RepositionEmbeddedWindow, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void ApplyViewportSizingForWindowState()
    {
        if (DisplayViewport == null || BootProgressOverlay == null) return;

        var parent = DisplayViewport.Parent as FrameworkElement;
        var availableWidth = parent?.ActualWidth ?? 0;
        var availableHeight = parent?.ActualHeight ?? 0;
        if (parent is Grid parentGrid && parentGrid.RowDefinitions.Count > 0)
            availableHeight = parentGrid.RowDefinitions[0].ActualHeight;
        if (availableWidth <= 1 || availableHeight <= 1)
        {
            availableWidth = Math.Max(1, _configuredViewportWidth);
            availableHeight = Math.Max(1, _configuredViewportHeight);
        }

        // Quantize in physical pixels before assigning WPF DIPs. Re-fitting with
        // floating-point aspect math here could turn an exact 423x940 portrait
        // viewport into a 423x941 HwndHost at 150% DPI, exposing a gray edge.
        var dpiScale = GetWindowDpiScale();
        var profile = OrientedDisplaySize(ReadBackendSettingsForWindow());
        var availablePhysicalWidth = Math.Max(1, (int)Math.Round(availableWidth * dpiScale));
        var availablePhysicalHeight = Math.Max(1, (int)Math.Round(availableHeight * dpiScale));
        var snappedPhysical = CalculateStableNativeViewport(
            availablePhysicalWidth, availablePhysicalHeight, profile.Width, profile.Height);
        var viewportWidth = snappedPhysical.Width / dpiScale;
        var viewportHeight = snappedPhysical.Height / dpiScale;

        DisplayViewport.HorizontalAlignment = HorizontalAlignment.Center;
        DisplayViewport.VerticalAlignment = VerticalAlignment.Center;
        DisplayViewport.Width = Math.Max(1, viewportWidth);
        DisplayViewport.Height = Math.Max(1, viewportHeight);
        BootProgressOverlay.HorizontalAlignment = HorizontalAlignment.Center;
        BootProgressOverlay.VerticalAlignment = VerticalAlignment.Center;
        BootProgressOverlay.Width = Math.Max(1, viewportWidth);
        BootProgressOverlay.Height = Math.Max(1, viewportHeight);

        UpdateBootProgressGeometry((int)Math.Round(BootProgressFill.Width / Math.Max(1, BootProgressTrack.Width) * 100));
    }

    private static bool OfficialEmulatorLaunchProfileChanged(BackendSettings before, BackendSettings after)
    {
        var b = OrientedDisplaySize(before);
        var a = OrientedDisplaySize(after);
        return b.Width != a.Width || b.Height != a.Height ||
               before.DisplayDpi != after.DisplayDpi ||
               before.Fps != after.Fps ||
               before.CpuCores != after.CpuCores ||
               before.RamMb != after.RamMb ||
               before.StorageGb != after.StorageGb ||
               before.LowLatencyInput != after.LowLatencyInput ||
               !string.Equals(before.EmulationPerformanceMode, after.EmulationPerformanceMode, StringComparison.OrdinalIgnoreCase) ||
               before.VsyncEnabled != after.VsyncEnabled ||
               before.FreesyncEnabled != after.FreesyncEnabled ||
               before.LeanGuestEnabled != after.LeanGuestEnabled ||
               before.PreferDiscreteGpu != after.PreferDiscreteGpu ||
               before.SpeakerOutput != after.SpeakerOutput ||
               before.MicrophoneInput != after.MicrophoneInput ||
               before.FastDisk != after.FastDisk ||
               before.NatNetworking != after.NatNetworking ||
               before.SecureIsolationEnabled != after.SecureIsolationEnabled ||
               before.NetworkIsolationEnabled != after.NetworkIsolationEnabled ||
               before.NetworkBlockHostServices != after.NetworkBlockHostServices ||
               before.NetworkBlockPrivateNetworks != after.NetworkBlockPrivateNetworks ||
               before.NetworkBlockLinkLocal != after.NetworkBlockLinkLocal ||
               before.NetworkBlockMulticast != after.NetworkBlockMulticast ||
               before.NetworkUseSafeDns != after.NetworkUseSafeDns ||
               before.NetworkAllowHostProxy != after.NetworkAllowHostProxy ||
               before.NetworkHostProxyPort != after.NetworkHostProxyPort ||
               !string.Equals(before.NetworkIsolationMode, after.NetworkIsolationMode, StringComparison.OrdinalIgnoreCase) ||
               !string.Equals(before.NetworkDnsServers, after.NetworkDnsServers, StringComparison.OrdinalIgnoreCase) ||
               before.ReadOnlyBaseImage != after.ReadOnlyBaseImage ||
               before.OfficialEmulatorDisposableMode != after.OfficialEmulatorDisposableMode ||
               !string.Equals(before.ResolutionMode, after.ResolutionMode, StringComparison.OrdinalIgnoreCase) ||
               !string.Equals(before.RendererApi, after.RendererApi, StringComparison.OrdinalIgnoreCase) ||
               !string.Equals(before.GpuMode, after.GpuMode, StringComparison.OrdinalIgnoreCase) ||
               !string.Equals(before.SystemUiRenderer, after.SystemUiRenderer, StringComparison.OrdinalIgnoreCase) ||
               !string.Equals(before.Acceleration, after.Acceleration, StringComparison.OrdinalIgnoreCase) ||
               before.PersonaEnabled != after.PersonaEnabled ||
               before.PersonaFailClosed != after.PersonaFailClosed ||
               !string.Equals(before.PersonaMode, after.PersonaMode, StringComparison.OrdinalIgnoreCase) ||
               !string.Equals(before.PersonaSeed, after.PersonaSeed, StringComparison.Ordinal) ||
               !string.Equals(before.DeviceModel, after.DeviceModel, StringComparison.Ordinal) ||
               !string.Equals(before.DeviceMaker, after.DeviceMaker, StringComparison.Ordinal) ||
               !string.Equals(before.PersonaLocale, after.PersonaLocale, StringComparison.OrdinalIgnoreCase) ||
               !string.Equals(before.PersonaTimezone, after.PersonaTimezone, StringComparison.OrdinalIgnoreCase) ||
               !string.Equals(before.PersonaCountryCode, after.PersonaCountryCode, StringComparison.OrdinalIgnoreCase) ||
               !string.Equals(before.BootProfile, after.BootProfile, StringComparison.OrdinalIgnoreCase);
    }

    private void RepositionEmbeddedWindow()
    {
        if (_activeVm == null) return;
        try
        {
            var host = DisplayHost;
            // PointToScreen returns physical pixels (DPI-aware)
            var point = host.PointToScreen(new Point(0, 0));
            // ActualWidth/Height are DIPs — scale to physical pixels
            var dpi = GetDpiForWindow(_windowHandle);
            var scale = dpi / 96.0;

            int hostW = Math.Max(1, (int)Math.Round(host.ActualWidth * scale));
            int hostH = Math.Max(1, (int)Math.Round(host.ActualHeight * scale));
            var configuredDisplay = OrientedDisplaySize(ReadBackendSettingsForWindow());
            var fit = UsesOfficialEmulatorNativeWindow()
                ? CalculateStableNativeViewport(hostW, hostH, configuredDisplay.Width, configuredDisplay.Height)
                : CalculateAspectFit(hostW, hostH, AndroidScanoutWidth, AndroidScanoutHeight);
            _androidDisplayRectPixels = fit;

            // Embedded renderer windows are parented to DisplayHost.DisplayHandle,
            // so coordinates are relative to that child HWND, not screen space.
            int x = fit.X;
            int y = fit.Y;
            int w = fit.Width;
            int h = fit.Height;

            if (w > 0 && h > 0 && DisplayHost.DisplayHandle != IntPtr.Zero)
                _engine.ResizeEmbeddedWindow(_activeVm.Id, x, y, w, h);
        }
        catch { }
    }



    private void QueueEmbeddedWindowReposition()
    {
        if (_embeddedWindowRepositionQueued || _shutdownStarted) return;
        _embeddedWindowRepositionQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _embeddedWindowRepositionQueued = false;
            if (_shutdownStarted || _activeVm == null) return;
            UpdateLayout();
            DisplayHost.UpdateLayout();
            RepositionEmbeddedWindow();
        }, System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    private void EnsureEmbeddedContainmentTimer()
    {
        _embeddedContainmentTimer ??= new System.Windows.Threading.DispatcherTimer(
            TimeSpan.FromMilliseconds(100),
            System.Windows.Threading.DispatcherPriority.Render,
            (_, _) =>
            {
                if (_shutdownStarted || _activeVm == null)
                {
                    _embeddedContainmentTimer?.Stop();
                    return;
                }
                if (!_launcherPresentationActive) RepositionEmbeddedWindow();
            },
            Dispatcher);
        _embeddedContainmentTimer.Start();
    }

    private bool UsesOfficialEmulatorNativeWindow() => _engine is GoogleEmulatorRuntimeService;

    private static Int32Rect CalculateStableNativeViewport(int hostW, int hostH, int contentW, int contentH)
    {
        hostW = Math.Max(1, hostW);
        hostH = Math.Max(1, hostH);
        contentW = Math.Max(1, contentW);
        contentH = Math.Max(1, contentH);

        static int GreatestCommonDivisor(int a, int b)
        {
            while (b != 0) (a, b) = (b, a % b);
            return Math.Max(1, Math.Abs(a));
        }

        // Qt/gfxstream is most reliable when its child surface lands on an exact
        // reduced guest ratio. Fractional 9:16 sizes such as 748x1329 can leave
        // the scanout gray after a resize/orientation transition; 747x1328 is the
        // nearest exact surface and leaves only the unavoidable centered pixel.
        var divisor = GreatestCommonDivisor(contentW, contentH);
        var unitW = contentW / divisor;
        var unitH = contentH / divisor;
        var multiplier = Math.Min(hostW / unitW, hostH / unitH);
        if (multiplier < 1)
            return CalculateAspectFit(hostW, hostH, contentW, contentH);
        var width = unitW * multiplier;
        var height = unitH * multiplier;
        return new Int32Rect((hostW - width) / 2, (hostH - height) / 2, width, height);
    }

    private static Int32Rect CalculateAspectFit(int hostW, int hostH, int contentW, int contentH)
    {
        hostW = Math.Max(1, hostW);
        hostH = Math.Max(1, hostH);
        var scale = Math.Min(hostW / (double)contentW, hostH / (double)contentH);
        var w = Math.Max(1, (int)Math.Round(contentW * scale));
        var h = Math.Max(1, (int)Math.Round(contentH * scale));
        return new Int32Rect((hostW - w) / 2, (hostH - h) / 2, w, h);
    }

    private void OnEngineWindowCreated(string vmId, IntPtr hWnd)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _activeNativeWindow = hWnd;
            var deferDisplayUntilReady = _coldBootLauncher is { IsVisible: true };
            _engine.SetEmbeddedWindowVisible(vmId, !deferDisplayUntilReady);
            if (!deferDisplayUntilReady)
            {
                UpdateLayout();
                DisplayHost.UpdateLayout();
                RepositionEmbeddedWindow();
                EnsureEmbeddedContainmentTimer();
                _ = NudgeNativeRendererPaintAsync(vmId);
                try { SetFocus(hWnd); } catch { }
            }
            NoVmOverlay.Visibility = Visibility.Collapsed;
            StatusText.Text = deferDisplayUntilReady
                ? "Native display attached and hidden until Android is ready"
                : "Native display attached; waiting for first renderer frame";
            AppendRendererDebug($"{DateTime.Now:HH:mm:ss.fff} display attached for {vmId}; hiddenUntilReady={deferDisplayUntilReady}");
        });
    }

    private async Task NudgeNativeRendererPaintAsync(string vmId)
    {
        // The embedded emulator HWND can attach before gfxstream has presented a
        // real frame, leaving the child area grey until any later WPF layout
        // change (toolbar expand/retract, maximize, etc.) sends another resize.
        // Re-send a few host-only SetWindowPos pulses after attach/boot; this
        // does not change Android wm size or density.
        if (!UsesOfficialEmulatorNativeWindow()) return;
        for (var i = 0; i < 8; i++)
        {
            await Task.Delay(i == 0 ? 60 : 180);
            if (_activeVm == null || _activeVm.Id != vmId) return;
            await Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    UpdateLayout();
                    DisplayHost.UpdateLayout();
                    // Re-pin the exact quantized rectangle. Do not replace it with
                    // full-host dimensions here; that reintroduced fractional
                    // scaling and discarded the centered X/Y offset.
                    RepositionEmbeddedWindow();
                    DisplayHost.InvalidateVisual();
                    DisplayViewport.InvalidateVisual();
                }
                catch { }
            }, System.Windows.Threading.DispatcherPriority.Render);
        }
    }

    private void ShowAndroidBootToast()
    {
        if (AndroidBootToastPopup == null || DisplayViewport == null) return;
        if (WindowState == WindowState.Minimized || !IsVisible)
        {
            HideAndroidBootToast(immediate: true);
            return;
        }
        var wasOpen = AndroidBootToastPopup.IsOpen;
        var hostWidth = Math.Max(0.0, DisplayViewport.ActualWidth);
        var hostHeight = Math.Max(0.0, DisplayViewport.ActualHeight);
        var toastWidth = Math.Min(AndroidBootToastWidthDip, Math.Max(240.0, hostWidth - 24.0));
        _androidBootToast.Width = toastWidth;
        AndroidBootToastPopup.IsOpen = true;
        AndroidBootToastPopup.UpdateLayout();
        var toastHeight = Math.Max(64.0, AndroidBootToastView.ActualHeight > 0 ? AndroidBootToastView.ActualHeight : 64.0);
        AndroidBootToastPopup.HorizontalOffset = Math.Max(0, (hostWidth - toastWidth) / 2.0);
        AndroidBootToastPopup.VerticalOffset = Math.Max(0, hostHeight - toastHeight - 24.0);
        AndroidBootToastPopup.UpdateLayout();
        if (!wasOpen)
            _ = AndroidBootToastView.ShowAnimatedFromBottomAsync();
        if (!wasOpen)
            ScheduleAndroidBootToastHide(AndroidBootToastVisibleMs);
    }

    private void ScheduleAndroidBootToastHide(int delayMs)
    {
        _androidBootToastHideCts?.Cancel();
        var cts = new CancellationTokenSource();
        _androidBootToastHideCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delayMs, cts.Token);
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!cts.IsCancellationRequested)
                        HideAndroidBootToast();
                });
            }
            catch (OperationCanceledException) { }
        });
    }

    private void HideAndroidBootToast(bool immediate = false)
    {
        _androidBootToastHideCts?.Cancel();
        _androidBootToastHideCts = null;
        if (AndroidBootToastPopup == null || AndroidBootToastPopup.IsOpen != true)
            return;
        if (immediate)
        {
            AndroidBootToastPopup.IsOpen = false;
            return;
        }
        _ = HideAndroidBootToastAnimatedAsync();
    }

    private async Task HideAndroidBootToastAnimatedAsync()
    {
        try { await AndroidBootToastView.HideAnimatedToBottomAsync(); } catch { }
        if (AndroidBootToastPopup != null)
            AndroidBootToastPopup.IsOpen = false;
    }

    private void CloseBootToast_Click(object sender, RoutedEventArgs e) => HideAndroidBootToast();

    private void ShowApkInstallToast(string apkName, string subtext, int progressPercent, bool isError = false)
    {
        if (ApkInstallToastPopup == null || DisplayViewport == null) return;
        _apkInstallApkName = apkName;
        _apkInstallSubtextValue = subtext;
        _apkInstallProgressPercent = Math.Clamp(progressPercent, 0, 100);
        if (WindowState == WindowState.Minimized || !IsVisible)
        {
            HideApkInstallToast(immediate: true);
            return;
        }
        var wasOpen = ApkInstallToastPopup.IsOpen;
        var hostWidth = Math.Max(0.0, DisplayViewport.ActualWidth);
        var hostHeight = Math.Max(0.0, DisplayViewport.ActualHeight);
        var toastWidth = Math.Min(ApkInstallToastWidthDip, Math.Max(220.0, hostWidth - 24.0));
        _apkInstallToast.Width = toastWidth;
        _apkInstallToast.Title = string.IsNullOrWhiteSpace(apkName) ? "Installing APK" : $"Installing {apkName}";
        _apkInstallToast.Message = subtext;
        _apkInstallToast.ProgressPercent = _apkInstallProgressPercent;
        _apkInstallToast.IsError = isError;
        _apkInstallToast.IconData = isError ? ToastErrorIcon : ApkInstallIcon;
        ApkInstallToastPopup.IsOpen = true;
        ApkInstallToastPopup.UpdateLayout();
        var toastHeight = Math.Max(58.0, ApkInstallToastView.ActualHeight > 0 ? ApkInstallToastView.ActualHeight : 58.0);
        ApkInstallToastPopup.HorizontalOffset = Math.Max(0, (hostWidth - toastWidth) / 2.0);
        ApkInstallToastPopup.VerticalOffset = Math.Max(0, hostHeight - toastHeight - 24.0);
        ApkInstallToastPopup.UpdateLayout();
        if (!wasOpen)
            _ = ApkInstallToastView.ShowAnimatedFromBottomAsync();
    }

    private void HideApkInstallToast(bool immediate = false)
    {
        if (ApkInstallToastPopup == null || ApkInstallToastPopup.IsOpen != true)
            return;
        if (immediate)
        {
            ApkInstallToastPopup.IsOpen = false;
            return;
        }
        _ = HideApkInstallToastAnimatedAsync();
    }

    private async Task HideApkInstallToastAnimatedAsync()
    {
        try { await ApkInstallToastView.HideAnimatedToBottomAsync(); } catch { }
        if (ApkInstallToastPopup != null)
            ApkInstallToastPopup.IsOpen = false;
    }

    private void CloseApkInstallToast_Click(object sender, RoutedEventArgs e) => HideApkInstallToast();

    private void ShowBootProgress(string diagnosticLabel = "Loading")
    {
        StatusBar.Visibility = Visibility.Collapsed;
        BootProgressOverlay.Visibility = Visibility.Visible;
        ShowAndroidBootToast();
        _bootDisplayedProgress = 0;
        _bootTargetProgress = 0;
        _bootIdleTicks = 0;
        _bootTargetLabel = SetupWizard.UserFacingBootStage(0);
        ApplyBootProgressVisual(0, _bootTargetLabel);
        EnsureBootProgressTimer();
    }

    private void SetBootProgress(int percent, string diagnosticLabel)
    {
        percent = Math.Clamp(percent, 0, 100);
        if (percent >= _bootTargetProgress)
            _bootTargetLabel = SetupWizard.UserFacingBootStage(percent);
        _bootTargetProgress = Math.Max(_bootTargetProgress, percent);
        if (percent >= 100)
        {
            _bootDisplayedProgress = 100;
            ApplyBootProgressVisual(100, SetupWizard.UserFacingBootStage(100));
            return;
        }
        EnsureBootProgressTimer();
    }

    private void EnsureBootProgressTimer()
    {
        _bootProgressTimer ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(90)
        };
        _bootProgressTimer.Tick -= BootProgressTimer_Tick;
        _bootProgressTimer.Tick += BootProgressTimer_Tick;
        if (!_bootProgressTimer.IsEnabled) _bootProgressTimer.Start();
    }

    private void BootProgressTimer_Tick(object? sender, EventArgs e)
    {
        if (!_startInProgress && _bootTargetProgress < 100)
        {
            _bootProgressTimer?.Stop();
            return;
        }

        if (_bootDisplayedProgress < _bootTargetProgress)
        {
            var distance = _bootTargetProgress - _bootDisplayedProgress;
            _bootDisplayedProgress += Math.Max(1, (int)Math.Ceiling(distance / 7.0));
            _bootDisplayedProgress = Math.Min(_bootDisplayedProgress, _bootTargetProgress);
            _bootIdleTicks = 0;
        }
        else if (_startInProgress && _bootDisplayedProgress < 94 && ++_bootIdleTicks >= 9)
        {
            _bootDisplayedProgress++;
            _bootTargetProgress = Math.Max(_bootTargetProgress, _bootDisplayedProgress);
            _bootIdleTicks = 0;
        }

        ApplyBootProgressVisual(_bootDisplayedProgress, _bootTargetLabel);
    }

    private void ApplyBootProgressVisual(int percent, string label)
    {
        BootProgressPercent.Text = $"{percent}%";
        BootProgressLabel.Text = label;
        UpdateBootProgressGeometry(percent);
        _coldBootLauncher?.ReportBootProgress(percent, label);
    }

    private void UpdateBootProgressGeometry(int percent)
    {
        try
        {
            var width = Math.Max(1, BootProgressOverlay.ActualWidth);
            BootProgressTrack.Width = width;
            var fillWidth = Math.Clamp(width * percent / 100.0, 0, width);
            BootProgressFill.Width = fillWidth;
            var pillLeft = Math.Clamp(fillWidth - (BootProgressPercentPill.Width / 2.0), 0, Math.Max(0, width - BootProgressPercentPill.Width));
            Canvas.SetLeft(BootProgressPercentPill, pillLeft);
        }
        catch { }
    }

    private void HandleEngineStatus(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        _coldBootLauncher?.ReportBootDebug(message);

        if (message.StartsWith("boot-debug:|", StringComparison.OrdinalIgnoreCase))
        {
            AppendRendererDebug(message["boot-debug:|".Length..]);
            return;
        }

        if (message.StartsWith("renderer-debug:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = message.Split('|', 3);
            AppendRendererDebug(parts.Length >= 3 ? $"{parts[1]} {parts[2]}" : message);
            return;
        }

        if (message.StartsWith("runtime-event:", StringComparison.OrdinalIgnoreCase))
        {
            HandleRuntimeEvent(message);
            return;
        }

        AppendRendererDebug(message);

        if (message.StartsWith("progress:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = message.Split('|', 3);
            if (parts.Length >= 3 && int.TryParse(parts[1], out var pct))
            {
                var progressMessage = parts[2];
                if (ShouldSuppressProgressOverlay(progressMessage))
                {
                    if (progressMessage.Contains("Android UI is not ready", StringComparison.OrdinalIgnoreCase) ||
                        progressMessage.Contains("ADB not online", StringComparison.OrdinalIgnoreCase) ||
                        progressMessage.Contains("Waiting for ADB", StringComparison.OrdinalIgnoreCase))
                    {
                        StatusText.Text = "Renderer live; Android boot/ADB still pending";
                    }
                    return;
                }

                if (BootProgressOverlay.Visibility != Visibility.Visible)
                {
                    StatusBar.Visibility = Visibility.Collapsed;
                    BootProgressOverlay.Visibility = Visibility.Visible;
                }
                SetBootProgress(pct, progressMessage);
                StatusText.Text = progressMessage;
                if (progressMessage.Contains("First frame captured", StringComparison.OrdinalIgnoreCase))
                    LoadLatestFramePreview();
                if (pct >= 100 && IsRendererReadyMessage(progressMessage))
                {
                    if (_activeVm != null) _activeVm.Status = "running";
                    _startInProgress = false;
                    NoVmOverlay.Visibility = Visibility.Collapsed;
                    _ = CompleteBootProgressAsync(_activeVm == null ? "Renderer ready" : $"{_activeVm.Name} renderer ready");
                }
                return;
            }
        }

        if (message.Contains("did not attach", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("renderer exited", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("emulator exited", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("did not report boot_completed", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("did not become ready", StringComparison.OrdinalIgnoreCase))
        {
            _startInProgress = false;
            if (_activeVm != null) _activeVm.Status = "error";
            BootProgressOverlay.Visibility = Visibility.Collapsed;
            HideAndroidBootToast();
            StatusBar.Visibility = Visibility.Visible;
            AbortColdBootLauncher(message);
        }

        StatusText.Text = message;
    }

    private void HandleRuntimeEvent(string message)
    {
        var parts = message.Split('|', 4);
        if (parts.Length < 4 || !int.TryParse(parts[2], out var pct))
        {
            AppendRendererDebug(message);
            return;
        }

        var eventType = parts[1];
        var eventMessage = parts[3];
        UpdateAndroidReadinessStrip(eventType, eventMessage);
        AppendRendererDebug($"{DateTime.Now:HH:mm:ss.fff} {eventType}: {eventMessage}");

        if (!_startInProgress && IsPostRendererNoise(eventType, eventMessage))
        {
            StatusText.Text = "Renderer live; Android boot/ADB still pending";
            return;
        }

        if (BootProgressOverlay.Visibility != Visibility.Visible && !eventType.EndsWith(".ready", StringComparison.OrdinalIgnoreCase))
        {
            StatusBar.Visibility = Visibility.Collapsed;
            BootProgressOverlay.Visibility = Visibility.Visible;
        }

        AppendRendererReadinessSnapshot();

        switch (eventType)
        {
            case "producer.created":
                SetBootProgress(pct, "Producer");
                StatusText.Text = "Native frame producer attached";
                break;
            case "producer.frame_written":
                SetBootProgress(Math.Min(pct, 94), "Frame");
                StatusText.Text = eventMessage;
                break;
            case "display.primary":
                SetReadinessText(GpuSourceText, "fastpipe primary", "#4EC9B0");
                SetBootProgress(pct, "Display");
                StatusText.Text = "Display primary: fastpipe scanout-import";
                break;
            case "fastpipe.frame_target.ready":
                SetReadinessText(GpuSourceText, "target ready", "#4EC9B0");
                StatusText.Text = "Fastpipe scanout target attached";
                break;
            case "fastpipe.frame_written":
                _rendererDisplayReady = true;
                SetReadinessText(GpuSourceText, "frames", "#4EC9B0");
                // This is a steady-state frame tick, not boot progress. Do not
                // keep resetting the progress overlay to 82% on every rendered frame.
                StatusText.Text = "Fastpipe GPU frame presented";
                break;
            case "gpu_producer.configured":
                SetBootProgress(pct, "GPU producer");
                StatusText.Text = "GPU producer contract accepted";
                break;
            case "gfxstream.bridge.configured":
                SetBootProgress(pct, "Gfxstream");
                StatusText.Text = "Virtio-gpu/gfxstream bridge configured";
                break;
            case "transport.attached":
                SetBootProgress(pct, "Transport");
                StatusText.Text = "Native frame transport attached";
                break;
            case "renderer.ready":
                _rendererDisplayReady = true;
                SetBootProgress(pct, "Renderer");
                StatusText.Text = "Native renderer accepted frame transport";
                break;
            case "first_frame.ready":
            case "renderer.probe.ready":
                _rendererDisplayReady = true;
                if (_coldBootLauncher is not null && UsesOfficialEmulatorNativeWindow())
                {
                    SetBootProgress(Math.Min(pct, 92), "Starting Android");
                    StatusText.Text = "Renderer ready; waiting for Android boot completion";
                    break;
                }
                if (_activeVm != null) _activeVm.Status = "running";
                _startInProgress = false;
                NoVmOverlay.Visibility = Visibility.Collapsed;
                _ = CompleteBootProgressAsync(_activeVm == null ? "Renderer ready" : $"{_activeVm.Name} renderer ready");
                break;
            case "android.ui.pending":
            case "adb.wait":
            case "adb.timeout":
            case "android.partial":
                if (_rendererDisplayReady)
                {
                    if (_coldBootLauncher is not null)
                    {
                        SetBootProgress(Math.Min(Math.Max(pct, 86), 94), "Starting Android");
                        StatusText.Text = "Renderer ready; Android framework is still starting";
                        break;
                    }
                    _startInProgress = false;
                    if (!_androidPendingLatched)
                    {
                        _androidPendingLatched = true;
                        AppendRendererDebug($"{DateTime.Now:HH:mm:ss.fff} final: renderer is live; Android image remains at boot/ADB pending state");
                    }
                    BootProgressOverlay.Visibility = Visibility.Collapsed;
                    StatusBar.Visibility = Visibility.Visible;
                    StatusText.Text = "Renderer live; Android boot/ADB still pending";
                }
                break;
            case "producer.invalid":
            case "gpu_producer.invalid":
            case "transport.invalid":
            case "renderer.probe.invalid":
                _startInProgress = false;
                if (_activeVm != null) _activeVm.Status = "error";
                BootProgressOverlay.Visibility = Visibility.Collapsed;
                StatusBar.Visibility = Visibility.Visible;
                StatusText.Text = eventMessage;
                AbortColdBootLauncher(eventMessage);
                break;
        }
    }

    private void UpdateAndroidReadinessStrip(string eventType, string eventMessage)
    {
        if (eventType.Equals("runtime.start", StringComparison.OrdinalIgnoreCase))
        {
            SetReadinessText(AdbReadinessText, "starting", "#DCDCAA");
            SetReadinessText(FastpipeReadinessText, "waiting", "#DCDCAA");
            SetReadinessText(GpuSourceText, "pending", "#DCDCAA");
            SetReadinessText(AndroidReadinessText, "booting", "#DCDCAA");
            ReadinessHintText.Text = "waiting for fastpipe GPU source + Android framework";
            return;
        }

        if (eventType.Equals("fastpipe.control.connected", StringComparison.OrdinalIgnoreCase) ||
            eventType.Equals("fastpipe.data.connected", StringComparison.OrdinalIgnoreCase) ||
            eventType.Equals("fastpipe.data.packet", StringComparison.OrdinalIgnoreCase) ||
            eventMessage.Contains("controlMessages=1", StringComparison.OrdinalIgnoreCase))
        {
            SetReadinessText(FastpipeReadinessText, "open", "#4EC9B0");
            ReadinessHintText.Text = "guest opened fastpipe data/control; waiting for GPU frames + Android readiness";
        }


        if (eventType.Equals("fastpipe.frame_target.ready", StringComparison.OrdinalIgnoreCase))
        {
            SetReadinessText(GpuSourceText, "target ready", "#4EC9B0");
            ReadinessHintText.Text = "fastpipe scanout-import target attached";
        }
        else if (eventType.Equals("fastpipe.frame_written", StringComparison.OrdinalIgnoreCase))
        {
            SetReadinessText(GpuSourceText, "frames", "#4EC9B0");
            SetReadinessText(FastpipeReadinessText, "data", "#4EC9B0");
            ReadinessHintText.Text = "display source: revm.fastpipe.data scanout-import";
        }
        else if (eventType.Equals("display.primary", StringComparison.OrdinalIgnoreCase))
        {
            SetReadinessText(GpuSourceText, "primary", "#4EC9B0");
            ReadinessHintText.Text = eventMessage;
        }

        if (eventType.Equals("adb.wait", StringComparison.OrdinalIgnoreCase))
        {
            SetReadinessText(AdbReadinessText, "offline", "#F48771");
            SetReadinessText(AndroidReadinessText, "ADB pending", "#DCDCAA");
            ReadinessHintText.Text = "ADB TCP connected but not device; route/auth/framework under test";
        }
        else if (eventType.Equals("adb.timeout", StringComparison.OrdinalIgnoreCase))
        {
            SetReadinessText(AdbReadinessText, "timeout", "#F48771");
            SetReadinessText(AndroidReadinessText, "not ready", "#F48771");
            ReadinessHintText.Text = eventMessage;
        }
        else if (eventType.Equals("android.ready", StringComparison.OrdinalIgnoreCase))
        {
            SetReadinessText(AdbReadinessText, "device", "#4EC9B0");
            SetReadinessText(AndroidReadinessText, "boot_completed", "#4EC9B0");
            ReadinessHintText.Text = eventMessage;
        }
        else if (eventType.Equals("android.partial", StringComparison.OrdinalIgnoreCase) ||
                 eventType.Equals("android.ui.pending", StringComparison.OrdinalIgnoreCase))
        {
            SetReadinessText(AndroidReadinessText, "pending", "#DCDCAA");
            ReadinessHintText.Text = eventMessage;
        }
    }

    private static void SetReadinessText(TextBlock textBlock, string text, string color)
    {
        textBlock.Text = text;
        textBlock.Foreground = (Brush)new BrushConverter().ConvertFromString(color)!;
    }

    private static bool IsRendererReadyMessage(string message) =>
        message.Contains("Renderer endpoint validation passed", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("first native frame", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("frame is available to the native renderer", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("native gfxstream emulator window attached", StringComparison.OrdinalIgnoreCase);

    private bool ShouldSuppressProgressOverlay(string message)
    {
        if (!_rendererDisplayReady && _startInProgress)
            return false;
        return IsPostRendererNoise(string.Empty, message);
    }

    private static bool IsPostRendererNoise(string eventType, string message) =>
        eventType.Equals("producer.frame_written", StringComparison.OrdinalIgnoreCase) ||
        eventType.Equals("android.ui.pending", StringComparison.OrdinalIgnoreCase) ||
        eventType.Equals("scanout.pending", StringComparison.OrdinalIgnoreCase) ||
        eventType.Equals("scanout.frame_observed", StringComparison.OrdinalIgnoreCase) ||
        eventType.Equals("adb.wait", StringComparison.OrdinalIgnoreCase) ||
        eventType.Equals("adb.timeout", StringComparison.OrdinalIgnoreCase) ||
        eventType.Equals("android.partial", StringComparison.OrdinalIgnoreCase) ||
        eventType.Equals("runtime.heartbeat", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("QMP guest scanout frame", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("QMP screendump not usable yet", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("Live scanout has non-trivial visual content", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("Android UI is not ready", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("ADB not online", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("Waiting for ADB", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("Android boot attempt running", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("Android VM process alive", StringComparison.OrdinalIgnoreCase);

    private void BeginColdBootLauncher()
    {
        var settings = ReadBackendSettingsForWindow();
        if (!settings.ColdBootLauncherEnabled) return;

        if (_coldBootLauncher is { IsVisible: true } existing)
        {
            existing.TransitionToBootMode();
            WireColdBootLauncher(existing);
            _launcherPresentationActive = true;
            SuppressMainWindowForLauncher();
            return;
        }

        var launcher = new SetupWizard(bootMode: true);
        _coldBootLauncher = launcher;
        WireColdBootLauncher(launcher);
        _launcherPresentationActive = true;
        launcher.Show();
        SuppressMainWindowForLauncher();
    }

    private void WireColdBootLauncher(SetupWizard launcher)
    {
        if (ReferenceEquals(_wiredColdBootLauncher, launcher)) return;
        _wiredColdBootLauncher = launcher;
        launcher.BootCancelRequested += () => Dispatcher.BeginInvoke(async () =>
        {
            var vm = _activeVm;
            if (vm != null) await StopVmAsync(vm);
            _startInProgress = false;
            _bootProgressTimer?.Stop();
            ShowMainWindowAfterLauncher();
        });
        launcher.Closed += (_, _) =>
        {
            if (ReferenceEquals(_coldBootLauncher, launcher)) _coldBootLauncher = null;
            if (ReferenceEquals(_wiredColdBootLauncher, launcher)) _wiredColdBootLauncher = null;
            if (!_startInProgress) ShowMainWindowAfterLauncher();
        };
    }

    private void AbortColdBootLauncher(string message)
    {
        _bootProgressTimer?.Stop();
        if (_coldBootLauncher is not null)
        {
            _coldBootLauncher.FailBoot(message);
            return;
        }
        ShowMainWindowAfterLauncher();
    }

    private async Task CompleteBootProgressAsync(string finalStatus)
    {
        SetBootProgress(100, "Android ready");
        _bootProgressTimer?.Stop();
        await Task.Delay(180);
        BootProgressOverlay.Visibility = Visibility.Collapsed;
        StatusBar.Visibility = Visibility.Visible;
        var launcher = _coldBootLauncher;
        if (launcher is not null)
        {
            await launcher.CompleteBootAsync();
            if (ReferenceEquals(_coldBootLauncher, launcher)) _coldBootLauncher = null;
        }
        ShowMainWindowAfterLauncher();
        if (_activeVm != null)
        {
            _engine.SetEmbeddedWindowVisible(_activeVm.Id, true);
            RepositionEmbeddedWindow();
            EnsureEmbeddedContainmentTimer();
            _ = NudgeNativeRendererPaintAsync(_activeVm.Id);
            if (_activeNativeWindow != IntPtr.Zero)
            {
                try { SetFocus(_activeNativeWindow); } catch { }
            }
        }
        StatusText.Text = finalStatus;
    }

    private void LoadLatestFramePreview()
    {
        try
        {
            var ppmPath = Path.Combine(RevmPaths.BaseDir, "logs", "first-frame.ppm");
            var bmp = LoadPpmBitmap(ppmPath);
            if (bmp == null) return;
            FramePreview.Source = bmp;
            FramePreview.Visibility = Visibility.Visible;
            NoVmOverlay.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Frame load failed: " + ex.Message;
        }
    }

    private static BitmapSource? LoadPpmBitmap(string path)
    {
        if (!File.Exists(path)) return null;
        var data = File.ReadAllBytes(path);
        var idx = 0;

        string NextToken()
        {
            while (idx < data.Length)
            {
                if (data[idx] == '#')
                {
                    while (idx < data.Length && data[idx] != '\n') idx++;
                }
                else if (!char.IsWhiteSpace((char)data[idx])) break;
                idx++;
            }
            var start = idx;
            while (idx < data.Length && !char.IsWhiteSpace((char)data[idx])) idx++;
            return System.Text.Encoding.ASCII.GetString(data, start, idx - start);
        }

        if (NextToken() != "P6") return null;
        var width = int.Parse(NextToken());
        var height = int.Parse(NextToken());
        var max = int.Parse(NextToken());
        if (max != 255) return null;
        while (idx < data.Length && char.IsWhiteSpace((char)data[idx])) idx++;
        var stride = width * 3;
        if (idx + stride * height > data.Length) return null;
        var pixels = new byte[stride * height];
        Buffer.BlockCopy(data, idx, pixels, 0, pixels.Length);
        var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Rgb24, null, pixels, stride);
        bmp.Freeze();
        return bmp;
    }

    private void RefreshInstances()
    {
        _instances.Clear();
        foreach (var vm in _engine.GetInstances()) _instances.Add(vm);
    }

    // === Title Bar ===
    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) Maximize_Click(sender, e);
        else if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (_viewportFitMaximized)
            RestoreViewportWindow();
        else
            FitWindowToWorkArea();
    }

    private void FitWindowToWorkArea()
    {
        if (_aspectSizingActive || WindowState == WindowState.Minimized) return;
        var restore = WindowState == WindowState.Maximized
            ? RestoreBounds
            : new Rect(Left, Top, Math.Max(MinWidth, ActualWidth), Math.Max(MinHeight, ActualHeight));
        if (!_viewportFitMaximized && restore.Width > 0 && restore.Height > 0)
            _viewportRestoreBounds = restore;

        var workArea = SystemParameters.WorkArea;
        var chromeWidth = _sidebarCollapsed ? 0 : ToolbarWidthDip;
        var chromeHeight = TitleBarHeightDip + (_debugStripVisible ? DebugStripHeightDip : 0);
        var dpiScale = GetWindowDpiScale();
        var profile = OrientedDisplaySize(ReadBackendSettingsForWindow());
        var availablePhysicalWidth = Math.Max(1,
            (int)Math.Floor((workArea.Width - chromeWidth) * dpiScale));
        var availablePhysicalHeight = Math.Max(1,
            (int)Math.Floor((workArea.Height - chromeHeight) * dpiScale));
        var snappedPhysical = CalculateStableNativeViewport(
            availablePhysicalWidth, availablePhysicalHeight, profile.Width, profile.Height);
        var viewportWidth = snappedPhysical.Width / dpiScale;
        var viewportHeight = snappedPhysical.Height / dpiScale;
        var targetWidth = viewportWidth + chromeWidth;
        var targetHeight = viewportHeight + chromeHeight;

        _aspectSizingActive = true;
        try
        {
            WindowState = WindowState.Normal;
            Width = targetWidth;
            Height = targetHeight;
            // Correct nominal chrome using the live WPF layout. This keeps the
            // DisplayHost exactly on the snapped physical rectangle after custom
            // WindowChrome and monitor DPI borders are accounted for.
            for (var pass = 0; pass < 2; pass++)
            {
                UpdateLayout();
                DisplayHost.UpdateLayout();
                var measuredChromeWidth = Math.Max(0, ActualWidth - DisplayHost.ActualWidth);
                var measuredChromeHeight = Math.Max(0, ActualHeight - DisplayHost.ActualHeight);
                Width = viewportWidth + measuredChromeWidth;
                Height = viewportHeight + measuredChromeHeight;
            }
            targetWidth = Width;
            targetHeight = Height;
            Left = workArea.Left + (workArea.Width - targetWidth) / 2.0;
            Top = workArea.Top + (workArea.Height - targetHeight) / 2.0;
            _viewportFitMaximized = true;
        }
        finally
        {
            _aspectSizingActive = false;
        }
        UpdateMaximizeIcon();
        ApplyViewportSizingForWindowState();
        RepositionEmbeddedWindow();
    }

    private void RestoreViewportWindow()
    {
        if (_viewportRestoreBounds.Width <= 0 || _viewportRestoreBounds.Height <= 0)
            return;
        _aspectSizingActive = true;
        try
        {
            WindowState = WindowState.Normal;
            Left = _viewportRestoreBounds.Left;
            Top = _viewportRestoreBounds.Top;
            Width = _viewportRestoreBounds.Width;
            Height = _viewportRestoreBounds.Height;
            _viewportFitMaximized = false;
        }
        finally
        {
            _aspectSizingActive = false;
        }
        UpdateMaximizeIcon();
        ApplyViewportSizingForWindowState();
        RepositionEmbeddedWindow();
    }

    private void TitleMenu_Click(object sender, RoutedEventArgs e)
    {
        OverflowPopup.IsOpen = !OverflowPopup.IsOpen;
    }

    private void CollapseSidebar_Click(object sender, RoutedEventArgs e)
    {
        _sidebarCollapsed = !_sidebarCollapsed;
        ToolbarColumn.Width = new GridLength(_sidebarCollapsed ? 0 : 30);
        RightToolbar.Visibility = _sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
        CollapseIcon.Data = Geometry.Parse(_sidebarCollapsed
            ? "M5,3 L10,8 L5,13 M9,3 L14,8 L9,13"
            : "M11,3 L6,8 L11,13 M7,3 L2,8 L7,13");
        ApplyWindowSizeFromBackendSettings();
        RepositionEmbeddedWindow();
    }

    private void MiniMode_Click(object sender, RoutedEventArgs e)
    {
        _miniMode = !_miniMode;
        if (_miniMode)
        {
            _normalWidth = ActualWidth;
            _normalHeight = ActualHeight;
            Topmost = true;
            Width = 390;
            Height = 720;
            WindowState = WindowState.Normal;
            StatusText.Text = "Mini mode";
        }
        else
        {
            Topmost = false;
            if (_normalWidth > 0) Width = _normalWidth;
            if (_normalHeight > 0) Height = _normalHeight;
            StatusText.Text = "Mini mode off";
        }
        RepositionEmbeddedWindow();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmClose()) return;
        ShutdownNow();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_shutdownStarted) return;
        if (!_closeConfirmed && !ConfirmClose())
        {
            e.Cancel = true;
            return;
        }
        ShutdownNow();
    }

    private bool ConfirmClose()
    {
        if (_closeConfirmed) return true;
        try
        {
            var dialog = new ExitConfirmDialog { Owner = this };
            _closeConfirmed = dialog.ShowDialog() == true;
        }
        catch
        {
            _closeConfirmed = false;
        }
        return _closeConfirmed;
    }

    private void ShutdownNow()
    {
        if (_shutdownStarted) return;
        _shutdownStarted = true;
        try { _mainWindowSource?.RemoveHook(MainWindowWindowProc); } catch { }
        _mainWindowSource = null;
        try { _resizeDebounce?.Stop(); _resizeDebounce?.Dispose(); } catch { }
        try { _agentInbox?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        try { _agentCoordinator?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        _agentInbox = null;
        _agentCoordinator = null;
        _agentCenterDialog = null;
        try { _engine.StopAll(); } catch { }
        ForceKillRuntimeProcesses();
        Application.Current.Shutdown();
    }

    private static void ForceKillRuntimeProcesses()
    {
        var currentPid = Environment.ProcessId;
        var root = RuntimeBackendFactory.GetBaseDir().Replace('/', '\\');
        var processNames = new[]
        {
            "emulator",
            "qemu-system-x86_64",
            "revm-vmm",
            "revm-fastpipe-host",
            "revm-render-host"
        };

        foreach (var name in processNames)
        {
            foreach (var proc in Process.GetProcessesByName(name))
            {
                try
                {
                    if (proc.Id == currentPid) continue;
                    var path = TryGetProcessPath(proc);
                    var belongsToReplayer = string.IsNullOrWhiteSpace(path) ||
                        path.Replace('/', '\\').Contains(root, StringComparison.OrdinalIgnoreCase) ||
                        path.Contains("Android\\Sdk\\emulator", StringComparison.OrdinalIgnoreCase);
                    if (!belongsToReplayer) continue;
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(1200);
                }
                catch { }
                finally { try { proc.Dispose(); } catch { } }
            }
        }
    }

    private static string TryGetProcessPath(Process proc)
    {
        try { return proc.MainModule?.FileName ?? string.Empty; }
        catch { return string.Empty; }
    }

    // === Toolbar ===
    private void ToggleEngine_Click(object sender, RoutedEventArgs e) =>
        StatusText.Text = _engine.CheckEngine() ? "REPlayer runtime is available" : "REPlayer runtime not found";

    private void ToggleInstances_Click(object sender, RoutedEventArgs e) => ShowInstanceManager();

    private async void ShowInstanceManager()
    {
        InstancePopup.IsOpen = false;
        var pausedVm = await PauseActiveVmForModalAsync("Android paused while Instances is open");
        var instanceActionRequested = false;
        try
        {
            var dialog = new InstanceManagerDialog(
                _engine,
                vm => { instanceActionRequested = true; _ = StartVmAsync(vm); },
                vm => { instanceActionRequested = true; _ = StopVmAsync(vm); },
                id => _activeVm?.Id == id && (string.Equals(_activeVm.Status, "running", StringComparison.OrdinalIgnoreCase) || _startInProgress))
            { Owner = this };
            dialog.ShowDialog();
        }
        finally
        {
            if (!instanceActionRequested)
                await ResumePausedVmAfterModalAsync(pausedVm);
            RefreshInstances();
        }
    }

    private void ToggleOrientation_Click(object sender, RoutedEventArgs e)
    {
        if (_orientationChanging) return;
        if (!string.Equals(ReadBackendSettingsForWindow().BootProfile, "api34-resizable", StringComparison.OrdinalIgnoreCase))
        {
            StatusText.Text = "Select Resizable Analysis in Settings to rotate the physical Android display";
            return;
        }
        _isHorizontal = !_isHorizontal;
        UpdateOrientationIcon();

        // Keep REPlayer chrome/sidebar fixed. Only rotate the Android guest.
        if (!_sidebarCollapsed)
        {
            RightToolbar.Visibility = Visibility.Visible;
            ToolbarColumn.Width = new GridLength(30);
        }
        BottomToolbar.Visibility = Visibility.Collapsed;

        if (_activeVm != null)
        {
            StatusText.Text = _isHorizontal ? "Rotating Android to landscape..." : "Rotating Android to portrait...";
            _ = RotateActiveVmAsync(_isHorizontal ? 1 : 0);
        }
    }

    private async Task RotateActiveVmAsync(int rotation)
    {
        if (_activeVm == null || _orientationChanging) return;
        _orientationChanging = true;
        _resizeDebounce?.Stop();
        var vm = _activeVm;
        var targetLandscape = rotation == 1;
        try
        {
            StatusText.Text = targetLandscape ? "Switching to landscape..." : "Switching to portrait...";

            // API 34's native resizable-display controller updates the guest
            // DisplayDevice, gfxstream scanout, and Qt surface without restarting
            // emulator.exe or QEMU. Width/height only select the named profile.
            await _engine.SetAndroidResolutionAsync(vm.Id,
                targetLandscape ? 1920 : 1080,
                targetLandscape ? 1200 : 2400);
            await _engine.SetAndroidRotationAsync(vm.Id, 0);
            SaveOrientationMode(rotation);

            ApplyWindowSizeFromBackendSettings();
            UpdateLayout();
            for (var settle = 0; settle < 6; settle++)
            {
                RepositionEmbeddedWindow();
                await Task.Delay(settle == 0 ? 45 : 32);
            }
            StatusText.Text = targetLandscape ? "Android · landscape" : "Android · portrait";
        }
        catch (Exception ex)
        {
            _isHorizontal = !targetLandscape;
            UpdateOrientationIcon();
            SaveOrientationMode(_isHorizontal ? 1 : 0);
            ApplyWindowSizeFromBackendSettings();
            UpdateLayout();
            for (var settle = 0; settle < 4; settle++)
            {
                RepositionEmbeddedWindow();
                await Task.Delay(settle == 0 ? 45 : 32);
            }
            StatusText.Text = "Display switch failed";
            AppendRendererDebug($"{DateTime.Now:HH:mm:ss.fff} native display switch failed: {ex.Message}");
        }
        finally
        {
            _orientationChanging = false;
        }
    }

    private void SaveOrientationMode(int rotation)
    {
        try
        {
            var settingsPath = Path.Combine(RuntimeBackendFactory.GetBaseDir(), "runtime", "backend-settings.json");
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };
            var settings = File.Exists(settingsPath)
                ? JsonSerializer.Deserialize<BackendSettings>(File.ReadAllText(settingsPath), options) ?? new BackendSettings()
                : new BackendSettings();
            settings.ResolutionMode = rotation == 1 ? "landscape" : "portrait";
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            File.WriteAllText(settingsPath, JsonSerializer.Serialize(settings, options));
        }
        catch { }
    }

    private void AdjustPlayerWindowForOrientation(int rotation)
    {
        ApplyWindowSizeFromBackendSettings();
    }

    private async Task ApplyCurrentViewportResolutionAsync()
    {
        if (_activeVm == null || _windowHandle == IntPtr.Zero) return;
        var dpi = GetDpiForWindow(_windowHandle);
        var scale = dpi / 96.0;
        var w = Math.Max(1, (int)(DisplayHost.ActualWidth * scale));
        var h = Math.Max(1, (int)(DisplayHost.ActualHeight * scale));
        await _engine.SetAndroidResolutionAsync(_activeVm.Id, w, h);
    }

    private void Overflow_Click(object sender, RoutedEventArgs e) =>
        OverflowPopup.IsOpen = !OverflowPopup.IsOpen;

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        OverflowPopup.IsOpen = false;
        var pausedVm = await PauseActiveVmForModalAsync("Android paused while Settings is open");
        try
        {
            var before = ReadBackendSettingsForWindow();
            var dialog = new SettingsDialog
            {
                Owner = this,
                PrepareNetworkPolicyChangeAsync = StopActiveVmForNetworkPolicyChangeAsync
            };
            dialog.AppliedAsync += async () =>
            {
                var after = ReadBackendSettingsForWindow();
                await ApplySettingsLaunchProfileAsync(forceRestart: OfficialEmulatorLaunchProfileChanged(before, after));
                before = after;
            };

            if (dialog.ShowDialog() == true)
            {
                var after = ReadBackendSettingsForWindow();
                await ApplySettingsLaunchProfileAsync(forceRestart: OfficialEmulatorLaunchProfileChanged(before, after));
            }
        }
        finally
        {
            await ResumePausedVmAfterModalAsync(pausedVm);
        }
    }

    private async Task<bool> StopActiveVmForNetworkPolicyChangeAsync()
    {
        if (_activeVm is null && !_startInProgress) return true;
        var vm = _activeVm;
        if (vm is null)
        {
            StatusText.Text = "Wait for the current Android start operation to finish before changing network policy.";
            return false;
        }

        StatusText.Text = "Stopping Android before changing the host network boundary...";
        await StopVmAsync(vm);
        var stillTracked = _activeVm?.Id == vm.Id || _startInProgress;
        var stillRunning = _engine.GetInstances().Any(instance =>
            instance.Id == vm.Id && string.Equals(instance.Status, "running", StringComparison.OrdinalIgnoreCase));
        if (stillTracked || stillRunning)
        {
            StatusText.Text = "Android could not be proven stopped; host policy was not changed.";
            return false;
        }
        return true;
    }

    private async Task<VmInstance?> PauseActiveVmForModalAsync(string message)
    {
        if (_activeVm == null || _startInProgress) return null;
        var vm = _activeVm;
        try
        {
            await _engine.PauseInstanceAsync(vm.Id);
            StatusText.Text = message;
            return vm;
        }
        catch (Exception ex)
        {
            AppendRendererDebug($"{DateTime.Now:HH:mm:ss.fff} pause skipped: {ex.Message}");
            return null;
        }
    }

    private async Task ResumePausedVmAfterModalAsync(VmInstance? pausedVm)
    {
        if (pausedVm == null) return;
        if (_activeVm?.Id != pausedVm.Id || _startInProgress) return;
        try
        {
            await _engine.ResumeInstanceAsync(pausedVm.Id);
            StatusText.Text = $"{pausedVm.Name} resumed";
        }
        catch (Exception ex)
        {
            AppendRendererDebug($"{DateTime.Now:HH:mm:ss.fff} resume skipped: {ex.Message}");
        }
    }

    private async Task ApplySettingsLaunchProfileAsync(bool forceRestart)
    {
        ApplyWindowSizeFromBackendSettings();
        await Dispatcher.InvokeAsync(() =>
        {
            UpdateLayout();
            DisplayHost.UpdateLayout();
        }, System.Windows.Threading.DispatcherPriority.Render);
        RepositionEmbeddedWindow();

        if (_activeVm != null && _activeVm.Status == "running")
            await ApplyConfiguredAudioVolumeAsync();

        if (_activeVm != null && UsesOfficialEmulatorNativeWindow() && forceRestart)
            await RestartActiveVmForLaunchProfileAsync();
        else
            StatusText.Text = "Settings applied";
    }

    private async Task ApplyConfiguredAudioVolumeAsync()
    {
        if (_activeVm == null) return;
        var settings = ReadBackendSettingsForWindow();
        if (!settings.SpeakerOutput) return;
        var mediaVolume = settings.AudioMuted
            ? 0
            : Math.Clamp((int)Math.Round(settings.AudioVolumePercent * 15.0 / 100.0), 0, 15);
        var applied = await RunBundledAdbAsync(
            $"-s {AndroidToolSerial} shell media volume --stream 3 --set {mediaVolume}", 5000);
        StatusText.Text = applied
            ? $"Settings applied · media volume {settings.AudioVolumePercent}%"
            : "Settings applied; Android volume will apply on next boot";
    }

    private async Task RestartActiveVmForLaunchProfileAsync()
    {
        if (_activeVm == null) return;
        var vm = _activeVm;
        _orientationChanging = true;
        _resizeDebounce?.Stop();
        try
        {
            StatusText.Text = "Restarting Android with selected renderer/settings...";
            await Task.Run(() => _engine.StopInstance(vm.Id));
            vm.Status = "stopped";
            _activeVm = null;
            _startInProgress = false;
            await Task.Delay(250);
        }
        finally
        {
            _orientationChanging = false;
        }

        await StartVmAsync(vm);
    }

    private void ToggleDebugStrip_Click(object sender, RoutedEventArgs e)
    {
        _debugStripVisible = !_debugStripVisible;
        if (!_debugStripVisible)
            BootProgressOverlay.Visibility = Visibility.Collapsed;
        var iconData = Geometry.Parse(_debugStripVisible
            ? "M3,4 L13,4 L13,12 L3,12 Z M5,7 L11,7 M5,9 L9,9"
            : "M3,4 L13,4 L13,12 L3,12 Z M4,13 L14,3");
        DebugStripIcon.Data = iconData;
        BottomDebugStripIcon.Data = iconData.Clone();
        ApplyDebugStripVisibility();
        ApplyWindowSizeFromBackendSettings();
        RepositionEmbeddedWindow();
    }

    private async void AgentCenter_Click(object sender, RoutedEventArgs e)
    {
        OverflowPopup.IsOpen = false;
        if (_activeVm == null)
        {
            StatusText.Text = "Start an Android instance before opening Agent Center";
            return;
        }
        var serial = _engine.GetAdbSerial(_activeVm.Id);
        var adbPath = _engine.GetAdbPath();
        if (string.IsNullOrWhiteSpace(serial) || !File.Exists(adbPath))
        {
            StatusText.Text = "Agent Center requires a ready bundled ADB device";
            return;
        }
        if (_agentCoordinator is null || !string.Equals(_agentCoordinatorSerial, serial, StringComparison.OrdinalIgnoreCase))
        {
            if (_agentInbox is not null) await _agentInbox.DisposeAsync();
            if (_agentCoordinator is not null) await _agentCoordinator.DisposeAsync();
            _agentCenterDialog?.Close();
            _agentCenterDialog = null;
            var runDirectory = _engine.GetRunArtifactsDirectory(_activeVm.Id);
            var evidenceRoot = Path.Combine(runDirectory ?? Path.Combine(RevmPaths.BaseDir, "runtime", "agent-runs"), "agents");
            _agentCoordinator = new AndroidAgentCoordinator(new AdbAgentTransport(adbPath), evidenceRoot, maximumConcurrentAgents: 8);
            var safeSerial = string.Concat(serial.Select(character => char.IsLetterOrDigit(character) || character is '.' or '-' or '_' ? character : '_'));
            _agentInbox = new AndroidAgentInbox(_agentCoordinator, Path.Combine(RevmPaths.BaseDir, "runtime", "agent-harness", safeSerial), serial);
            _agentCoordinatorSerial = serial;
        }
        if (_agentCenterDialog is null)
        {
            var dialog = new AgentCenterDialog(_agentCoordinator, serial, _agentInbox!.InboxDirectory) { Owner = this };
            dialog.Closed += (_, _) => { if (ReferenceEquals(_agentCenterDialog, dialog)) _agentCenterDialog = null; };
            _agentCenterDialog = dialog;
            dialog.Show();
        }
        else
        {
            if (_agentCenterDialog.WindowState == WindowState.Minimized) _agentCenterDialog.WindowState = WindowState.Normal;
            _agentCenterDialog.Activate();
        }
        StatusText.Text = "Agent Center ready for " + serial;
    }

    private void KeyboardMapping_Click(object sender, RoutedEventArgs e)
    {
        OverflowPopup.IsOpen = false;
        var dialog = new KeyboardMappingDialog { Owner = this };
        dialog.ShowDialog();
        StatusText.Text = "Keyboard mapping profile staged — SDK input passthrough active";
    }

    private void VolumeUp_Click(object sender, RoutedEventArgs e) => _ = SendAndroidKeyAsync(24, "Volume up");
    private void VolumeDown_Click(object sender, RoutedEventArgs e) => _ = SendAndroidKeyAsync(25, "Volume down");
    private void FlipScreen_Click(object sender, RoutedEventArgs e) => _ = FlipScreenAsync();
    private void AndroidBack_Click(object sender, RoutedEventArgs e) => _ = SendAndroidKeyAsync(4, "Back");
    private void AndroidHome_Click(object sender, RoutedEventArgs e) => _ = SendAndroidKeyAsync(3, "Home");
    private void AndroidAppSwitcher_Click(object sender, RoutedEventArgs e) => _ = SendAndroidKeyAsync(187, "App switcher");


    private async Task FlipScreenAsync()
    {
        if (_activeVm == null)
        {
            StatusText.Text = "Start Android before flipping the screen";
            return;
        }

        _screenFlipRotation = (_screenFlipRotation + 1) % 4;
        try
        {
            await _engine.SetAndroidRotationAsync(_activeVm.Id, _screenFlipRotation);
            StatusText.Text = $"Screen rotation set to {_screenFlipRotation * 90}°";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Flip screen failed: {ex.Message}";
        }
    }

    private void FullScreen_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "Window size is locked to the selected emulator resolution";
        ApplyWindowSizeFromBackendSettings();
        RepositionEmbeddedWindow();
    }

    private async void VideoRecorder_Click(object sender, RoutedEventArgs e)
    {
        OverflowPopup.IsOpen = false;
        if (_activeVm == null)
        {
            StatusText.Text = "Start Android before recording";
            return;
        }

        if (_recordingProcess is { HasExited: false })
        {
            await StopScreenRecordingAsync();
            return;
        }

        try
        {
            var dir = GetActiveRunArtifactsDirectory();
            Directory.CreateDirectory(dir);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            _recordingDevicePath = $"/sdcard/replayer-record-{stamp}.mp4";
            _recordingOutputPath = Path.Combine(dir, $"revm-record-{stamp}.mp4");

            var psi = new ProcessStartInfo(_engine.GetAdbPath())
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in new[] { "-s", AndroidToolSerial, "shell", "screenrecord", "--bit-rate", "8000000", "--time-limit", "1800", _recordingDevicePath })
                psi.ArgumentList.Add(argument);
            _recordingProcess = Process.Start(psi);
            if (_recordingProcess == null)
            {
                StatusText.Text = "Screen recording failed to start";
                return;
            }

            StatusText.Text = "Screen recording started — click recorder again to stop";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Screen recording failed: {ex.Message}";
        }
    }

    private async Task StopScreenRecordingAsync()
    {
        if (_recordingProcess == null || string.IsNullOrWhiteSpace(_recordingDevicePath) || string.IsNullOrWhiteSpace(_recordingOutputPath))
            return;

        try
        {
            StatusText.Text = "Stopping screen recording...";
            await RunBundledAdbAsync($"-s {AndroidToolSerial} shell \"pkill -2 screenrecord || killall -2 screenrecord || killall screenrecord\"", 5000);
            try
            {
                using var cts = new CancellationTokenSource(5000);
                await _recordingProcess.WaitForExitAsync(cts.Token);
            }
            catch
            {
                try { _recordingProcess.Kill(entireProcessTree: true); } catch { }
            }

            var pulled = await RunBundledAdbAsync($"-s {AndroidToolSerial} pull {_recordingDevicePath} \"{_recordingOutputPath}\"", 30000);
            await RunBundledAdbAsync($"-s {AndroidToolSerial} shell rm -f {_recordingDevicePath}", 5000);
            StatusText.Text = pulled && File.Exists(_recordingOutputPath)
                ? $"Recording saved: {_recordingOutputPath}"
                : "Recording stopped, but pull failed";
            if (pulled && File.Exists(_recordingOutputPath) && _activeVm != null)
                _engine.RegisterRunArtifact(_activeVm.Id, "screen-recording", _recordingOutputPath);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Recording stop failed: {ex.Message}";
        }
        finally
        {
            _recordingProcess?.Dispose();
            _recordingProcess = null;
            _recordingDevicePath = null;
            _recordingOutputPath = null;
        }
    }


    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_activeVm == null || e.Handled || Keyboard.FocusedElement is TextBox) return;

        // Official emulator text input must not go through ADB. WPF does not
        // reliably transfer keyboard focus into a re-parented Qt emulator HWND,
        // so send native key messages to that HWND. Do not synthesize key-up on
        // key-down: doing so prevents WPF TextInput from producing WM_CHAR and
        // breaks actual typing in Android text fields.
        if (UsesOfficialEmulatorNativeWindow() && _activeNativeWindow != IntPtr.Zero)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            var vk = KeyInterop.VirtualKeyFromKey(key);
            if (vk <= 0) return;

            try { SetFocus(_activeNativeWindow); } catch { }
            SendNativeKey(vk, isDown: true, isSystem: e.Key == Key.System);

            // Let printable keys continue to PreviewTextInput, where we emit
            // WM_CHAR. Non-text/navigation keys are fully handled here.
            if (!IsTextProducingKey(key)) e.Handled = true;
            return;
        }

        if (TryMapAndroidKey(e.Key, out var keyCode))
        {
            e.Handled = true;
            _ = SendAndroidKeyAsync(keyCode, string.Empty);
            return;
        }

        var text = KeyToInputText(e.Key, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        if (!string.IsNullOrEmpty(text))
        {
            e.Handled = true;
            _ = SendAndroidTextAsync(text);
        }
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (_activeVm == null || e.Handled || Keyboard.FocusedElement is TextBox) return;
        if (!UsesOfficialEmulatorNativeWindow() || _activeNativeWindow == IntPtr.Zero) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk <= 0) return;
        SendNativeKey(vk, isDown: false, isSystem: e.Key == Key.System);
        if (!IsTextProducingKey(key)) e.Handled = true;
    }

    private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_activeVm == null || e.Handled || Keyboard.FocusedElement is TextBox) return;
        if (!UsesOfficialEmulatorNativeWindow() || _activeNativeWindow == IntPtr.Zero) return;
        if (string.IsNullOrEmpty(e.Text)) return;

        try { SetFocus(_activeNativeWindow); } catch { }
        foreach (var ch in e.Text)
            PostMessage(_activeNativeWindow, WM_CHAR, (IntPtr)ch, IntPtr.Zero);
        e.Handled = true;
    }

    private void SendNativeKey(int vk, bool isDown, bool isSystem)
    {
        var scanCode = (int)MapVirtualKey((uint)vk, MAPVK_VK_TO_VSC);
        var repeat = 1;
        var lParam = repeat | (scanCode << 16);
        if (!isDown) lParam |= unchecked((int)0xC0000000); // previous down + transition up
        var msg = isSystem
            ? (isDown ? WM_SYSKEYDOWN : WM_SYSKEYUP)
            : (isDown ? WM_KEYDOWN : WM_KEYUP);
        PostMessage(_activeNativeWindow, msg, (IntPtr)vk, (IntPtr)lParam);
    }

    private static bool IsTextProducingKey(Key key)
    {
        if (key >= Key.A && key <= Key.Z) return true;
        if (key >= Key.D0 && key <= Key.D9) return true;
        if (key >= Key.NumPad0 && key <= Key.NumPad9) return true;
        return key is Key.Space or Key.OemPeriod or Key.OemComma or Key.OemMinus or Key.OemPlus or
            Key.OemQuestion or Key.OemSemicolon or Key.OemQuotes or Key.OemOpenBrackets or
            Key.OemCloseBrackets or Key.OemPipe or Key.OemTilde;
    }

    private static bool TryMapAndroidKey(Key key, out int keyCode)
    {
        keyCode = key switch
        {
            Key.Escape => 4,
            Key.Back => 67,
            Key.Enter => 66,
            Key.Tab => 61,
            Key.Up => 19,
            Key.Down => 20,
            Key.Left => 21,
            Key.Right => 22,
            Key.Delete => 112,
            Key.Home => 3,
            Key.PageUp => 92,
            Key.PageDown => 93,
            _ => 0
        };
        return keyCode != 0;
    }

    private static string? KeyToInputText(Key key, bool shift)
    {
        if (key >= Key.A && key <= Key.Z)
        {
            var c = (char)('a' + (key - Key.A));
            return shift ? char.ToUpperInvariant(c).ToString() : c.ToString();
        }
        if (key >= Key.D0 && key <= Key.D9) return ((char)('0' + (key - Key.D0))).ToString();
        if (key >= Key.NumPad0 && key <= Key.NumPad9) return ((char)('0' + (key - Key.NumPad0))).ToString();
        return key switch
        {
            Key.Space => "%s",
            Key.OemPeriod => ".",
            Key.OemComma => ",",
            Key.OemMinus => "-",
            Key.OemPlus => shift ? "+" : "=",
            Key.OemQuestion => shift ? "?" : "/",
            _ => null
        };
    }

    private async Task SendAndroidTextAsync(string text)
    {
        if (_activeVm == null) return;
        var escaped = text.Replace("%", "%25").Replace(" ", "%s");
        await RunBundledAdbAsync($"-s {AndroidToolSerial} shell input text \"{escaped}\"", 4000);
    }

    private async Task SendAndroidKeyAsync(int keyCode, string label)
    {
        if (_activeVm == null)
        {
            StatusText.Text = "No Android instance is active";
            return;
        }
        var ok = await RunBundledAdbAsync($"-s {AndroidToolSerial} shell input keyevent {keyCode}", 4000);
        if (!string.IsNullOrEmpty(label)) StatusText.Text = ok ? label : $"{label} failed — ADB not ready";
    }

    // === Instance Management ===
    private void NewInstance_Click(object sender, RoutedEventArgs e)
    {
        ShowInstanceManager();
    }

    private void Instance_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement el && el.DataContext is VmInstance vm)
        {
            InstancePopup.IsOpen = false;
            if (_activeVm?.Id == vm.Id && (string.Equals(vm.Status, "running", StringComparison.OrdinalIgnoreCase) || _startInProgress))
                _ = StopVmAsync(vm);
            else
                _ = StartVmAsync(vm);
        }
    }

    private async Task StopVmAsync(VmInstance vm)
    {
        try
        {
            await Task.Run(() => _engine.StopInstance(vm.Id));
            vm.Status = "stopped";
            if (_activeVm?.Id == vm.Id)
            {
                _activeVm = null;
                _activeNativeWindow = IntPtr.Zero;
                _rendererDisplayReady = false;
                _startInProgress = false;
                _bootProgressTimer?.Stop();
                try { _coldBootLauncher?.Close(); } catch { }
                _coldBootLauncher = null;
                if (!IsVisible) Show();
                NoVmOverlay.Visibility = Visibility.Visible;
            }
            StatusText.Text = $"Stopped: {vm.Name}";
            RefreshInstances();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Stop failed: {ex.Message}";
        }
    }

    private void DeleteInstance_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string vmId)
        {
            var vm = _instances.FirstOrDefault(i => i.Id == vmId);
            if (vm == null) return;
            if (vm.Id == "ld-0") { StatusText.Text = "Primary compatibility bridge cannot be deleted here."; return; }
            if (vm.Id == "google-avd-0")
            {
                _engine.StopInstance(vm.Id);
                vm.Status = "stopped";
                _activeVm = null;
                _startInProgress = false;
                StatusText.Text = "Stopped REPlayer Google Android instance";
                RefreshInstances();
                return;
            }
            if (MessageBox.Show($"Delete {vm.Name}? All data will be lost.",
                "Delete Instance", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                try { _engine.DeleteInstance(vmId); RefreshInstances(); StatusText.Text = $"Deleted: {vm.Name}"; }
                catch (Exception ex) { StatusText.Text = ex.Message; }
            }
        }
    }

    private async Task<bool> EnsureOfficialNetworkPolicyBeforeStartAsync()
    {
        if (_engine is not GoogleEmulatorRuntimeService) return true;

        var settings = ReadBackendSettingsForWindow();
        OfficialEmulatorNetworkPlan plan;
        try
        {
            plan = OfficialEmulatorNetworkIsolation.BuildPlan(settings, RuntimeBackendFactory.GetBaseDir());
        }
        catch (Exception ex)
        {
            StatusText.Text = "Network policy is invalid: " + ex.Message;
            MessageBox.Show(this,
                ex.Message + "\n\nOpen Settings > Network and correct the policy before starting Android.",
                "Network isolation",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        var verification = OfficialEmulatorNetworkIsolation.VerifyPlan(plan);
        if (!settings.SecureIsolationEnabled || verification.Success) return true;

        var answer = MessageBox.Show(this,
            verification.Message +
            $"\n\nREPlayer must install {plan.FirewallRules.Count} process-scoped Windows Firewall rules before this isolated guest can start." +
            "\n\nApply the policy now? On first use, Windows will request administrator approval once to install a hash-verified worker under Program Files and again to apply the policy. The main REPlayer application remains non-administrator.",
            "Administrator approval required for network isolation",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);
        if (answer != MessageBoxResult.Yes)
        {
            StatusText.Text = "Start cancelled: network isolation requires administrator approval.";
            return false;
        }

        StatusText.Text = "Waiting for Windows administrator approval...";
        var result = await OfficialEmulatorNetworkIsolation.ApplyConfiguredPolicyWithElevationAsync(settings, RuntimeBackendFactory.GetBaseDir());
        if (result.Success)
        {
            StatusText.Text = result.Message + " Starting Android...";
            return true;
        }

        StatusText.Text = "Start blocked: " + result.Message;
        MessageBox.Show(this,
            result.Message + "\n\nAndroid was not started and secure isolation remains fail-closed.",
            "Network isolation",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        return false;
    }

    private async Task StartVmAsync(VmInstance vm)
    {
        if (_startInProgress)
        {
            StatusText.Text = "Android is already starting; wait for the renderer to attach.";
            return;
        }

        if (_activeVm?.Id == vm.Id && vm.Status == "running")
        {
            RepositionEmbeddedWindow();
            StatusText.Text = $"{vm.Name} is already running";
            return;
        }

        _startInProgress = true;
        try
        {
            if (!await EnsureOfficialNetworkPolicyBeforeStartAsync())
            {
                _startInProgress = false;
                return;
            }
            _rendererDisplayReady = false;
            _androidPendingLatched = false;
            SetReadinessText(AdbReadinessText, "starting", "#DCDCAA");
            SetReadinessText(FastpipeReadinessText, "waiting", "#DCDCAA");
            SetReadinessText(AndroidReadinessText, "booting", "#DCDCAA");
            ReadinessHintText.Text = "runtime launch started";
            AppendRendererDebug($"{DateTime.Now:HH:mm:ss.fff} start requested: {vm.Name} ({vm.Id})");
            var previousVm = _activeVm;
            vm.Status = "starting";
            _activeVm = vm;
            BeginColdBootLauncher();
            ShowBootProgress("Loading");
            ApplyWindowSizeFromBackendSettings();
            await Dispatcher.InvokeAsync(() =>
            {
                UpdateLayout();
                DisplayHost.UpdateLayout();
            }, System.Windows.Threading.DispatcherPriority.Render);
            SetBootProgress(8, "Preparing");

            if (previousVm != null && previousVm.Id != vm.Id)
            {
                await Task.Run(() => _engine.StopInstance(previousVm.Id));
                previousVm.Status = "stopped";
            }

            await Task.Delay(60);
            SetBootProgress(22, "Display");

            var host = DisplayHost;
            // The runtime reparents native windows to DisplayHost.DisplayHandle,
            // so startup coordinates must be host-client-relative. Screen-space
            // PointToScreen values create portrait launch offsets after SetParent.
            var dpi = GetDpiForWindow(_windowHandle);
            var scale = dpi / 96.0;

            int x = 0;
            int y = 0;
            int w = (int)(host.ActualWidth * scale);
            int h = (int)(host.ActualHeight * scale);

            if (DisplayHost.DisplayHandle == IntPtr.Zero)
                throw new InvalidOperationException("Native display host is not ready.");

            SetBootProgress(45, "Launching runtime");
            await Task.Delay(60);
            SetBootProgress(72, "Starting renderer");

            var hostHandle = DisplayHost.DisplayHandle;
            var (started, startMessage) = _engine.StartInstanceWithDebug(vm.Id, hostHandle, x, y, w, h);
            if (!HandleNativeStartResult(vm, started, startMessage))
                return;

            // The native renderer surface was created at the current viewport size.
            // Do not issue a second startup resize here: it can race the first
            // producer frame/present path and make WPF appear unresponsive.

            vm.Status = "starting";
            NoVmOverlay.Visibility = Visibility.Collapsed;

            if (startMessage.Contains("waiting for Android renderer", StringComparison.OrdinalIgnoreCase))
            {
                StatusText.Text = "Waiting for native renderer...";
                return;
            }

            if (startMessage.Contains("bootstrap is running", StringComparison.OrdinalIgnoreCase))
            {
                SetBootProgress(72, "Renderer");
                StatusText.Text = "Native renderer/producer running...";
                return;
            }

            if (UsesOfficialEmulatorNativeWindow())
            {
                SetBootProgress(78, "Starting Android");
                StatusText.Text = "Official emulator started; waiting for Android boot completion...";
                return;
            }

            var finalStatus = startMessage.Contains("placeholder", StringComparison.OrdinalIgnoreCase)
                ? "Renderer scaffold only — Android VM not implemented yet"
                : $"{vm.Name} ready";
            await CompleteBootProgressAsync(finalStatus);
        }
        catch (Exception ex)
        {
            BootProgressOverlay.Visibility = Visibility.Collapsed;
            HideAndroidBootToast();
            StatusBar.Visibility = Visibility.Visible;
            StatusText.Text = $"Start failed: {ex.Message}";
            AppendRendererDebug($"{DateTime.Now:HH:mm:ss.fff} start failed: {ex.Message}");
            vm.Status = "error";
            _startInProgress = false;
            AbortColdBootLauncher(ex.Message);
        }
    }

    private bool HandleNativeStartResult(VmInstance vm, bool started, string startMessage)
    {
        AppendRendererDebug($"{DateTime.Now:HH:mm:ss.fff} start result: {started}; {startMessage}");
        if (!started)
            throw new InvalidOperationException(startMessage);
        SetBootProgress(72, "Waiting for renderer");
        return true;
    }

    private void HandleNativeStartResult(VmInstance vm, Task<(bool success, string debug)> task)
    {
        try
        {
            var (started, startMessage) = task.GetAwaiter().GetResult();
            HandleNativeStartResult(vm, started, startMessage);
        }
        catch (Exception ex)
        {
            BootProgressOverlay.Visibility = Visibility.Collapsed;
            HideAndroidBootToast();
            StatusBar.Visibility = Visibility.Visible;
            StatusText.Text = $"Start failed: {ex.Message}";
            AppendRendererDebug($"{DateTime.Now:HH:mm:ss.fff} background start failed: {ex.Message}");
            vm.Status = "error";
            _startInProgress = false;
            AbortColdBootLauncher(ex.Message);
        }
    }

    private void AppendRendererDebug(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        _rendererDebugLines.Add(line.Length > 260 ? line[..260] + "…" : line);
        while (_rendererDebugLines.Count > 8)
            _rendererDebugLines.RemoveAt(0);
        RendererDebugText.Text = string.Join(Environment.NewLine, _rendererDebugLines);
    }

    private void AppendRendererReadinessSnapshot()
    {
        if (_activeVm == null || _engine is not RevmNativeRuntimeService nativeRuntime)
            return;

        var snapshot = nativeRuntime.GetRendererReadinessSnapshot(_activeVm.Id);
        if (snapshot == null)
            return;

        AppendRendererDebug($"{DateTime.Now:HH:mm:ss.fff} snapshot: ready={snapshot.ReadyForPresent}; last={snapshot.LastEventType}; {snapshot.Summary}");
    }

    // === Tools ===
    private string AndroidToolSerial
    {
        get
        {
            if (_activeVm != null)
            {
                var serial = _engine.GetAdbSerial(_activeVm.Id);
                if (!string.IsNullOrWhiteSpace(serial)) return serial;
            }
            return _engine is GoogleEmulatorRuntimeService
                ? "official-emulator-not-running"
                : "emulator-5554";
        }
    }

    private string GetActiveRunArtifactsDirectory()
    {
        if (_activeVm != null)
        {
            var runDirectory = _engine.GetRunArtifactsDirectory(_activeVm.Id);
            if (!string.IsNullOrWhiteSpace(runDirectory)) return runDirectory;
        }
        return Path.Combine(RevmPaths.BaseDir, "captures");
    }

    private void Adb_Click(object sender, RoutedEventArgs e) { OverflowPopup.IsOpen = false; TryRun("cmd.exe", $"/k \"{_engine.GetAdbPath()}\" -s {AndroidToolSerial} shell", $"ADB shell opened for {AndroidToolSerial}"); }
    private void SpoofGps_Click(object sender, RoutedEventArgs e)
    {
        OverflowPopup.IsOpen = false;
        new GpsSpoofDialog(_engine.GetAdbPath(), AndroidToolSerial) { Owner = this }.ShowDialog();
    }
    private async void Screenshot_Click(object sender, RoutedEventArgs e)
    {
        OverflowPopup.IsOpen = false;
        var dir = GetActiveRunArtifactsDirectory();
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"revm-{DateTime.Now:yyyyMMdd-HHmmss}.png");
        var ok = await RunBundledAdbAsync($"-s {AndroidToolSerial} exec-out screencap -p", 8000, path);
        if (ok && _activeVm != null) _engine.RegisterRunArtifact(_activeVm.Id, "screenshot", path);
        StatusText.Text = ok ? $"Screenshot saved: {path}" : "Screenshot failed — ADB not ready";
    }

    private async void InjectApk_Click(object sender, RoutedEventArgs e)
    {
        OverflowPopup.IsOpen = false;
        var dlg = new OpenFileDialog { Filter = "APK files|*.apk", Title = "Select APK to install" };
        if (dlg.ShowDialog() != true) return;

        var apkPath = dlg.FileName;
        var apkName = Path.GetFileName(apkPath);
        try
        {
            if (_activeVm == null)
            {
                ShowApkInstallToast(apkName, "Start Android before installing", 0);
                StatusText.Text = "Start Android before installing APK";
                return;
            }

            ShowApkInstallToast(apkName, "Waiting for emulator", 8);
            var (ok, detail) = await InstallApkWithToastAsync(apkPath, apkName);
            ShowApkInstallToast(apkName, ok ? "Install complete" : detail, 100, isError: !ok);
            StatusText.Text = ok ? $"APK installed: {apkName}" : $"APK install failed: {detail}";
            if (ok)
                _ = Task.Delay(2200).ContinueWith(_ => Dispatcher.Invoke(HideApkInstallToast));
        }
        catch (Exception ex)
        {
            ShowApkInstallToast(apkName, ex.Message, 100, isError: true);
            StatusText.Text = ex.Message;
        }
    }

    private async Task<(bool Ok, string Detail)> InstallApkWithToastAsync(string apkPath, string apkName)
    {
        if (!File.Exists(apkPath)) return (false, "APK file not found");
        var adbPath = _engine.GetAdbPath();
        if (!File.Exists(adbPath)) return (false, "Bundled ADB not found");

        ShowApkInstallToast(apkName, "Checking ADB device", 15);
        var devices = await RunProcessCaptureAsync(adbPath, new[] { "devices" }, 8000);
        if (!devices.Output.Contains($"{AndroidToolSerial}	device", StringComparison.OrdinalIgnoreCase))
            return (false, $"{AndroidToolSerial} is not ready");

        ShowApkInstallToast(apkName, "Staging APK", 28);
        var stagingRoot = Path.Combine(GetActiveRunArtifactsDirectory(), "staging");
        var stagingDir = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDir);
        var stagedPath = Path.Combine(stagingDir, string.IsNullOrWhiteSpace(apkName) ? "install.apk" : apkName);
        File.Copy(apkPath, stagedPath, overwrite: true);

        ShowApkInstallToast(apkName, "Pushing and installing", 38);
        var result = await RunProcessCaptureAsync(adbPath, new[] { "-s", AndroidToolSerial, "install", "-r", "-d", "-g", "--no-streaming", stagedPath }, 240000);
        var text = (result.Output + "\n" + result.Error).Trim();
        var ok = result.ExitCode == 0 && text.Contains("Success", StringComparison.OrdinalIgnoreCase);
        if (!ok && text.Contains("unknown option", StringComparison.OrdinalIgnoreCase))
        {
            ShowApkInstallToast(apkName, "Retrying install", 58);
            result = await RunProcessCaptureAsync(adbPath, new[] { "-s", AndroidToolSerial, "install", "-r", "-d", "-g", stagedPath }, 240000);
            text = (result.Output + "\n" + result.Error).Trim();
            ok = result.ExitCode == 0 && text.Contains("Success", StringComparison.OrdinalIgnoreCase);
        }
        if (!ok && ShouldAttemptDebugSigningAfterInstallFailure(text))
        {
            ShowApkInstallToast(apkName, "Signing APK", 62);
            var signed = await TrySignApkWithDebugCertificateAsync(stagedPath);
            if (signed.Ok)
            {
                ShowApkInstallToast(apkName, "Installing signed APK", 72);
                result = await RunProcessCaptureAsync(adbPath, new[] { "-s", AndroidToolSerial, "install", "-r", "-d", "-g", "--no-streaming", signed.SignedApkPath }, 240000);
                text = (result.Output + "\n" + result.Error).Trim();
                ok = result.ExitCode == 0 && text.Contains("Success", StringComparison.OrdinalIgnoreCase);
                if (!ok && text.Contains("unknown option", StringComparison.OrdinalIgnoreCase))
                {
                    result = await RunProcessCaptureAsync(adbPath, new[] { "-s", AndroidToolSerial, "install", "-r", "-d", "-g", signed.SignedApkPath }, 240000);
                    text = (result.Output + "\n" + result.Error).Trim();
                    ok = result.ExitCode == 0 && text.Contains("Success", StringComparison.OrdinalIgnoreCase);
                }
            }
            else
            {
                text = signed.Detail;
            }
        }
        try
        {
            var installLogPath = Path.Combine(GetActiveRunArtifactsDirectory(), $"apk-install-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}.log");
            using var apkStream = new FileStream(apkPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var apkHash = Convert.ToHexString(SHA256.HashData(apkStream)).ToLowerInvariant();
            File.WriteAllText(installLogPath,
                $"APK: {Path.GetFileName(apkPath)}{Environment.NewLine}" +
                $"SHA256: {apkHash}{Environment.NewLine}" +
                $"ADB serial: {AndroidToolSerial}{Environment.NewLine}" +
                $"Result: {(ok ? "success" : "failure")}{Environment.NewLine}{Environment.NewLine}{text}");
            if (_activeVm != null) _engine.RegisterRunArtifact(_activeVm.Id, "apk-install-log", installLogPath);
        }
        catch (Exception ex)
        {
            StatusText.Text = "APK installed, but case logging failed: " + ex.Message;
        }
        try { Directory.Delete(stagingDir, recursive: true); } catch { }
        ShowApkInstallToast(apkName, ok ? "Finalizing install" : SummarizeAdbInstallFailure(text), ok ? 92 : 100, isError: !ok);
        return ok ? (true, "Success") : (false, SummarizeAdbInstallFailure(text));
    }

    private async Task<(bool Ok, string SignedApkPath, string Detail)> TrySignApkWithDebugCertificateAsync(string apkPath)
    {
        var keytool = FindJavaTool("keytool.exe");
        if (keytool == null)
            return (false, apkPath, "APK parse failed and Java keytool was not found for debug signing");

        var signingDir = Path.Combine(RevmPaths.BaseDir, "runtime", "apk-signing");
        Directory.CreateDirectory(signingDir);
        var keystorePath = Path.Combine(signingDir, "replayer-debug.keystore");
        const string alias = "androiddebugkey";
        const string password = "android";

        if (!File.Exists(keystorePath))
        {
            var keyResult = await RunProcessCaptureAsync(keytool, new[]
            {
                "-genkeypair", "-v",
                "-keystore", keystorePath,
                "-storepass", password,
                "-keypass", password,
                "-alias", alias,
                "-keyalg", "RSA",
                "-keysize", "2048",
                "-validity", "10000",
                "-dname", "CN=Android Debug,O=REPlayer,C=US"
            }, 30000);
            if (keyResult.ExitCode != 0 || !File.Exists(keystorePath))
                return (false, apkPath, SummarizeAdbInstallFailure(keyResult.Output + "\n" + keyResult.Error));
        }

        var tools = await EnsureAndroidBuildToolsSigningToolsAsync();
        if (tools.ApkSigner != null)
        {
            var alignedApkPath = Path.Combine(Path.GetDirectoryName(apkPath)!, Path.GetFileNameWithoutExtension(apkPath) + ".aligned.apk");
            var signedApkPath = Path.Combine(Path.GetDirectoryName(apkPath)!, Path.GetFileNameWithoutExtension(apkPath) + ".debug-signed.apk");
            var inputForSigning = apkPath;
            if (tools.ZipAlign != null)
            {
                var align = await RunProcessCaptureAsync(tools.ZipAlign, new[] { "-f", "-p", "4", apkPath, alignedApkPath }, 120000);
                if (align.ExitCode == 0 && File.Exists(alignedApkPath))
                    inputForSigning = alignedApkPath;
            }

            var sign = await RunProcessCaptureAsync(tools.ApkSigner, new[]
            {
                "sign",
                "--ks", keystorePath,
                "--ks-key-alias", alias,
                "--ks-pass", "pass:" + password,
                "--key-pass", "pass:" + password,
                "--out", signedApkPath,
                inputForSigning
            }, 120000);
            if (sign.ExitCode == 0 && File.Exists(signedApkPath))
                return (true, signedApkPath, "Signed with Android apksigner");
        }

        var jarsigner = FindJavaTool("jarsigner.exe");
        if (jarsigner == null)
            return (false, apkPath, "APK parse failed and Android apksigner/jarsigner were not found");

        var jarSignedApkPath = Path.Combine(Path.GetDirectoryName(apkPath)!, Path.GetFileNameWithoutExtension(apkPath) + ".debug-v1-signed.apk");
        var signResult = await RunProcessCaptureAsync(jarsigner, new[]
        {
            "-keystore", keystorePath,
            "-storepass", password,
            "-keypass", password,
            "-signedjar", jarSignedApkPath,
            apkPath,
            alias
        }, 120000);
        if (signResult.ExitCode != 0 || !File.Exists(jarSignedApkPath))
            return (false, apkPath, SummarizeAdbInstallFailure(signResult.Output + "\n" + signResult.Error));

        return (true, jarSignedApkPath, "Signed with REPlayer debug certificate");
    }

    private async Task<(string? ApkSigner, string? ZipAlign)> EnsureAndroidBuildToolsSigningToolsAsync()
    {
        var sdkRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(_engine.GetAdbPath()) ?? ".", ".."));
        var apksigner = Directory.Exists(sdkRoot)
            ? Directory.GetFiles(sdkRoot, "apksigner.bat", SearchOption.AllDirectories).OrderByDescending(p => p).FirstOrDefault()
            : null;
        var zipalign = Directory.Exists(sdkRoot)
            ? Directory.GetFiles(sdkRoot, "zipalign.exe", SearchOption.AllDirectories).OrderByDescending(p => p).FirstOrDefault()
            : null;
        if (apksigner != null)
            return (apksigner, zipalign);

        var buildToolsDir = Path.Combine(sdkRoot, "build-tools", "33.0.2");
        apksigner = Path.Combine(buildToolsDir, "apksigner.bat");
        zipalign = Path.Combine(buildToolsDir, "zipalign.exe");
        if (File.Exists(apksigner))
            return (apksigner, File.Exists(zipalign) ? zipalign : null);

        ShowApkInstallToast(_apkInstallApkName, "Downloading Android signing tools", 64);
        Directory.CreateDirectory(Path.Combine(sdkRoot, "build-tools"));
        var zipPath = Path.Combine(Path.GetTempPath(), "replayer-build-tools-33.0.2-windows.zip");
        if (!File.Exists(zipPath))
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            await using var input = await http.GetStreamAsync("https://dl.google.com/android/repository/build-tools_r33.0.2-windows.zip");
            await using var output = File.Create(zipPath);
            await input.CopyToAsync(output);
        }

        var extractRoot = Path.Combine(sdkRoot, "build-tools", "_extract-33.0.2");
        if (Directory.Exists(extractRoot)) Directory.Delete(extractRoot, recursive: true);
        ZipFile.ExtractToDirectory(zipPath, extractRoot);
        var extractedApkSigner = Directory.GetFiles(extractRoot, "apksigner.bat", SearchOption.AllDirectories).FirstOrDefault();
        if (extractedApkSigner == null)
            return (null, null);
        var extractedDir = Path.GetDirectoryName(extractedApkSigner)!;
        if (Directory.Exists(buildToolsDir)) Directory.Delete(buildToolsDir, recursive: true);
        Directory.Move(extractedDir, buildToolsDir);
        try { Directory.Delete(extractRoot, recursive: true); } catch { }

        apksigner = Path.Combine(buildToolsDir, "apksigner.bat");
        zipalign = Path.Combine(buildToolsDir, "zipalign.exe");
        return (File.Exists(apksigner) ? apksigner : null, File.Exists(zipalign) ? zipalign : null);
    }

    private static string? FindJavaTool(string exeName)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), exeName);
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var adoptiumRoot = Path.Combine(programFiles, "Eclipse Adoptium");
        if (Directory.Exists(adoptiumRoot))
        {
            try
            {
                var found = Directory.GetFiles(adoptiumRoot, exeName, SearchOption.AllDirectories)
                    .OrderByDescending(path => path)
                    .FirstOrDefault();
                if (found != null) return found;
            }
            catch { }
        }

        foreach (var javaHome in new[] { Environment.GetEnvironmentVariable("JAVA_HOME"), Environment.GetEnvironmentVariable("JDK_HOME") })
        {
            try
            {
                if (string.IsNullOrWhiteSpace(javaHome)) continue;
                var candidate = Path.Combine(javaHome, "bin", exeName);
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }

        return null;
    }

    private static bool ShouldAttemptDebugSigningAfterInstallFailure(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return text.Contains("INSTALL_PARSE_FAILED_NO_CERTIFICATES", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("INSTALL_PARSE_FAILED", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Failure [-124", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("package failure -124", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("No certificates", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Failed collecting certificates", StringComparison.OrdinalIgnoreCase);
    }

    private static string SummarizeAdbInstallFailure(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "ADB install failed";
        var line = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Reverse()
            .FirstOrDefault(l => l.Contains("Failure", StringComparison.OrdinalIgnoreCase) || l.Contains("Exception", StringComparison.OrdinalIgnoreCase) || l.Contains("error", StringComparison.OrdinalIgnoreCase))
            ?? text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault()
            ?? "ADB install failed";
        return line.Length > 120 ? line[..120] + "…" : line;
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunProcessCaptureAsync(string fileName, string[] args, int timeoutMs)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start " + fileName);
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            return (-1, await stdoutTask, "Timed out");
        }
        return (proc.ExitCode, await stdoutTask, await stderrTask);
    }

    private async Task<bool> RunBundledAdbAsync(string args, int timeoutMs, string? stdoutFile = null)
    {
        try
        {
            var psi = new ProcessStartInfo(_engine.GetAdbPath(), args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;

            var stdoutTask = stdoutFile == null
                ? proc.StandardOutput.ReadToEndAsync()
                : CopyStdoutToFileAsync(proc, stdoutFile);
            var stderrTask = proc.StandardError.ReadToEndAsync();
            var waitTask = proc.WaitForExitAsync();
            var completed = await Task.WhenAny(waitTask, Task.Delay(timeoutMs));
            if (completed != waitTask)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return false;
            }
            await stdoutTask;
            var stderr = await stderrTask;
            if (proc.ExitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
                StatusText.Text = stderr.Trim();
            return proc.ExitCode == 0;
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            return false;
        }
    }

    private static async Task<string> CopyStdoutToFileAsync(Process proc, string path)
    {
        await using var fs = File.Create(path);
        await proc.StandardOutput.BaseStream.CopyToAsync(fs);
        return path;
    }

    private void TryRun(string exe, string args, string successMsg)
    {
        try
        {
            Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true });
            if (!string.IsNullOrEmpty(successMsg)) StatusText.Text = successMsg;
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _engine.StopAll();
        base.OnClosing(e);
    }
}
