using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using VegasReborn.Core;
using VegasReborn.Core.Account;
using VegasReborn.Core.Display;
using VegasReborn.Core.Launch;
using VegasReborn.Core.Presence;
using VegasReborn.Core.Profiles;
using VegasReborn.Core.Settings;
using VegasReborn.Core.Titles;
using VegasReborn.Core.Update;

namespace VegasReborn.App.ViewModels;

public enum Page
{
    Games,
    Account,
    Settings,
}

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppPaths paths;
    private GameViewModel selectedGame;
    private Page page = Page.Games;
    private string message = "";
    private bool isBusy;
    private GameLauncher? launcher;
    private DiscordPresence? presence;
    private IReadOnlyList<MonitorChoice> monitors = [];

    public MainViewModel(AppPaths paths)
    {
        this.paths = paths;
        Preferences = Preferences.Load(paths);
        CompanionAccount = new CompanionAccount(paths);
        Updates = new UpdateCoordinator(new UpdateOperations(paths, new ReleaseDownloader()), Activity, IsPackaged(paths));
        RefreshMonitors();
        var fixSettings = new PreferencesFixSettings(Preferences, SavePreferences);
        foreach (var title in TitleCatalog.All)
        {
            Games.Add(new GameViewModel(title, this, fixSettings));
        }
        selectedGame = Games.FirstOrDefault(game => game.Title.Key == Preferences.SelectedTitle) ?? Games[0];
        Account = new AccountViewModel(this, CompanionAccount);
        Settings = new SettingsViewModel(this, Updates);
        ApplyPresence();
    }

    public Preferences Preferences { get; }
    public CompanionAccount CompanionAccount { get; }
    public UpdateCoordinator Updates { get; }
    public AccountViewModel Account { get; }
    public SettingsViewModel Settings { get; }
    public string Version { get; } = ReleaseInfo.CurrentVersion;
    public ObservableCollection<GameViewModel> Games { get; } = [];
    public IReadOnlyList<MonitorChoice> Monitors => monitors;

    /// <summary>Raised when the window should minimise (true) or come back (false) around play.</summary>
    public event EventHandler<bool>? MinimizeRequested;

    public GameViewModel SelectedGame
    {
        get => selectedGame;
        set
        {
            if (value is not null && Set(ref selectedGame, value))
            {
                Preferences.SelectedTitle = value.Title.Key;
                SavePreferences();
                RefreshMonitors();
                value.NotifyDisplayOptions();
                Account.SelectedGameChanged();
            }
        }
    }

    public Page Page
    {
        get => page;
        set
        {
            if (Set(ref page, value))
            {
                OnPropertyChanged(nameof(IsGames));
                OnPropertyChanged(nameof(IsAccount));
                OnPropertyChanged(nameof(IsSettings));
            }
        }
    }

    public bool IsGames { get => Page == Page.Games; set { if (value) Page = Page.Games; } }
    public bool IsAccount { get => Page == Page.Account; set { if (value) Page = Page.Account; } }
    public bool IsSettings { get => Page == Page.Settings; set { if (value) Page = Page.Settings; } }

    /// <summary>Launch and sign-in status shown under the game banner.</summary>
    public string Message
    {
        get => message;
        private set
        {
            if (Set(ref message, value))
            {
                OnPropertyChanged(nameof(HasMessage));
            }
        }
    }

    public bool HasMessage => Message.Length > 0;

    /// <summary>True while launching, playing or saving. Other launches and account changes wait.</summary>
    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (Set(ref isBusy, value))
            {
                Updates.ActivityChanged();
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsPlaying => Games.Any(game => game.IsPlaying);

    public void ShowMessage(string text) => Message = text;

    public void SavePreferences()
    {
        try
        {
            Preferences.Save(paths);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Message = "Settings could not be saved. Check that the launcher folder is writable.";
        }
    }

    public void DisplayOptionsChanged()
    {
        foreach (var game in Games)
        {
            game.NotifyDisplayOptions();
        }
    }

    public async Task PlayAsync(GameViewModel game)
    {
        if (IsBusy)
        {
            return;
        }
        if (!CompanionAccount.IsSignedIn)
        {
            Message = "Sign in with Discord before playing.";
            Account.ShowStatus(Message);
            Page = Page.Account;
            return;
        }
        if (!game.FolderFound)
        {
            Message = $"Choose the folder {game.Name} is installed in, then launch again.";
            return;
        }
        IsBusy = true;
        var progress = new Progress<string>(text => Message = text);
        var started = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        try
        {
            launcher ??= new GameLauncher(paths, ServiceConfiguration.Load(paths), CompanionAccount, CompanionAccount);
            var request = Preferences.LaunchRequestFor(game.Title, () => App.Current.Dispatcher.BeginInvoke(() =>
            {
                game.IsPlaying = true;
                OnPropertyChanged(nameof(IsPlaying));
                Message = "Playing. Close the game to save your character.";
                SetPresence(PresenceActivity.For(game.Title, playing: true, started: started));
                Updates.ActivityChanged();
                if (Preferences.MinimizeOnPlay)
                {
                    MinimizeRequested?.Invoke(this, true);
                }
            }));
            var outcome = await launcher.PlayAsync(request, progress);
            Message = outcome.CharacterSaved ? SyncMessages.SavedAfterGame
                : game.Title.SaveSlots.Count == 0 ? "Game closed."
                : SyncMessages.NoCharacterCreated;
        }
        catch (SyncConflictException error)
        {
            Message = error.Message;
            Account.ShowConflict(game.Title);
            Page = Page.Account;
        }
        catch (Exception error) when (error is LaunchException or ServiceException)
        {
            Message = error.Message;
        }
        catch (Exception)
        {
            Message = "The game could not be launched. Your character was preserved.";
        }
        finally
        {
            game.IsPlaying = false;
            OnPropertyChanged(nameof(IsPlaying));
            IsBusy = false;
            ApplyPresence();
            MinimizeRequested?.Invoke(this, false);
            Account.Refresh();
        }
    }

    /// <summary>Runs an account or sync action under the busy flag, showing its status.</summary>
    public async Task RunBusyAsync(Func<IProgress<string>, Task> action)
    {
        if (IsBusy)
        {
            return;
        }
        IsBusy = true;
        try
        {
            await action(new Progress<string>(text => Message = text));
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void ApplyPresence()
    {
        SetPresence(Preferences.DiscordPresence ? PresenceActivity.For(SelectedGame?.Title) : null);
    }

    private void SetPresence(PresenceActivity? activity)
    {
        if (!Preferences.DiscordPresence)
        {
            presence?.SetActivity(null);
            return;
        }
        presence ??= new DiscordPresence();
        presence.SetActivity(activity);
    }

    private void RefreshMonitors()
    {
        IReadOnlyList<DisplayMonitor> found;
        try
        {
            found = Desktop.AvailableMonitors();
        }
        catch (Exception)
        {
            found = [];
        }
        monitors = [new MonitorChoice("", "Primary monitor"), .. found.Select(monitor => new MonitorChoice(monitor.Device, monitor.Label))];
    }

    private CompanionActivity Activity() => new(Busy: IsBusy, Playing: IsPlaying);

    /// <summary>A signed release has version.json and its runtime folder next to the exe. Development builds do not.</summary>
    private static bool IsPackaged(AppPaths paths) =>
        File.Exists(Path.Combine(paths.Root, "version.json"))
        && Directory.Exists(Path.Combine(paths.Root, "runtime-" + ReleaseInfo.CurrentVersion));

    public void Dispose()
    {
        presence?.Dispose();
        CompanionAccount.Dispose();
    }
}
