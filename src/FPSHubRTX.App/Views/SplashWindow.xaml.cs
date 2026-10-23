using System.Windows;
using System.Windows.Threading;

namespace FPSHubRTX.App.Views;

/// <summary>Loading screen shown while the library and services start.</summary>
public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
    }

    public void SetStatus(string text)
    {
        Status.Text = text;
        // Let the splash repaint during synchronous start-up work.
        Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
    }
}
