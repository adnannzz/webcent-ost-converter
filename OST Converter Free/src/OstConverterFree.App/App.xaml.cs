using System.Windows;
using System.Windows.Threading;
using OstConverter.App.ViewModels;
using OstConverter.Core.Diagnostics;
using OstConverterFree.Accounts;

namespace OstConverter.App;

public partial class App : Application
{
    DispatcherTimer? _refreshTimer;
    readonly UpdateService _updates = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        HookBackgroundErrors();
        var license = CreateAccountManager();
        var main = new MainViewModel(license);
        var window = new MainWindow { DataContext = main };
        window.Show();

        // Check the account in the background: now, and then daily. Failures (offline) leave the current state in force.
        _ = license.RefreshAsync();
        _refreshTimer = new DispatcherTimer { Interval = LicenseConfig.RefreshInterval };
        _refreshTimer.Tick += (_, _) => _ = license.RefreshAsync();
        _refreshTimer.Start();
        _ = CheckForUpdatesAsync(main);
        _ = main.LoadCoffeeLinkAsync();

#if DEBUG
        if (DevSnapshot.Requested) { _ = DevSnapshot.RunAsync(window, main, license); return; }
#endif

        // A file passed on the command line (or "Open with") goes straight to the options screen.
        if (e.Args.Length > 0 && File.Exists(e.Args[0])) _ = main.OpenFileAsync(e.Args[0]);
    }

    static AccountManager CreateAccountManager()
    {
        string? storePath = null;
#if DEBUG
        // Lets debug runs use a throwaway account file instead of the real one.
        if (Environment.GetEnvironmentVariable("OSTFREE_ACCOUNT_STORE") is { Length: > 0 } p) storePath = p;
#endif
        var manager = new AccountManager(
            new HttpLicenseApi(LicenseConfig.ApiBaseUrl),
            new DpapiLicenseStore(storePath),
            new TokenVerifier(LicenseConfig.PublicKeyPem),
            DeviceIdentity.Current());
        manager.Load();
        return manager;
    }

    void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var report = CrashLog.Write(e.Exception, "UI thread");
        var where = report is null ? "" : $"\n\nA report was saved to:\n{report}\n\nPlease attach it if you contact support.";
        MessageBox.Show("The action could not be completed: " + e.Exception.Message + where, "Something went wrong", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    /// <summary>Errors outside the UI thread (background tasks) would otherwise end the program silently.</summary>
    static void HookBackgroundErrors()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) CrashLog.Write(ex, args.IsTerminating ? "fatal" : "background");
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CrashLog.Write(args.Exception, "unobserved task");
            args.SetObserved();
        };
    }

    async Task CheckForUpdatesAsync(MainViewModel main)
    {
        var version = await _updates.CheckAndDownloadAsync();
        if (version is not null) main.OfferUpdate(version, _updates.ApplyAndRestart);
    }
}
