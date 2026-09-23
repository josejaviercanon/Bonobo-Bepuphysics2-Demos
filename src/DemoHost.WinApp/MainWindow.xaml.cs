using Microsoft.UI.Xaml;

namespace DemoHost;

/// <summary>Application window; hosts the WebView2 page.</summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");

        RootFrame.Navigate(typeof(MainPage));

        Closed += OnClosed;
    }

    /// <summary>Stops the simulation and releases the WebView2 shared buffers.</summary>
    private void OnClosed(object sender, WindowEventArgs args)
    {
        (RootFrame.Content as MainPage)?.Shutdown();
    }
}
