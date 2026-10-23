using System.Windows;

namespace FPSHubRTX.App.Dialogs;

public partial class PromptWindow : Window
{
    public PromptWindow(string title, string message, string defaultValue)
    {
        InitializeComponent();
        Title = title;
        Message.Text = message;
        Input.Text = defaultValue;
        Loaded += (_, _) =>
        {
            Input.Focus();
            Input.SelectAll();
        };
    }

    public string Value => Input.Text;

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
