using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using VegasReborn.App.ViewModels;

namespace VegasReborn.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel model;

    public MainWindow(MainViewModel model)
    {
        InitializeComponent();
        this.model = model;
        DataContext = model;
        SourceInitialized += (_, _) => UseDarkTitleBar();
        model.MinimizeRequested += (_, minimize) =>
        {
            if (minimize)
            {
                WindowState = WindowState.Minimized;
            }
            else if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
                Activate();
            }
        };
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // The companion saves the character after the game exits; closing now would skip that.
        if (model.IsBusy)
        {
            MessageBox.Show(this, "Close the game first so the launcher can save your character.", "Vegas Reborn",
                MessageBoxButton.OK, MessageBoxImage.Information);
            e.Cancel = true;
        }
        base.OnClosing(e);
    }

    private void UseDarkTitleBar()
    {
        // DWMWA_USE_IMMERSIVE_DARK_MODE; ignored on Windows versions without it.
        const int useImmersiveDarkMode = 20;
        int enabled = 1;
        var handle = new WindowInteropHelper(this).Handle;
        _ = DwmSetWindowAttribute(handle, useImmersiveDarkMode, ref enabled, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
