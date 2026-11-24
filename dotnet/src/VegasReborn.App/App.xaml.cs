using System.Diagnostics;
using System.Windows;
using VegasReborn.App.ViewModels;
using VegasReborn.Core;
using VegasReborn.Core.Update;

namespace VegasReborn.App;

public partial class App : Application
{
    private MainViewModel? model;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Update commands run before any window, account or game data.
        switch (UpdateCommandLine.Parse(e.Args))
        {
            case ApplyUpdateCommand apply:
                Shutdown(await new UpdateWindow(new StagedUpdateHelper(apply.PlanPath, apply.ParentProcessId)).RunAsync());
                return;
            case not null:
                Shutdown(UpdateCommandLine.TryRun(e.Args) ?? 2);
                return;
        }

        var paths = AppPaths.ForInstallation();
        if (UpdaterOnlyPackage.NeedsFullInstall(paths))
        {
            if (MessageBox.Show(UpdaterOnlyPackage.Message, UpdaterOnlyPackage.Title, MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
            {
                Process.Start(new ProcessStartInfo(UpdaterOnlyPackage.FullDownloadPage.AbsoluteUri) { UseShellExecute = true })?.Dispose();
            }
            Shutdown(0);
            return;
        }

        model = new MainViewModel(paths);
        var window = new MainWindow(model);
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        model.Updates.ShutdownRequested += (_, _) => Dispatcher.BeginInvoke(() => Shutdown(0));
        window.Show();
        _ = model.DetectInstallsAsync();

        await model.Account.RefreshFromServiceAsync();
        if (model.Preferences.CheckUpdates)
        {
            await model.Updates.CheckAsync();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        model?.Dispose();
        base.OnExit(e);
    }
}
