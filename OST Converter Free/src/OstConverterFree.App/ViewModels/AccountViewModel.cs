using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OstConverterFree.Accounts;

namespace OstConverter.App.ViewModels;

public sealed record DeviceRow(string DeviceId, string Name, bool IsCurrent)
{
    public string Label => IsCurrent ? Name + " (this PC)" : Name;
    public bool CanRemove => !IsCurrent;
}

/// <summary>Create account / sign in with an emailed code, and the list of PCs signed in to the account.</summary>
public sealed partial class AccountViewModel : ObservableObject
{
    readonly AccountManager _account;
    string? _pendingEmail, _pendingCode, _pendingName;

    public AccountViewModel(AccountManager account)
    {
        _account = account;
        _account.Changed += OnAccountChanged;
        Sync();
        if (IsSignedIn) _ = LoadDevicesAsync();
    }

    public ObservableCollection<DeviceRow> Devices { get; } = [];
    public ObservableCollection<DeviceRow> LimitDevices { get; } = [];

    [ObservableProperty] string _name = "";
    [ObservableProperty] string _email = "";
    [ObservableProperty] string _code = "";
    [ObservableProperty] bool _codeSent;
    [ObservableProperty] bool _isBusy;
    [ObservableProperty] string _message = "";
    [ObservableProperty] bool _messageIsError;
    [ObservableProperty] bool _isSignedIn;
    [ObservableProperty] string _accountEmail = "";

    public bool IsSignedOut => !IsSignedIn;
    public bool HasMessage => Message.Length > 0;
    public bool NeedsDeviceChoice => LimitDevices.Count > 0;
    public string StatusText => IsSignedIn
        ? "You can convert without limits."
        : "You can open files and preview them without an account. Create a free account to convert.";
    public bool CanSendCode => !IsBusy && Email.Contains('@') && Name.Trim().Length >= 2;
    public bool CanSignIn => !IsBusy && CodeSent && Code.Trim().Length >= 4;

    partial void OnIsSignedInChanged(bool value) { OnPropertyChanged(nameof(IsSignedOut)); OnPropertyChanged(nameof(StatusText)); }
    partial void OnMessageChanged(string value) => OnPropertyChanged(nameof(HasMessage));
    partial void OnEmailChanged(string value) => OnPropertyChanged(nameof(CanSendCode));
    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(CanSendCode));
    partial void OnCodeChanged(string value) => OnPropertyChanged(nameof(CanSignIn));
    partial void OnCodeSentChanged(bool value) => OnPropertyChanged(nameof(CanSignIn));
    partial void OnIsBusyChanged(bool value) { OnPropertyChanged(nameof(CanSendCode)); OnPropertyChanged(nameof(CanSignIn)); }

    void OnAccountChanged(object? sender, EventArgs e)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) Sync(); else d.BeginInvoke(Sync);
    }

    void Sync()
    {
        IsSignedIn = _account.IsSignedIn;
        AccountEmail = _account.AccountEmail ?? "";
    }

    void Info(string text) { Message = text; MessageIsError = false; }
    void Error(string text) { Message = text; MessageIsError = true; }

    async Task Busy(Func<Task> work)
    {
        IsBusy = true;
        try { await work(); }
        catch (System.Net.Http.HttpRequestException e) when (e.StatusCode is { } status && (int)status >= 500)
        {
            // The server answered, so the connection is fine: the server has a problem (for example it cannot send the email).
            Error($"The account server had a problem ({(int)status}). Please try again later or contact support.");
        }
        catch (Exception e) when (AccountManager.IsNetworkError(e))
        {
            Error("Can't reach the account server. Check your internet connection and try again.");
        }
        catch (LicenseApiException e)
        {
            Error(e.Status switch
            {
                System.Net.HttpStatusCode.TooManyRequests => "Too many attempts. Please wait a minute and try again.",
                System.Net.HttpStatusCode.BadRequest => "That email address doesn't look right.",
                _ => "The account server rejected the request.",
            });
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    Task SendCode() => Busy(async () =>
    {
        await _account.RequestCodeAsync(Email);
        CodeSent = true;
        Code = "";
        Info($"We sent a 6-digit code to {Email.Trim()}. It is valid for 10 minutes. If this is your first time, signing in creates your account.");
    });

    [RelayCommand]
    Task SignIn() => SignInCore(null);

    async Task SignInCore(string? replaceDeviceId) => await Busy(async () =>
    {
        _pendingEmail = Email.Trim();
        _pendingCode = Code.Trim();
        _pendingName = Name.Trim();
        var result = await _account.SignInAsync(_pendingEmail, _pendingCode, replaceDeviceId, _pendingName);
        switch (result.Outcome)
        {
            case SignInOutcome.Success:
                LimitDevices.Clear();
                OnPropertyChanged(nameof(NeedsDeviceChoice));
                CodeSent = false; Code = "";
                Info("You're signed in. Converting is now unlimited.");
                await LoadDevicesAsync();
                break;
            case SignInOutcome.DeviceLimit:
                LimitDevices.Clear();
                foreach (var d in result.Devices ?? []) LimitDevices.Add(new DeviceRow(d.DeviceId, d.Name, false));
                OnPropertyChanged(nameof(NeedsDeviceChoice));
                Error("This account is already signed in on the maximum number of PCs. Choose one to sign out so this PC can take its place.");
                break;
            case SignInOutcome.Blocked: Error("This account has been suspended. Contact support at Info@webcents.in if you think this is a mistake."); break;
            case SignInOutcome.InvalidCode: Error("That code isn't right or has expired. Request a new one."); break;
            case SignInOutcome.Offline: Error("Can't reach the account server. Check your internet connection and try again."); break;
            case SignInOutcome.RejectedToken: Error("The server's response could not be verified. Make sure you have the latest version of this app."); break;
        }
    });

    [RelayCommand]
    async Task ReplaceDevice(DeviceRow? device)
    {
        if (device is null) return;
        Email = _pendingEmail ?? Email;
        Code = _pendingCode ?? Code;
        Name = _pendingName ?? Name;
        await SignInCore(device.DeviceId);
    }

    [RelayCommand]
    Task SignOut() => Busy(async () =>
    {
        await _account.SignOutAsync();
        Devices.Clear();
        CodeSent = false;
        Info("Signed out. You can still preview files, but converting needs an account.");
    });

    /// <summary>Call when the window closes so it stops listening for account changes.</summary>
    public void Close() => _account.Changed -= OnAccountChanged;

    [RelayCommand]
    Task RemoveDevice(DeviceRow? device) => device is null ? Task.CompletedTask : Busy(async () =>
    {
        await _account.RemoveDeviceAsync(device.DeviceId);
        await LoadDevicesAsync();
        Info($"{device.Name} was signed out.");
    });

    async Task LoadDevicesAsync()
    {
        try
        {
            var list = await _account.ListDevicesAsync();
            Devices.Clear();
            foreach (var d in list) Devices.Add(new DeviceRow(d.DeviceId, d.Name, d.Current));
        }
        catch (Exception e) when (AccountManager.IsNetworkError(e) || e is LicenseApiException or InvalidOperationException)
        {
            // The device list is a convenience; the rest of the window still works without it.
        }
    }
}
