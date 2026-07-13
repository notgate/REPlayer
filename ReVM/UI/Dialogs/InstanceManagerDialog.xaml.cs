using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ReVM;

public partial class InstanceManagerDialog : Window
{
    private readonly IAndroidRuntimeBackend _engine;
    private readonly ObservableCollection<VmInstance> _instances = new();
    private readonly Action<VmInstance> _startInstance;
    private readonly Action<VmInstance> _stopInstance;
    private readonly Func<string, bool> _isInstanceRunning;
    private bool _syncingInputs;
    private string? _editingInstanceId;

    public InstanceManagerDialog(IAndroidRuntimeBackend engine, Action<VmInstance> startInstance, Action<VmInstance> stopInstance, Func<string, bool> isInstanceRunning)
    {
        InitializeComponent();
        _engine = engine;
        _startInstance = startInstance;
        _stopInstance = stopInstance;
        _isInstanceRunning = isInstanceRunning;
        InstanceList.ItemsSource = _instances;
        RefreshInstances();
        SyncBoxesFromSliders();
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void RefreshInstances()
    {
        _instances.Clear();
        foreach (var vm in _engine.GetInstances()) _instances.Add(vm);
    }

    private void AddInstance_Click(object sender, RoutedEventArgs e)
    {
        ApplyNumericBoxes();
        var name = string.IsNullOrWhiteSpace(NameBox.Text) ? "Android" : NameBox.Text.Trim();
        var profile = ProfileCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag ? tag : "api34-persona";
        var cpu = ClampInt(ParseInt(CpuBox.Text, (int)CpuSlider.Value), 1, 64);
        var ram = ClampInt(ParseInt(RamBox.Text, (int)RamSlider.Value), 1024, 524288);
        var storage = ClampInt(ParseInt(StorageBox.Text, (int)StorageSlider.Value), 8, 4096);
        if (_editingInstanceId == null)
        {
            _engine.CreateInstance(name, cpu, ram, storage, profile);
        }
        else
        {
            _engine.UpdateInstance(_editingInstanceId, name, cpu, ram, storage, profile);
            _editingInstanceId = null;
            SaveInstanceButton.Content = "Add";
        }
        NameBox.Text = "Android";
        ProfileCombo.SelectedIndex = 0;
        RefreshInstances();
    }

    private void ToggleInstance_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        var vm = _instances.FirstOrDefault(i => i.Id == id);
        if (vm == null) return;
        Close();
        if (_isInstanceRunning(vm.Id) ||
            string.Equals(vm.Status, "running", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(vm.Status, "starting", StringComparison.OrdinalIgnoreCase))
            _stopInstance(vm);
        else
            _startInstance(vm);
    }

    private void EditInstance_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        var vm = _instances.FirstOrDefault(i => i.Id == id);
        if (vm == null) return;
        _editingInstanceId = vm.Id;
        NameBox.Text = vm.Name;
        CpuSlider.Value = ClampInt(vm.CpuCount, 1, 64);
        RamSlider.Value = ClampInt(vm.RamMB, 1024, 524288);
        StorageSlider.Value = ClampInt(vm.StorageGB, 8, 4096);
        SelectProfile(vm.BootProfile);
        SyncBoxesFromSliders();
        SaveInstanceButton.Content = "Save";
    }

    private void DeleteInstance_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        var vm = _instances.FirstOrDefault(i => i.Id == id);
        if (vm == null) return;
        if (MessageBox.Show(this, $"Delete {vm.Name}? Its storage and snapshots will be removed.", "Delete Instance",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _instances.Remove(vm);
        if (_editingInstanceId == id)
        {
            _editingInstanceId = null;
            SaveInstanceButton.Content = "Add";
        }
        try
        {
            _engine.DeleteInstance(id);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Delete Instance", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        RefreshInstances();
    }

    private void CpuSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => SyncBoxesFromSliders();
    private void RamSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => SyncBoxesFromSliders();
    private void StorageSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => SyncBoxesFromSliders();

    private void CpuBox_LostFocus(object sender, RoutedEventArgs e) => ApplyNumericBoxes();
    private void RamBox_LostFocus(object sender, RoutedEventArgs e) => ApplyNumericBoxes();
    private void StorageBox_LostFocus(object sender, RoutedEventArgs e) => ApplyNumericBoxes();

    private void NumericBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyNumericBoxes();
            e.Handled = true;
        }
    }

    private void SyncBoxesFromSliders()
    {
        if (_syncingInputs || CpuBox == null || RamBox == null || StorageBox == null) return;
        _syncingInputs = true;
        CpuBox.Text = ((int)Math.Round(CpuSlider.Value)).ToString();
        RamBox.Text = ((int)Math.Round(RamSlider.Value)).ToString();
        StorageBox.Text = ((int)Math.Round(StorageSlider.Value)).ToString();
        _syncingInputs = false;
    }

    private void ApplyNumericBoxes()
    {
        if (_syncingInputs) return;
        _syncingInputs = true;
        CpuSlider.Value = ClampInt(ParseInt(CpuBox.Text, (int)CpuSlider.Value), 1, 64);
        RamSlider.Value = ClampInt(ParseInt(RamBox.Text, (int)RamSlider.Value), 1024, 524288);
        StorageSlider.Value = ClampInt(ParseInt(StorageBox.Text, (int)StorageSlider.Value), 8, 4096);
        CpuBox.Text = ((int)CpuSlider.Value).ToString();
        RamBox.Text = ((int)RamSlider.Value).ToString();
        StorageBox.Text = ((int)StorageSlider.Value).ToString();
        _syncingInputs = false;
    }

    private void SelectProfile(string? bootProfile)
    {
        foreach (var item in ProfileCombo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), bootProfile, StringComparison.OrdinalIgnoreCase))
            {
                ProfileCombo.SelectedItem = item;
                return;
            }
        }
        ProfileCombo.SelectedIndex = 0;
    }

    private static int ParseInt(string? text, int fallback) => int.TryParse(text, out var value) ? value : fallback;
    private static int ClampInt(int value, int min, int max) => Math.Max(min, Math.Min(max, value));
}
