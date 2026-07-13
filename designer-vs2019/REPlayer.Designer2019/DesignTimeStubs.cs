using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ReVM
{
    // Design-only stand-ins so Visual Studio 2019 / Blend can render the XAML without loading the .NET 9 runtime.
    public sealed class NativeChildHost : Border { }

    public partial class MainWindow : Window
    {
        public MainWindow() { InitializeComponent(); }
        private void Adb_Click(object sender, RoutedEventArgs e) { }
        private void AndroidAppSwitcher_Click(object sender, RoutedEventArgs e) { }
        private void AndroidBack_Click(object sender, RoutedEventArgs e) { }
        private void AndroidHome_Click(object sender, RoutedEventArgs e) { }
        private void CloseApkInstallToast_Click(object sender, RoutedEventArgs e) { }
        private void CloseBootToast_Click(object sender, RoutedEventArgs e) { }
        private void Close_Click(object sender, RoutedEventArgs e) { }
        private void CollapseSidebar_Click(object sender, RoutedEventArgs e) { }
        private void DeleteInstance_Click(object sender, RoutedEventArgs e) { }
        private void FlipScreen_Click(object sender, RoutedEventArgs e) { }
        private void FullScreen_Click(object sender, RoutedEventArgs e) { }
        private void InjectApk_Click(object sender, RoutedEventArgs e) { }
        private void Instance_Click(object sender, RoutedEventArgs e) { }
        private void KeyboardMapping_Click(object sender, RoutedEventArgs e) { }
        private void OpenIde_Click(object sender, RoutedEventArgs e) { }
        private void Maximize_Click(object sender, RoutedEventArgs e) { }
        private void MiniMode_Click(object sender, RoutedEventArgs e) { }
        private void Minimize_Click(object sender, RoutedEventArgs e) { }
        private void NewInstance_Click(object sender, RoutedEventArgs e) { }
        private void Screenshot_Click(object sender, RoutedEventArgs e) { }
        private void Settings_Click(object sender, RoutedEventArgs e) { }
        private void SpoofGps_Click(object sender, RoutedEventArgs e) { }
        private void TitleMenu_Click(object sender, RoutedEventArgs e) { }
        private void ToggleDebugStrip_Click(object sender, RoutedEventArgs e) { }
        private void ToggleEngine_Click(object sender, RoutedEventArgs e) { }
        private void ToggleInstances_Click(object sender, RoutedEventArgs e) { }
        private void ToggleOrientation_Click(object sender, RoutedEventArgs e) { }
        private void VideoRecorder_Click(object sender, RoutedEventArgs e) { }
        private void VolumeDown_Click(object sender, RoutedEventArgs e) { }
        private void VolumeUp_Click(object sender, RoutedEventArgs e) { }
    }

    public partial class ToastNotificationControl : UserControl
    {
        public event RoutedEventHandler DismissRequested;
        public ToastNotificationControl() { InitializeComponent(); }
        private void Dismiss_Click(object sender, RoutedEventArgs e) => DismissRequested?.Invoke(this, e);
    }

    public partial class SettingsDialog : Window
    {
        public SettingsDialog() { InitializeComponent(); }
        private void Apply_Click(object sender, RoutedEventArgs e) { }
        private void BrowseRuntime_Click(object sender, RoutedEventArgs e) { }
        private void Cancel_Click(object sender, RoutedEventArgs e) { }
        private void Nav_Click(object sender, RoutedEventArgs e) { }
        private void Save_Click(object sender, RoutedEventArgs e) { }
        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e) { }
    }

    public partial class InstanceManagerDialog : Window
    {
        public InstanceManagerDialog() { InitializeComponent(); }
        private void AddInstance_Click(object sender, RoutedEventArgs e) { }
        private void Close_Click(object sender, RoutedEventArgs e) { }
        private void CpuSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { }
        private void CpuBox_LostFocus(object sender, RoutedEventArgs e) { }
        private void DeleteInstance_Click(object sender, RoutedEventArgs e) { }
        private void EditInstance_Click(object sender, RoutedEventArgs e) { }
        private void NumericBox_KeyDown(object sender, KeyEventArgs e) { }
        private void RamSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { }
        private void RamBox_LostFocus(object sender, RoutedEventArgs e) { }
        private void StorageSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { }
        private void StorageBox_LostFocus(object sender, RoutedEventArgs e) { }
        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e) { }
        private void ToggleInstance_Click(object sender, RoutedEventArgs e) { }
    }

    public partial class NewInstanceDialog : Window
    {
        public NewInstanceDialog() { InitializeComponent(); }
        private void Cancel_Click(object sender, RoutedEventArgs e) { }
        private void Create_Click(object sender, RoutedEventArgs e) { }
    }

    public partial class SetupWizard : Window
    {
        public SetupWizard() { InitializeComponent(); }
        private void Cancel_Click(object sender, RoutedEventArgs e) { }
        private void Finish_Click(object sender, RoutedEventArgs e) { }
        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e) { }
    }

    public partial class GpsSpoofDialog : Window
    {
        public GpsSpoofDialog() { InitializeComponent(); }
        private void Close_Click(object sender, RoutedEventArgs e) { }
        private void Reset_Click(object sender, RoutedEventArgs e) { }
        private void Set_Click(object sender, RoutedEventArgs e) { }
    }

    public partial class ExitConfirmDialog : Window
    {
        public ExitConfirmDialog() { InitializeComponent(); }
        private void Cancel_Click(object sender, RoutedEventArgs e) { }
        private void Exit_Click(object sender, RoutedEventArgs e) { }
        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e) { }
    }

    public partial class KeyboardMappingDialog : Window
    {
        public KeyboardMappingDialog() { InitializeComponent(); }
        private void Close_Click(object sender, RoutedEventArgs e) { }
        private void Save_Click(object sender, RoutedEventArgs e) { }
        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e) { }
    }
}
