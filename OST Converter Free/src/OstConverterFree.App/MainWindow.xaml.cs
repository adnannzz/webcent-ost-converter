using System.ComponentModel;
using System.Reflection;
using System.Windows;
using OstConverter.App.ViewModels;

namespace OstConverter.App;

/// <summary>One entry of the three-step indicator in the header. State is "Active", "Done" or "Todo".</summary>
public sealed class StepItem : INotifyPropertyChanged
{
    string _state = "Todo";
    public StepItem(int number, string label) { Number = number; Label = label; }
    public int Number { get; }
    public string Label { get; }
    public string State
    {
        get => _state;
        set { if (_state == value) return; _state = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class MainWindow : Window
{
    readonly StepItem[] _steps = [new(1, "Select file"), new(2, "Choose what to convert"), new(3, "Convert")];

    public MainWindow()
    {
        InitializeComponent();
        Steps.ItemsSource = _steps;
        var v = Assembly.GetEntryAssembly()?.GetName().Version;
        VersionText.Text = v is null ? "" : $"Version {v.Major}.{v.Minor}.{v.Build}";
        DataContextChanged += (_, _) => Track(DataContext as MainViewModel);
    }

    void Track(MainViewModel? vm)
    {
        if (vm is null) return;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.Step)) ShowStep(vm.Step); };
        ShowStep(vm.Step);
    }

    void ShowStep(int current)
    {
        foreach (var s in _steps) s.State = s.Number == current ? "Active" : s.Number < current ? "Done" : "Todo";
    }

    static string? DroppedFile(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files
            ? files[0] : null;

    void OnDragOver(object sender, DragEventArgs e)
    {
        var f = DroppedFile(e);
        var ok = DataContext is MainViewModel { CurrentPage: SelectViewModel } && f is not null
                 && (f.EndsWith(".ost", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".pst", StringComparison.OrdinalIgnoreCase));
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    async void OnDrop(object sender, DragEventArgs e)
    {
        if (DataContext is MainViewModel { CurrentPage: SelectViewModel } vm && DroppedFile(e) is { } path)
            await vm.OpenFileAsync(path);
    }
}
