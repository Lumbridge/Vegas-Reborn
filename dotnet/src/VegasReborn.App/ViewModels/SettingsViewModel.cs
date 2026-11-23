using System.Windows.Input;
using VegasReborn.Core.Update;

namespace VegasReborn.App.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly MainViewModel owner;
    private readonly UpdateCoordinator updates;

    public SettingsViewModel(MainViewModel owner, UpdateCoordinator updates)
    {
        this.owner = owner;
        this.updates = updates;
        updates.Changed += (_, _) => App.Current.Dispatcher.BeginInvoke(Refresh);
        CheckCommand = new RelayCommand(() => updates.CheckAsync(), () => updates.CanCheck);
        InstallCommand = new RelayCommand(() => updates.UpdateAndRestartAsync(), () => updates.CanInstall);
        ForceCommand = new RelayCommand(() => updates.ForceUpdateAsync(), () => updates.CanForceUpdate);
    }

    public ICommand CheckCommand { get; }
    public ICommand InstallCommand { get; }
    public ICommand ForceCommand { get; }

    public string UpdateStatus => updates.Status;
    public bool HasProgress => updates.Progress is { Total: > 0 };
    public double ProgressValue => updates.Progress is { Total: > 0 } progress ? 100.0 * progress.Completed / progress.Total : 0;
    public bool UpdateAvailable => updates.AvailableRelease is not null;
    public bool UpdateBlocked => updates.UpdateBlocked;

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

    private void Refresh()
    {
        OnPropertyChanged(nameof(UpdateStatus));
        OnPropertyChanged(nameof(HasProgress));
        OnPropertyChanged(nameof(ProgressValue));
        OnPropertyChanged(nameof(UpdateAvailable));
        OnPropertyChanged(nameof(UpdateBlocked));
        CommandManager.InvalidateRequerySuggested();
    }
}
