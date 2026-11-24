using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using VegasReborn.Core;
using VegasReborn.Core.Content;
using VegasReborn.Core.Update;

namespace VegasReborn.App.ViewModels;

/// <summary>The Maps Pack section of the Vegas 1 page: download, progress, cancel, remove and update.</summary>
public sealed class MapsPackViewModel : ObservableObject
{
    public const string FallbackSize = "727 MB";
    public const string GameRunningText = "Close the game first.";

    private static readonly TimeSpan RunningCacheLifetime = TimeSpan.FromSeconds(2);

    private readonly MainViewModel owner;
    private readonly AppPaths paths;
    private readonly Func<bool> gameRunning;
    private MapsPackState state;
    private bool isDownloading;
    private double percent;
    private string statusText = "";
    private bool updateAvailable;
    private string sizeText = FallbackSize;
    private CancellationTokenSource? cancel;
    private bool runningCached;
    private long runningCheckedAt = long.MinValue;

    public MapsPackViewModel(MainViewModel owner, AppPaths paths, Func<bool>? gameRunning = null)
    {
        this.owner = owner;
        this.paths = paths;
        this.gameRunning = gameRunning ?? GameProcesses.AnyRunning;
        DownloadCommand = new RelayCommand(DownloadAsync, CanChange);
        CancelCommand = new RelayCommand(() =>
        {
            cancel?.Cancel();
            return Task.CompletedTask;
        }, () => IsDownloading);
        RemoveCommand = new RelayCommand(RemoveAsync, CanChange);
        UpdateCommand = new RelayCommand(DownloadAsync, CanChange);
        RefreshState();
        _ = CheckForUpdateAsync();
    }

    public ICommand DownloadCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand UpdateCommand { get; }

    public MapsPackState State
    {
        get => state;
        private set
        {
            if (Set(ref state, value))
            {
                RaiseLayout();
            }
        }
    }

    public bool IsDownloading
    {
        get => isDownloading;
        private set
        {
            if (Set(ref isDownloading, value))
            {
                RaiseLayout();
            }
        }
    }

    public double Percent
    {
        get => percent;
        private set => Set(ref percent, value);
    }

    public string StatusText
    {
        get => statusText;
        private set
        {
            if (Set(ref statusText, value))
            {
                OnPropertyChanged(nameof(HasStatus));
            }
        }
    }

    public bool UpdateAvailable
    {
        get => updateAvailable;
        private set
        {
            if (Set(ref updateAvailable, value))
            {
                RaiseLayout();
            }
        }
    }

    public bool HasStatus => StatusText.Length > 0;
    public string DownloadText => $"Download Maps Pack ({sizeText})";
    public string ProgressText => $"Downloading… {Percent:0}%";
    public bool ShowDownload => !IsDownloading && State == MapsPackState.NotInstalled;
    public bool ShowInstalled => !IsDownloading && State == MapsPackState.Installed && !UpdateAvailable;
    public bool ShowUpdate => !IsDownloading && State == MapsPackState.Installed && UpdateAvailable;
    public bool ShowDamaged => !IsDownloading && State == MapsPackState.Damaged;
    public bool ShowRemove => !IsDownloading && State != MapsPackState.NotInstalled;

    /// <summary>The tooltip for the disabled buttons; null while the game is closed.</summary>
    public string? DisabledReason => GameRunning() ? GameRunningText : null;

    private void RaiseLayout()
    {
        OnPropertyChanged(nameof(ShowDownload));
        OnPropertyChanged(nameof(ShowInstalled));
        OnPropertyChanged(nameof(ShowUpdate));
        OnPropertyChanged(nameof(ShowDamaged));
        OnPropertyChanged(nameof(ShowRemove));
    }

    private bool GameRunning()
    {
        var now = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(runningCheckedAt, now) >= RunningCacheLifetime)
        {
            var running = gameRunning();
            runningCheckedAt = now;
            if (running != runningCached)
            {
                runningCached = running;
                OnPropertyChanged(nameof(DisabledReason));
            }
        }
        return runningCached;
    }

    private bool CanChange() => !IsDownloading && !owner.IsBusy && !GameRunning();

    private void RefreshState() => State = MapsPackRecord.CheckQuick(paths, out _);

    private async Task CheckForUpdateAsync()
    {
        try
        {
            using var downloader = new ReleaseDownloader(ReleaseDownloader.ContentDeadline);
            var installer = new MapsPackInstaller(paths, downloader);
            if (State == MapsPackState.Installed)
            {
                UpdateAvailable = await installer.UpdateAvailableAsync(CancellationToken.None);
            }
            else if (State == MapsPackState.NotInstalled)
            {
                var release = await installer.FetchAsync(CancellationToken.None);
                sizeText = FormatSize(release.Archives.Sum(archive => archive.Bytes));
                OnPropertyChanged(nameof(DownloadText));
            }
        }
        catch (ContentException)
        {
            // Offline is normal; keep the fallback size and no update notice.
        }
    }

    public static string FormatSize(long bytes) =>
        $"{Math.Round(bytes / 1048576.0, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.CurrentCulture)} MB";

    private Task DownloadAsync() => owner.RunBusyAsync(async _ =>
    {
        StatusText = "";
        Percent = 0;
        OnPropertyChanged(nameof(ProgressText));
        using var source = new CancellationTokenSource();
        cancel = source;
        IsDownloading = true;
        try
        {
            using var downloader = new ReleaseDownloader(ReleaseDownloader.ContentDeadline);
            var installer = new MapsPackInstaller(paths, downloader);
            // Created here, on the UI thread, so reports reach the bindings on the UI thread.
            var progress = new Progress<MapsPackProgress>(report =>
            {
                Percent = report.TotalBytes > 0 ? Math.Min(100, report.Bytes * 100.0 / report.TotalBytes) : 0;
                OnPropertyChanged(nameof(ProgressText));
            });
            await installer.InstallAsync(progress, source.Token);
            UpdateAvailable = false;
        }
        catch (OperationCanceledException)
        {
            // Cancelled by the user: show nothing and return to the previous state.
        }
        catch (ContentException error)
        {
            StatusText = error.Message;
        }
        finally
        {
            cancel = null;
            IsDownloading = false;
            RefreshState();
        }
    });

    private Task RemoveAsync() => owner.RunBusyAsync(_ =>
    {
        StatusText = "";
        try
        {
            using var downloader = new ReleaseDownloader(ReleaseDownloader.ContentDeadline);
            new MapsPackInstaller(paths, downloader).Remove();
            UpdateAvailable = false;
        }
        catch (ContentException error)
        {
            StatusText = error.Message;
        }
        RefreshState();
        return Task.CompletedTask;
    });
}
