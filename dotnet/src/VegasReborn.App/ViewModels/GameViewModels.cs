using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using VegasReborn.Core.Account;
using VegasReborn.Core.Display;
using VegasReborn.Core.Profiles;
using VegasReborn.Core.Launch;
using VegasReborn.Core.Profiles.Vegas1;
using VegasReborn.Core.Profiles.Vegas2;
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

public sealed class FixSectionViewModel(string heading, string lead, IEnumerable<FixViewModel> fixes, GameViewModel? display = null)
{
    /// <summary>The game whose display mode and monitor rows lead this section; null for none.</summary>
    public GameViewModel? Display { get; } = display;
    public bool ShowsDisplay => Display is not null;
    public string Heading { get; } = heading;
    public string Lead { get; } = lead;
    public IReadOnlyList<FixViewModel> Fixes { get; } = fixes.ToList();
}

/// <summary>A monitor choice for borderless and windowed play. An empty device means the primary monitor.</summary>
public sealed record MonitorChoice(string Device, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A field of view choice. Zero degrees is the game's default.</summary>
public sealed record FieldOfViewChoice(int Degrees, string Label)
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
    private RetailRank? rank;
    private ImageSource? rankIcon;
    private static readonly Dictionary<string, ImageSource?> RankIcons = new(StringComparer.OrdinalIgnoreCase);

    public GameViewModel(GameTitle title, MainViewModel owner, IFixSettings settings)
    {
        Title = title;
        this.owner = owner;
        preferences = owner.Preferences;
        // Skip intros is shared by every game (preferences.json skip_intros) and is applied
        // to the copy's MovieLoading.ini for both titles, so it is listed with the game fixes.
        skipIntrosRow = new FixViewModel("Skip intro movies",
            "Go straight to the menu. Loading screens and gameplay movies still play.",
            "Turns off the [INTRO] movie in MovieLoading.ini in the launcher's game copy. Off restores the original setting. This setting is shared by every game.",
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
                Sections.Add(new FixSectionViewModel(heading, lead, fixes, section == FixSection.GameFixes ? this : null));
            }
        }
        if (title.Key == TitleCatalog.Vegas1.Key)
        {
            MapsPack = new MapsPackViewModel(owner, owner.Paths);
        }
        LaunchCommand = new RelayCommand(() => owner.PlayAsync(this), () => !owner.IsBusy);
        BrowseCommand = new RelayCommand(BrowseAsync, () => !owner.IsBusy && !owner.IsSearching);
        SearchCommand = new RelayCommand(() => owner.FindInstallAsync(this), () => !owner.IsBusy && !owner.IsSearching);
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
    public ObservableCollection<FixSectionViewModel> Sections { get; } = [];
    public IReadOnlyList<string> DisplayModeNames { get; } = DisplayModes.All.Select(DisplayModes.Name).ToList();
    /// <summary>The Extra Maps section; null for titles without one.</summary>
    public MapsPackViewModel? MapsPack { get; }
    public ICommand LaunchCommand { get; }
    public ICommand BrowseCommand { get; }
    public ICommand SearchCommand { get; }

    public string Status
    {
        get => status;
        private set => Set(ref status, value);
    }

    /// <summary>The signed-in player's saved rank for this title, or null when none is known.</summary>
    public RetailRank? Rank
    {
        get => rank;
        private set
        {
            if (!Equals(rank, value))
            {
                rank = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasRank));
                OnPropertyChanged(nameof(RankDetails));
                OnPropertyChanged(nameof(RankAccessibleName));
            }
        }
    }

    public bool HasRank => Rank is not null;

    /// <summary>The game's insignia for <see cref="Rank"/>, or null when the game folder does not have it.</summary>
    public ImageSource? RankIcon
    {
        get => rankIcon;
        private set
        {
            if (Set(ref rankIcon, value))
            {
                OnPropertyChanged(nameof(HasRankIcon));
            }
        }
    }

    public bool HasRankIcon => RankIcon is not null;
    public string RankDetails => Rank?.DisplayText ?? "";
    public string RankAccessibleName => Rank is { } r
        ? $"Rank: {r.Title}, {r.Subrank} of {r.PipCount} pips, {Math.Round(r.BarProgress * 100)} percent to the next step"
        : "";

    /// <summary>
    /// Re-reads the saved character's rank from this title's account save folder. Guests and
    /// titles without a decoded save show no rank.
    /// </summary>
    public void RefreshRank(CompanionAccount account)
    {
        RetailRank? read = null;
        if (account.Session is { IsGuest: false } session)
        {
            try
            {
                read = Title.Key == TitleCatalog.Vegas1.Key
                    ? RankTable.TryReadRank(account.SyncFor(Title).ProgressionFile(session), GameFolder)
                    : Title.Key == TitleCatalog.Vegas2.Key
                        ? RankTable.TryReadRank(Vegas2SaveFile(session), GameFolder, RankTable.Vegas2, data => PecSave.ReadXp(data))
                        : null;
            }
            catch (Exception error) when (error is ProfileSyncException or ArgumentException or IOException)
            {
                read = null;
            }
        }
        Rank = read;
        RankIcon = read is null ? null : LoadRankIcon(GameFolder, read.LabelKey, RankSourceFor(Title));
    }

    private string? Vegas2SaveFile(AccountSession session) =>
        session.DiscordId is { Length: > 0 } id && id.All(char.IsAsciiDigit)
            ? Path.Combine(LaunchLayout.ProfileDirectory(LaunchLayout.DefaultAccountDirectory(owner.Paths, Title, id), Title), PecSave.FileName)
            : null;

    private static RankSource RankSourceFor(GameTitle title) =>
        title.Key == TitleCatalog.Vegas2.Key ? RankTable.Vegas2 : RankTable.Vegas1;

    private static ImageSource? LoadRankIcon(string gameFolder, string labelKey, RankSource source)
    {
        var path = RankInsignia.PathFor(gameFolder, labelKey, source);
        if (RankIcons.TryGetValue(path, out var cached))
        {
            return cached;
        }
        ImageSource? icon = null;
        if (RankInsignia.TryLoad(gameFolder, labelKey, source) is { } image)
        {
            var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, image.Bgra, image.Width * 4);
            bitmap.Freeze();
            icon = bitmap;
        }
        if (icon is not null)
        {
            RankIcons[path] = icon;
        }
        return icon;
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
                RefreshRank(owner.CompanionAccount);
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

    public IReadOnlyList<FieldOfViewChoice> FieldOfViewChoices { get; } =
    [
        new(0, "Game default (60°)"),
        .. FieldOfView.Choices.Select(degrees => new FieldOfViewChoice(degrees, $"{degrees}°")),
    ];

    // The field of view is kept per game (preferences.json titles.<key>.field_of_view).
    public FieldOfViewChoice SelectedFieldOfView
    {
        get => FieldOfViewChoices.FirstOrDefault(choice => choice.Degrees == preferences.FieldOfViewFor(Title)) ?? FieldOfViewChoices[0];
        set
        {
            if (value is not null && value.Degrees != preferences.FieldOfViewFor(Title))
            {
                preferences.SetFieldOfView(Title, value.Degrees);
                owner.SavePreferences();
                OnPropertyChanged();
            }
        }
    }

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

    private async Task BrowseAsync()
    {
        var dialog = new OpenFolderDialog
        {
            Title = $"Choose the {Name} folder",
            InitialDirectory = Directory.Exists(GameFolder) ? GameFolder : "",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        var chosen = dialog.FolderName;
        if (GameInstallLocator.ContainsClient(chosen, Title))
        {
            GameFolder = chosen;
            owner.ShowMessage("");
            return;
        }
        // A parent folder such as a game library: look for the game inside it.
        if (await owner.FindInstallInAsync(this, chosen) is { } found)
        {
            GameFolder = found.Folder;
            owner.ShowMessage(MainViewModel.InstallMessage(this, found));
            return;
        }
        owner.ShowMessage($"{Name} was not found in that folder. Choose the folder that contains {Title.ClientExecutable.Replace('/', '\\')}.");
    }

    // Accessible name for the game list item.
    public override string ToString() => $"{Name}, {Status}";
}
