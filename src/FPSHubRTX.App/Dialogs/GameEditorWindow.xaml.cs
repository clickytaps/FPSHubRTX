using System.Windows;
using FPSHubRTX.ViewModels;

namespace FPSHubRTX.App.Dialogs;

public partial class GameEditorWindow : Window
{
    public GameEditorWindow()
    {
        InitializeComponent();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is GameEditorViewModel vm && !vm.Validate()) return;
        DialogResult = true;
    }
}
