using System.Diagnostics;
using System.Globalization;
using System.Windows.Input;
using VegasReborn.Core.Account;
using VegasReborn.Core.Profiles;
using VegasReborn.Core.Titles;

namespace VegasReborn.App.ViewModels;

public sealed class AccountViewModel : ObservableObject
{
    private readonly MainViewModel owner;
    private readonly CompanionAccount account;
    private CancellationTokenSource? signIn;
    private string status = "";
    private string nameInput = "";
    private string guestNameInput = "";
    private GameTitle? conflictTitle;

    public AccountViewModel(MainViewModel owner, CompanionAccount account)
    {
        this.owner = owner;
        this.account = account;
        account.Changed += (_, _) => App.Current.Dispatcher.BeginInvoke(Refresh);
        SignInCommand = new RelayCommand(SignInAsync, () => !owner.IsBusy && !IsSignedIn);
        PlayAsGuestCommand = new RelayCommand(PlayAsGuestAsync, () => !owner.IsBusy && !IsSignedIn && !IsSigningIn);
        CancelSignInCommand = new RelayCommand(() => { signIn?.Cancel(); return Task.CompletedTask; }, () => IsSigningIn);
        SignOutCommand = new RelayCommand(() => { account.SignOut(); Status = ""; return Task.CompletedTask; }, () => !owner.IsBusy && IsSignedIn);
        ClaimNameCommand = new RelayCommand(ClaimNameAsync, () => !owner.IsBusy && CanClaimName && !string.IsNullOrWhiteSpace(NameInput));
        SyncCommand = new RelayCommand(SyncAsync, () => !owner.IsBusy && ShowSync && SyncAvailable);
        KeepCloudCommand = new RelayCommand(() => ResolveAsync(ConflictChoice.KeepCloud), () => !owner.IsBusy && HasConflict);
        KeepLocalCommand = new RelayCommand(() => ResolveAsync(ConflictChoice.KeepLocal), () => !owner.IsBusy && HasConflict);
        Refresh();
    }

    public ICommand SignInCommand { get; }
    public ICommand PlayAsGuestCommand { get; }
    public ICommand CancelSignInCommand { get; }
    public ICommand SignOutCommand { get; }
    public ICommand ClaimNameCommand { get; }
    public ICommand SyncCommand { get; }
    public ICommand KeepCloudCommand { get; }
    public ICommand KeepLocalCommand { get; }

    public bool IsSignedIn => account.IsSignedIn;
    public bool IsSigningIn => signIn is not null;
    public string PlayerName => account.Session?.PlayerName ?? "";
    public bool IsGuest => account.IsGuest;
    public string DiscordName => account.Session is { } session
        ? session.IsGuest ? "Guest. " + AccountMessages.GuestSaves : "Discord: " + session.DisplayName
        : "";
    public bool ShowSync => IsSignedIn && !IsGuest;
    public bool NeedsNameClaim => account.Session?.NeedsNameClaim == true;
    public bool ShowNameSection => account.Session?.NameClaimsEnabled == true;
    public bool CanClaimName => ShowNameSection && account.Session?.NextNameChange(DateTimeOffset.UtcNow) is null;
    public string NameHelp => NeedsNameClaim
        ? AccountMessages.ChooseName
        : account.Session?.NextNameChange(DateTimeOffset.UtcNow) is { } next
            ? AccountMessages.NextNameChange(next, TimeZoneInfo.Local, CultureInfo.CurrentCulture)
            : "Shown in game and in sessions. Names can change every 30 days.";

    public string SyncTitle => owner.SelectedGame?.Name ?? "";
    public bool SyncAvailable => owner.SelectedGame?.Title.SaveSlots.Count > 0;
    public string SyncHelp => SyncAvailable
        ? $"Your {SyncTitle} character is saved to your account after each game. Sync now to check it before playing on another PC."
        : SyncMessages.NotAvailable;

    public bool HasConflict => conflictTitle is not null;
    public string ConflictText => conflictTitle is null ? "" :
        $"Your {conflictTitle.DisplayName} character on this PC and the one saved to your account have both changed. " +
        "Choose which to keep. The other is backed up, and your higher XP is kept either way.";

    public string Status
    {
        get => status;
        private set => Set(ref status, value);
    }

    public string GuestNameInput
    {
        get => guestNameInput;
        set => Set(ref guestNameInput, value);
    }

    public string NameInput
    {
        get => nameInput;
        set
        {
            if (Set(ref nameInput, value))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public void Refresh()
    {
        foreach (var name in new[]
        {
            nameof(IsSignedIn), nameof(IsSigningIn), nameof(IsGuest), nameof(ShowSync), nameof(PlayerName), nameof(DiscordName), nameof(NeedsNameClaim),
            nameof(ShowNameSection), nameof(CanClaimName), nameof(NameHelp), nameof(SyncTitle), nameof(SyncAvailable), nameof(SyncHelp),
            nameof(HasConflict), nameof(ConflictText),
        })
        {
            OnPropertyChanged(name);
        }
        CommandManager.InvalidateRequerySuggested();
    }

    public void SelectedGameChanged() => Refresh();

    public void ShowStatus(string text) => Status = text;

    public void ShowConflict(GameTitle title)
    {
        conflictTitle = title;
        Refresh();
    }

    /// <summary>Re-reads the saved account once at startup so claim state is current.</summary>
    public async Task RefreshFromServiceAsync()
    {
        if (!IsSignedIn)
        {
            return;
        }
        try
        {
            var session = await account.RefreshAsync(CancellationToken.None);
            Status = AccountMessages.Ready(session);
        }
        catch (ServiceException error)
        {
            Status = error.Message;
        }
        catch (Exception)
        {
            Status = "The account service could not be reached. You can retry from Account.";
        }
    }

    private async Task SignInAsync()
    {
        signIn = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        Refresh();
        try
        {
            await owner.RunBusyAsync(async progress =>
            {
                var session = await account.SignInAsync(OpenBrowser, new Progress<string>(text => Status = text), signIn.Token);
                Status = AccountMessages.SignedInAs(session) + ". " + AccountMessages.Ready(session);
                owner.ShowMessage("");
            });
        }
        catch (ServiceException error)
        {
            Status = error.Message;
        }
        catch (OperationCanceledException)
        {
            Status = "Sign-in cancelled.";
        }
        catch (Exception)
        {
            Status = AccountException.GenericMessage;
        }
        finally
        {
            signIn.Dispose();
            signIn = null;
            Refresh();
        }
    }

    private async Task PlayAsGuestAsync()
    {
        try
        {
            await owner.RunBusyAsync(async _ =>
            {
                Status = "Starting guest play...";
                var session = await account.PlayAsGuestAsync(GuestNameInput, CancellationToken.None);
                Status = AccountMessages.SignedInAs(session) + ". " + AccountMessages.ReadyToPlay;
                owner.ShowMessage("");
            });
        }
        catch (ServiceException error)
        {
            Status = error.Message;
        }
        catch (Exception)
        {
            Status = AccountException.MessageFor("guest_sign_in_failed");
        }
        Refresh();
    }

    private async Task ClaimNameAsync()
    {
        try
        {
            await owner.RunBusyAsync(async _ =>
            {
                await account.ClaimNameAsync(NameInput, CancellationToken.None);
                Status = AccountMessages.NameSaved + AccountMessages.ReadyToPlay;
                NameInput = "";
            });
        }
        catch (ServiceException error)
        {
            Status = error.Message;
        }
        catch (Exception)
        {
            Status = AccountException.GenericMessage;
        }
    }

    private async Task SyncAsync()
    {
        var title = owner.SelectedGame.Title;
        try
        {
            await owner.RunBusyAsync(async _ =>
            {
                Status = SyncMessages.CheckingBeforeLaunch;
                await account.SynchronizeAsync(title, new Progress<string>(text => Status = text), CancellationToken.None);
            });
        }
        catch (SyncConflictException error)
        {
            Status = error.Message;
            ShowConflict(title);
        }
        catch (ServiceException error)
        {
            Status = error.Message;
        }
        catch (Exception)
        {
            Status = ProfileSyncException.GenericMessage;
        }
    }

    private async Task ResolveAsync(ConflictChoice choice)
    {
        var title = conflictTitle;
        if (title is null)
        {
            return;
        }
        try
        {
            await owner.RunBusyAsync(async _ =>
            {
                await account.ResolveConflictAsync(title, choice, new Progress<string>(text => Status = text), CancellationToken.None);
                conflictTitle = null;
                owner.ShowMessage("");
            });
        }
        catch (ServiceException error)
        {
            Status = error.Message;
        }
        catch (Exception)
        {
            Status = ProfileSyncException.GenericMessage;
        }
        Refresh();
    }

    private static void OpenBrowser(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
}
