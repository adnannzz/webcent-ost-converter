using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OstConverter.Core.Conversion;
using OstConverter.Core.Pff;
using OstConverterFree.Accounts;

namespace OstConverter.App.ViewModels;

/// <summary>Owns navigation between the three screens, the open file, and what the signed-in account allows.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    readonly AccountManager _account;
    PstStore? _store;

    public MainViewModel(AccountManager account)
    {
        _account = account;
        // Refreshes happen on background threads; the account badge must update on the UI thread.
        _account.Changed += (_, _) =>
        {
            var d = System.Windows.Application.Current?.Dispatcher;
            if (d is null || d.CheckAccess()) RefreshAccount(); else d.BeginInvoke(RefreshAccount);
        };
        Select = new SelectViewModel(this);
        _currentPage = Select;
        RefreshAccount();
    }

    public SelectViewModel Select { get; }

    [ObservableProperty] object _currentPage;
    [ObservableProperty] int _step = 1;
    /// <summary>Signed out: previewing works, converting needs an account.</summary>
    [ObservableProperty] bool _isSignedOut = true;
    [ObservableProperty] string _accountBadge = "Not signed in";

    [ObservableProperty] bool _updateReady;
    [ObservableProperty] string _updateText = "";
    Action? _applyUpdate;

    /// <summary>Called from the update check (a background thread) when a newer version has been downloaded.</summary>
    public void OfferUpdate(string version, Action apply)
    {
        void Show() { _applyUpdate = apply; UpdateText = $"Update {version} is ready. Restart to install"; UpdateReady = true; }
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) Show(); else d.BeginInvoke(Show);
    }

    [RelayCommand] void InstallUpdate() => _applyUpdate?.Invoke();

    /// <summary>The "Buy me a coffee" button only appears while the owner has set a payment link on the admin page.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasCoffeeLink))] string? _coffeeUrl;
    public bool HasCoffeeLink => !string.IsNullOrEmpty(CoffeeUrl);
    [RelayCommand] void BuyCoffee() => SupportLink.Open(CoffeeUrl);

    /// <summary>Fetches the tip link from the server in the background; an unreachable server just means no button.</summary>
    public async Task LoadCoffeeLinkAsync()
    {
        var url = await _account.GetCoffeeUrlAsync();
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) CoffeeUrl = url; else await d.InvokeAsync(() => CoffeeUrl = url);
    }

    public Entitlement Entitlement => _account.Current;
    public string? AccountEmail => _account.AccountEmail;
    public string? FilePath { get; private set; }

    void RefreshAccount()
    {
        IsSignedOut = !_account.IsSignedIn;
        AccountBadge = IsSignedOut ? "Not signed in" : _account.AccountEmail ?? "Signed in";
        OnPropertyChanged(nameof(Entitlement));
        OnPropertyChanged(nameof(AccountEmail));
        if (CurrentPage is OptionsViewModel options) options.AccountChanged();
    }

    /// <summary>Opens a PST/OST on a worker thread and moves to the options screen on success.</summary>
    public async Task OpenFileAsync(string path)
    {
        await Select.OpenAsync(path);
    }

    internal void FileOpened(string path, PstStore store)
    {
        _store?.Dispose();
        _store = store;
        FilePath = path;
        CurrentPage = new OptionsViewModel(this, store, path);
        Step = 2;
    }

    internal void StartConversion(ConversionOptions options)
    {
        // Defence in depth: the Convert button is disabled when signed out, but never convert without an account.
        if (_store is null || IsSignedOut) return;
        var vm = new ConvertViewModel(this, _store, options);
        CurrentPage = vm;
        Step = 3;
        vm.Start();
    }

    internal void BackToOptions()
    {
        if (_store is null || FilePath is null) return;
        CurrentPage = new OptionsViewModel(this, _store, FilePath);
        Step = 2;
    }

    [RelayCommand]
    public void NewFile()
    {
        _store?.Dispose();
        _store = null;
        FilePath = null;
        Select.Reset();
        CurrentPage = Select;
        Step = 1;
    }

    [RelayCommand]
    void ShowAccount()
    {
        var w = new AccountWindow(new AccountViewModel(_account)) { Owner = System.Windows.Application.Current.MainWindow };
        w.ShowDialog();
    }
}
