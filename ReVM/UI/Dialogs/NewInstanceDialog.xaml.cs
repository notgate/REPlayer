using System.Windows;
using System.Windows.Controls;

namespace ReVM;

public partial class NewInstanceDialog : Window
{
    public string VmName => NameBox.Text.Trim();
    public int CpuCount => (int)CpuSlider.Value;
    public int RamMB => (int)RamSlider.Value;
    public int StorageGB => (int)StorageSlider.Value;
    public string BootProfile => ProfileCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag ? tag : "api34-persona";

    public NewInstanceDialog()
    {
        InitializeComponent();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(VmName))
        {
            MessageBox.Show("Please enter a name for the instance.", "Validation",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
        Close();
    }
}
