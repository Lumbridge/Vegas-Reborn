using System.Windows.Input;
using VegasReborn.Core.Update;

namespace VegasReborn.App.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly MainViewModel owner;
    private readonly UpdateCoordinator updates;
    private string? dismissedVersion;

    public SettingsViewModel(MainViewModel owner, UpdateCoordinator updates)
    {
        this.owner = owner;
        this.updates = updates;
        updates.Changed += (_, _) => App.Current.Dispatcher.BeginInvoke(Refresh);
        CheckCommand = new RelayCommand(() => updates.CheckAsync(), () => updates.CanCheck);
        InstallCommand = new RelayCommand(() => updates.UpdateAndRestartAsync(), () => updates.CanInstall);
        ForceCommand = new RelayCommand(() => updates.ForceUpdateAsync(), () => updates.CanForceUpdate);
        ShowUpdatesCommand = new RelayCommand(() =>
        {
            owner.Page = Page.Settings;
            return Task.CompletedTask;
        });
        DismissNoticeCommand = new RelayCommand(() =>
        {
            dismissedVersion = updates.AvailableRelease?.Version;
            OnPropertyChanged(nameof(ShowNotice));
            return Task.CompletedTask;
        });
    }

    public ICommand CheckCommand { get; }
    public ICommand InstallCommand { get; }
    public ICommand ForceCommand { get; }
    public ICommand ShowUpdatesCommand { get; }
    public ICommand DismissNoticeCommand { get; }

    public string UpdateStatus => updates.Status;
    public bool HasProgress => updates.Progress is { Total: > 0 };
    public double ProgressValue => updates.Progress is { Total: > 0 } progress ? 100.0 * progress.Completed / progress.Total : 0;
    public bool UpdateAvailable => updates.AvailableRelease is not null;
    public bool UpdateBlocked => updates.UpdateBlocked;

    /// <summary>
    /// The notice under the navigation bar: shown on the Games and Account pages while a release is
    /// available, until Later is chosen for that version. A blocked update is always shown.
    /// </summary>
    public bool ShowNotice => !owner.IsSettings && (UpdateBlocked
        || (updates.AvailableRelease is { } release && release.Version != dismissedVersion));

    public bool MinimizeOnPlay
    {
        get => owner.Preferences.MinimizeOnPlay;
        set
        {
            owner.Preferences.MinimizeOnPlay = value;
            owner.SavePreferences();
            OnPropertyChanged();
        }
    }

    public bool DiscordPresence
    {
        get => owner.Preferences.DiscordPresence;
        set
        {
            owner.Preferences.DiscordPresence = value;
            owner.SavePreferences();
            owner.ApplyPresence();
            OnPropertyChanged();
        }
    }

    public bool CheckUpdates
    {
        get => owner.Preferences.CheckUpdates;
        set
        {
            owner.Preferences.CheckUpdates = value;
            owner.SavePreferences();
            OnPropertyChanged();
        }
    }

    public void PageChanged() => OnPropertyChanged(nameof(ShowNotice));

    private void Refresh()
    {
        OnPropertyChanged(nameof(UpdateStatus));
        OnPropertyChanged(nameof(HasProgress));
        OnPropertyChanged(nameof(ProgressValue));
        OnPropertyChanged(nameof(UpdateAvailable));
        OnPropertyChanged(nameof(UpdateBlocked));
        OnPropertyChanged(nameof(ShowNotice));
        CommandManager.InvalidateRequerySuggested();
    }
}
