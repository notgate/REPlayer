using System.Windows;
using System.Windows.Input;

namespace ReVM;

public partial class ExitConfirmDialog : Window
{
    public ExitConfirmDialog()
    {
        InitializeComponent();
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
