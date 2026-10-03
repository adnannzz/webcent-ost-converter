using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using OstConverter.Core.Pff;

namespace OstConverter.App.ViewModels;

public sealed record DetectedFile(string Path, string Name, string SizeText, string Kind);

public sealed partial class SelectViewModel : ObservableObject
{
    readonly MainViewModel _main;

    public SelectViewModel(MainViewModel main)
    {
        _main = main;
        FindFiles();
    }

    public ObservableCollection<DetectedFile> Detected { get; } = [];

    [ObservableProperty] bool _isBusy;
    [ObservableProperty] string _status = "";
    [ObservableProperty] string _error = "";
    public bool HasError => Error.Length > 0;
    public bool HasDetected => Detected.Count > 0;

    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));

    public void Reset()
    {
        Error = "";
        Status = "";
        IsBusy = false;
    }

    [RelayCommand]
    async Task Browse()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select an Outlook data file",
            Filter = "Outlook data files (*.ost;*.pst)|*.ost;*.pst|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog() == true) await OpenAsync(dlg.FileName);
    }

    [RelayCommand]
    Task OpenDetected(DetectedFile? file) => file is null ? Task.CompletedTask : OpenAsync(file.Path);

    public async Task OpenAsync(string path)
    {
        if (IsBusy) return;
        Error = "";
        IsBusy = true;
        Status = "Reading " + System.IO.Path.GetFileName(path) + "…";
        try
        {
            var store = await Task.Run(() => PstStore.Open(path));
            _main.FileOpened(path, store);
        }
        catch (Exception e) when (e is PffFormatException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            Error = e.Message;
        }
        finally
        {
            IsBusy = false;
            Status = "";
        }
    }

    void FindFiles()
    {
        var places = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Outlook"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Outlook Files"),
        };
        foreach (var dir in places.Where(Directory.Exists))
        {
            try
            {
                foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*.*")
                             .Where(f => f.Extension.Equals(".ost", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".pst", StringComparison.OrdinalIgnoreCase))
                             .OrderByDescending(f => f.Length))
                    Detected.Add(new DetectedFile(f.FullName, f.Name, FormatSize(f.Length), f.Extension.TrimStart('.').ToUpperInvariant()));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* folder not readable: skip */ }
        }
    }

    internal static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F1} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F0} MB",
        _ => $"{Math.Max(1, bytes / 1024)} KB",
    };
}
