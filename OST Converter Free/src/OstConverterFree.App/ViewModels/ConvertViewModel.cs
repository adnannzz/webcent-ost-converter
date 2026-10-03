using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OstConverter.Core.Conversion;
using OstConverter.Core.Pff;

namespace OstConverter.App.ViewModels;

public sealed partial class ConvertViewModel : ObservableObject
{
    readonly MainViewModel _main;
    readonly PstStore _store;
    readonly ConversionOptions _options;
    readonly CancellationTokenSource _cts = new();

    public ConvertViewModel(MainViewModel main, PstStore store, ConversionOptions options)
    {
        _main = main; _store = store; _options = options;
    }

    public ObservableCollection<string> Problems { get; } = [];

    [ObservableProperty] double _percent;
    [ObservableProperty] string _status = "Starting…";
    [ObservableProperty] string _currentFolder = "";
    [ObservableProperty] bool _isRunning = true;
    [ObservableProperty] bool _isDone;
    [ObservableProperty] bool _wasCancelled;
    [ObservableProperty] string _headline = "Converting…";
    [ObservableProperty] string _details = "";
    [ObservableProperty] string _fatalError = "";

    public bool HasProblems => Problems.Count > 0;
    public bool HasFatalError => FatalError.Length > 0;
    /// <summary>Finished normally: drives the green check mark in the header of the page.</summary>
    public bool Succeeded => IsDone && !WasCancelled && !HasFatalError;
    partial void OnFatalErrorChanged(string value) { OnPropertyChanged(nameof(HasFatalError)); OnPropertyChanged(nameof(Succeeded)); }
    partial void OnIsDoneChanged(bool value) => OnPropertyChanged(nameof(Succeeded));
    partial void OnWasCancelledChanged(bool value) => OnPropertyChanged(nameof(Succeeded));

    public async void Start()
    {
        var progress = new Progress<ConversionProgress>(p =>
        {
            Percent = p.Total == 0 ? 100 : 100.0 * p.Done / p.Total;
            Status = $"{p.Done:N0} of {p.Total:N0} items";
            CurrentFolder = p.Folder;
        });

        try
        {
            var result = await Task.Run(() => ConversionPipeline.Run(_store, _options, progress, _cts.Token));
            Finish(result);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or PffFormatException)
        {
            IsRunning = false;
            IsDone = true;
            Headline = "Conversion stopped";
            FatalError = e is UnauthorizedAccessException or IOException
                ? $"The output folder could not be written: {e.Message}"
                : e.Message;
        }
    }

    void Finish(ConversionResult r)
    {
        IsRunning = false;
        IsDone = true;
        WasCancelled = r.Cancelled;
        Percent = r.Cancelled ? Percent : 100;
        Headline = r.Cancelled ? "Conversion cancelled" : "Conversion complete";

        var parts = new List<string> { $"{r.Exported:N0} item{(r.Exported == 1 ? "" : "s")} converted" };
        if (r.OutputFiles.Count > 0) parts.Add(r.OutputFiles.Count == 1 ? "saved as 1 PST file" : $"saved as {r.OutputFiles.Count} PST files");
        if (r.SkippedNotApplicable > 0) parts.Add($"{r.SkippedNotApplicable:N0} not applicable to this format (contacts, calendar, tasks)");
        if (r.SkippedByDate > 0) parts.Add($"{r.SkippedByDate:N0} outside the date range");
        Details = string.Join(" · ", parts) + $"  ({r.Duration.TotalSeconds:F1}s)";

        foreach (var e in r.Errors.Take(200))
            Problems.Add($"{(e.IsWarning ? "Incomplete" : "Failed")} — {e.Folder}: {e.Message}");
        if (r.Errors.Count > 200) Problems.Add($"…and {r.Errors.Count - 200:N0} more (see the log file).");
        OnPropertyChanged(nameof(HasProblems));
    }

    [RelayCommand] void Cancel() { _cts.Cancel(); Status = "Cancelling…"; }

    [RelayCommand]
    void OpenFolder()
    {
        if (Directory.Exists(_options.OutputDir))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_options.OutputDir}\"") { UseShellExecute = true });
    }

    [RelayCommand]
    void OpenLog()
    {
        var log = Path.Combine(_options.OutputDir, "conversion-log.txt");
        if (File.Exists(log)) Process.Start(new ProcessStartInfo(log) { UseShellExecute = true });
    }

    [RelayCommand] void Back() => _main.BackToOptions();
    [RelayCommand] void NewFile() => _main.NewFile();
}
