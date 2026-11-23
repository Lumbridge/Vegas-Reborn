using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using Microsoft.Win32;
using VegasReborn.Core.Display;
using VegasReborn.Core.Settings;
using VegasReborn.Core.Titles;

namespace VegasReborn.App.ViewModels;

/// <summary>Where fix switches are read and saved.</summary>
public interface IFixSettings
{
    bool IsEnabled(GameTitle title, GameFix fix);
    void SetEnabled(GameTitle title, GameFix fix, bool enabled);
}

/// <summary>Fix switches stored in preferences.json.</summary>
public sealed class PreferencesFixSettings(Preferences preferences, Action save) : IFixSettings
{
    public bool IsEnabled(GameTitle title, GameFix fix) => preferences.IsFixEnabled(title, fix.Id);

    public void SetEnabled(GameTitle title, GameFix fix, bool enabled)
    {
        if (fix.Kind == FixKind.Toggle)
        {
            preferences.SetFixEnabled(title, fix.Id, enabled);
            save();
        }
    }
}

/// <summary>A row in a fix section: a catalog fix, or a launch setting shown with the fixes.</summary>
public sealed class FixViewModel : ObservableObject
{
    private readonly Func<bool> read;
    private readonly Action<bool> write;

    public FixViewModel(GameTitle title, GameFix fix, IFixSettings settings)
        : this(fix.Title, fix.Summary, fix.Details, fix.Kind == FixKind.Toggle,
               () => settings.IsEnabled(title, fix), value => settings.SetEnabled(title, fix, value))
    {
    }

    public FixViewModel(string title, string summary, string details, bool isToggle, Func<bool> read, Action<bool> write)
    {
        Title = title;
        Summary = summary;
        Details = details;
        IsToggle = isToggle;
        this.read = read;
        this.write = write;
    }

    public string Title { get; }
    public string Summary { get; }
    public string Details { get; }
    public bool IsToggle { get; }

    public bool IsEnabled
    {
        get => read();
        set
        {
            if (value != IsEnabled)
            {
                write(value);
                OnPropertyChanged();
            }
        }
    }

    public void Refresh() => OnPropertyChanged(nameof(IsEnabled));
}

public sealed class FixSectionViewModel(string heading, string lead, IEnumerable<FixViewModel> fixes)
{
    public string Heading { get; } = heading;
    public string Lead { get; } = lead;
    public IReadOnlyList<FixViewModel> Fixes { get; } = fixes.ToList();
}

/// <summary>A monitor choice for borderless and windowed play. An empty device means the primary monitor.</summary>
public sealed record MonitorChoice(string Device, string Label)
{
    public override string ToString() => Label;
}

public sealed class GameViewModel : ObservableObject
{
    private readonly MainViewModel owner;
    private readonly Preferences preferences;
    private readonly FixViewModel skipIntrosRow;
    private string status = "";
    private bool isPlaying;

    public GameViewModel(GameTitle title, MainViewModel owner, IFixSettings settings)
    {
        Title = title;
        this.owner = owner;
        preferences = owner.Preferences;
        // Skip intros is shared by every game (preferences.json skip_intros) and is applied
        // to the copy's MovieLoading.ini for both titles, so it is listed with the game fixes.
        skipIntrosRow = new FixViewModel("Skip intro movies",
            "Go straight to the menu. Loading screens and gameplay movies still play.",
            "Turns off the [INTRO] movie in the companion copy's MovieLoading.ini. Off restores the original setting. This setting is shared by every game.",
            isToggle: true, () => preferences.SkipIntros, value => SkipIntros = value);
        foreach (var (section, heading, lead) in SectionText)
        {
            var fixes = title.FixesIn(section).Select(fix => new FixViewModel(title, fix, settings)).ToList();
            if (section == FixSection.GameFixes)
            {
                fixes.Insert(0, skipIntrosRow);
            }
            if (fixes.Count > 0)
            {
                Sections.Add(new FixSectionViewModel(heading, lead, fixes));
            }
        }
        LaunchCommand = new RelayCommand(() => owner.PlayAsync(this), () => !owner.IsBusy);
        BrowseCommand = new RelayCommand(BrowseAsync, () => !owner.IsBusy);
        RefreshStatus();
    }

    private static readonly (FixSection Section, string Heading, string Lead)[] SectionText =
    [
        (FixSection.GameFixes, "Game fixes",
            "Fixes for the original game on a current PC. Changes apply the next time you launch."),
        (FixSection.KeptFromCrashing, "Kept from crashing",
            "Where the original game fails or hangs on a current PC."),
        (FixSection.OnlineServices, "Online services",
            "What the game needs to use the Vegas Reborn service in place of the closed Ubisoft service. Always on."),
    ];

    public GameTitle Title { get; }
    public string Name => Title.DisplayName;
    public bool IsExperimental => Title.Support == TitleSupport.Experimental;
    public bool SupportsLaunchOptions => Title.SupportsLaunchOptions;
    public ObservableCollection<FixSectionViewModel> Sections { get; } = [];
    public IReadOnlyList<string> DisplayModeNames { get; } = DisplayModes.All.Select(DisplayModes.Name).ToList();
    public ICommand LaunchCommand { get; }
    public ICommand BrowseCommand { get; }

    public string Status
    {
        get => status;
        private set => Set(ref status, value);
    }

    public bool IsPlaying
    {
        get => isPlaying;
        set
        {
            if (Set(ref isPlaying, value))
            {
                RefreshStatus();
            }
        }
    }

    public bool FolderFound => File.Exists(Path.Combine(GameFolder, Title.ClientExecutable));

    public string GameFolder
    {
        get => preferences.GameFolderFor(Title);
        set
        {
            if (value != GameFolder)
            {
                preferences.SetGameFolder(Title, value);
                owner.SavePreferences();
                OnPropertyChanged();
                OnPropertyChanged(nameof(FolderFound));
                RefreshStatus();
            }
        }
    }

    // Display mode, monitor and intros are shared by every game, as in preferences.json.
    public string SelectedDisplayMode
    {
        get => DisplayModes.Name(preferences.DisplayMode);
        set
        {
            if (DisplayModes.TryParse(value, out var mode) && mode != preferences.DisplayMode)
            {
                preferences.DisplayMode = mode;
                owner.SavePreferences();
                owner.DisplayOptionsChanged();
            }
        }
    }

    public bool ShowsMonitor => preferences.DisplayMode is DisplayMode.Borderless or DisplayMode.Windowed;

    public IReadOnlyList<MonitorChoice> Monitors => owner.Monitors;

    public MonitorChoice? SelectedMonitor
    {
        get => Monitors.FirstOrDefault(choice => choice.Device == preferences.MonitorDevice) ?? Monitors.FirstOrDefault();
        set
        {
            if (value is not null && value.Device != preferences.MonitorDevice)
            {
                preferences.MonitorDevice = value.Device;
                owner.SavePreferences();
                owner.DisplayOptionsChanged();
            }
        }
    }

    public bool SkipIntros
    {
        get => preferences.SkipIntros;
        set
        {
            if (value != preferences.SkipIntros)
            {
                preferences.SkipIntros = value;
                owner.SavePreferences();
                owner.DisplayOptionsChanged();
            }
        }
    }

    public void NotifyDisplayOptions()
    {
        OnPropertyChanged(nameof(SelectedDisplayMode));
        OnPropertyChanged(nameof(ShowsMonitor));
        OnPropertyChanged(nameof(SelectedMonitor));
        OnPropertyChanged(nameof(Monitors));
        OnPropertyChanged(nameof(SkipIntros));
        skipIntrosRow.Refresh();
    }

    public void RefreshStatus()
    {
        Status = IsPlaying ? "Playing"
            : !FolderFound ? "Game folder not found"
            : IsExperimental ? "Online test"
            : "Ready";
    }

    private Task BrowseAsync()
    {
        var dialog = new OpenFolderDialog
        {
            Title = $"Choose the {Name} folder",
            InitialDirectory = Directory.Exists(GameFolder) ? GameFolder : "",
        };
        if (dialog.ShowDialog() == true)
        {
            var chosen = dialog.FolderName;
            if (!File.Exists(Path.Combine(chosen, Title.ClientExecutable)))
            {
                owner.ShowMessage($"That folder does not contain {Title.ClientExecutable.Replace('/', '\\')}. Choose the folder {Name} is installed in.");
                return Task.CompletedTask;
            }
            GameFolder = chosen;
            owner.ShowMessage("");
        }
        return Task.CompletedTask;
    }

    // Accessible name for the game list item.
    public override string ToString() => $"{Name}, {Status}";
}
