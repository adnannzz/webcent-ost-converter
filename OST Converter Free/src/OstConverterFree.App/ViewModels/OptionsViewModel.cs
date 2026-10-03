using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using OstConverter.Core.Conversion;
using OstConverter.Core.Export;
using OstConverter.Core.Pff;

namespace OstConverter.App.ViewModels;

public sealed partial class FolderNode : ObservableObject
{
    internal FolderNode(Folder folder, string rawPath, int count, FolderNode? parent)
    {
        Folder = folder; RawPath = rawPath; Count = count; Parent = parent;
    }

    public Folder Folder { get; }
    public string RawPath { get; }
    public int Count { get; }
    public FolderNode? Parent { get; }
    public ObservableCollection<FolderNode> Children { get; } = [];
    public string Label => Count > 0 ? $"{Folder.Name}  ({Count:N0})" : Folder.Name;

    [ObservableProperty] bool _isExpanded = true;
    [ObservableProperty] bool _isSelected;

    bool _isChecked;
    public event Action? CheckChanged;

    /// <summary>Checking a folder checks everything beneath it; unchecking clears it too.</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set => SetChecked(value, cascade: true);
    }

    internal void SetChecked(bool value, bool cascade)
    {
        if (_isChecked != value)
        {
            _isChecked = value;
            OnPropertyChanged(nameof(IsChecked));
            CheckChanged?.Invoke();
        }
        if (cascade) foreach (var c in Children) c.SetChecked(value, true);
    }

    public IEnumerable<FolderNode> Descendants()
    {
        foreach (var c in Children)
        {
            yield return c;
            foreach (var d in c.Descendants()) yield return d;
        }
    }

    internal int TotalCount => Count + Children.Sum(c => c.TotalCount);
}

public sealed class MessageRow(Message message, string date, string from, string subject, string kind)
{
    public Message Message { get; } = message;
    public string Date { get; } = date;
    public string From { get; } = from;
    public string Subject { get; } = subject;
    public string Kind { get; } = kind;
}

public sealed record FormatChoice(ExportFormat Format, string Label, string Hint);

/// <summary>PST output is split into several files once one reaches this size.</summary>
public sealed record SplitChoice(string Label, long Bytes);

public sealed partial class OptionsViewModel : ObservableObject
{
    const int MaxPreviewRows = 5000;

    readonly MainViewModel _main;
    readonly PstStore _store;
    CancellationTokenSource? _itemsCts;

    public OptionsViewModel(MainViewModel main, PstStore store, string path)
    {
        _main = main;
        _store = store;
        FileName = Path.GetFileName(path);
        FileInfoText = $"{(store.File.IsOst ? "OST" : "PST")} · {SelectViewModel.FormatSize(new FileInfo(path).Length)}";
        _outputFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "OST Export",
            FileNames.Safe(Path.GetFileNameWithoutExtension(path), 40));
        _selectedFormat = Formats[0];
        _selectedSplit = SplitSizes[0];
        _ = LoadTreeAsync();
    }

    public bool ShowSplit => SelectedFormat.Format == ExportFormat.Pst;
    partial void OnSelectedFormatChanged(FormatChoice value) => OnPropertyChanged(nameof(ShowSplit));

    public string FileName { get; }
    public string FileInfoText { get; }

    public IReadOnlyList<SplitChoice> SplitSizes { get; } =
    [
        new("40 GB (recommended)", 40L << 30),
        new("20 GB", 20L << 30),
        new("10 GB", 10L << 30),
        new("5 GB", 5L << 30),
        new("2 GB", 2L << 30),
    ];

    public IReadOnlyList<FormatChoice> Formats { get; } =
    [
        new(ExportFormat.Pst, "PST (Outlook data file)", "One Outlook data file with your folders, mail, contacts, calendar and tasks. Open it in Outlook with File > Open & Export > Open Outlook Data File."),
        new(ExportFormat.Eml, "EML (one file per message)", "Opens in Outlook, Thunderbird, Apple Mail and most other mail apps."),
        new(ExportFormat.Mbox, "MBOX (one file per folder)", "Standard mailbox format for Thunderbird, Apple Mail and Gmail import tools."),
        new(ExportFormat.Msg, "MSG (Outlook messages)", "One .msg file per message. Double-click to open in Outlook. Mail only."),
        new(ExportFormat.Pdf, "PDF (one per message)", "Header, attachment list and the message text, ready to print or archive. HTML styling and images are not reproduced."),
        new(ExportFormat.Html, "HTML (readable web pages)", "Each message becomes a web page with its attachments saved beside it."),
        new(ExportFormat.Csv, "CSV (spreadsheet)", "Mail index, contacts, calendar and tasks as spreadsheets."),
    ];

    public ObservableCollection<FolderNode> Roots { get; } = [];
    public ObservableCollection<MessageRow> Items { get; } = [];

    [ObservableProperty] bool _isLoadingTree = true;
    [ObservableProperty] bool _isLoadingItems;
    [ObservableProperty] string _treeError = "";
    [ObservableProperty] bool _includeSystemFolders;
    [ObservableProperty] FormatChoice _selectedFormat;
    [ObservableProperty] SplitChoice _selectedSplit = null!;
    [ObservableProperty] string _outputFolder;
    [ObservableProperty] DateTime? _dateFrom;
    [ObservableProperty] DateTime? _dateTo;
    [ObservableProperty] FolderNode? _selectedFolder;
    [ObservableProperty] MessageRow? _selectedItem;
    [ObservableProperty] string _previewHeader = "";
    [ObservableProperty] string _previewBody = "Select a message to preview it.";
    [ObservableProperty] string _itemsNote = "";
    [ObservableProperty] string _summary = "";
    [ObservableProperty] bool _canConvert;

    /// <summary>Signed out: files can be previewed but not converted.</summary>
    public bool IsSignedOut => _main.IsSignedOut;
    public RelayCommand SignInCommand => new(() => _main.ShowAccountCommand.Execute(null));

    /// <summary>Called by the main view model when the user signs in or out while this page is open.</summary>
    internal void AccountChanged() { OnPropertyChanged(nameof(IsSignedOut)); UpdateSummary(); }

    partial void OnIncludeSystemFoldersChanged(bool value) => _ = LoadTreeAsync();
    partial void OnOutputFolderChanged(string value) => UpdateSummary();
    partial void OnSelectedFolderChanged(FolderNode? value) => _ = LoadItemsAsync(value);
    partial void OnSelectedItemChanged(MessageRow? value) => ShowPreview(value);

    async Task LoadTreeAsync()
    {
        IsLoadingTree = true;
        TreeError = "";
        try
        {
            var includeSystem = IncludeSystemFolders;
            var roots = await Task.Run(() => BuildTree(includeSystem));
            Roots.Clear();
            foreach (var r in roots)
            {
                Hook(r);
                Roots.Add(r);
            }
            UpdateSummary();
            SelectedFolder = FirstWithItems(Roots);
        }
        catch (Exception e) when (e is PffFormatException or IOException)
        {
            TreeError = "The folder list could not be read: " + e.Message;
        }
        finally { IsLoadingTree = false; }
    }

    List<FolderNode> BuildTree(bool includeSystem)
    {
        var byPath = new Dictionary<string, FolderNode>();
        var roots = new List<FolderNode>();
        foreach (var (folder, raw) in _store.WalkFolders())
        {
            if (!includeSystem && FolderPaths.IsSystem(raw)) continue;
            if (FolderPaths.Display(raw).Length == 0) continue; // store containers (Root, IPM_SUBTREE) aren't shown

            int count;
            try { count = folder.GetMessageCount(); } catch (PffFormatException) { count = 0; }

            FolderNode? parent = null;
            for (var p = ParentPath(raw); p.Length > 0 && parent is null; p = ParentPath(p)) byPath.TryGetValue(p, out parent);

            var node = new FolderNode(folder, raw, count, parent) { IsExpanded = parent is null || parent.Parent is null };
            byPath[raw] = node;
            (parent?.Children ?? (ICollection<FolderNode>)roots).Add(node);
            if (count > 0) node.SetChecked(true, cascade: false);
        }
        Prune(roots);
        return roots;
    }

    static string ParentPath(string p) { int i = p.LastIndexOf('/'); return i < 0 ? "" : p[..i]; }

    /// <summary>Drops folders that, with everything beneath them, hold no items.</summary>
    static void Prune(ICollection<FolderNode> nodes)
    {
        foreach (var n in nodes.ToList())
        {
            Prune(n.Children);
            if (n.TotalCount == 0) nodes.Remove(n);
        }
    }

    void Hook(FolderNode n)
    {
        n.CheckChanged += UpdateSummary;
        foreach (var c in n.Children) Hook(c);
    }

    static FolderNode? FirstWithItems(IEnumerable<FolderNode> nodes)
    {
        foreach (var n in nodes)
        {
            if (n.Count > 0) return n;
            if (FirstWithItems(n.Children) is { } hit) return hit;
        }
        return null;
    }

    IEnumerable<FolderNode> AllNodes() => Roots.SelectMany(r => r.Descendants().Prepend(r));

    public ISet<ulong> CheckedNids() => AllNodes().Where(n => n.IsChecked).Select(n => n.Folder.Nid).ToHashSet();

    void UpdateSummary()
    {
        var chosen = AllNodes().Where(n => n.IsChecked && n.Count > 0).ToList();
        long total = chosen.Sum(n => (long)n.Count);
        CanConvert = chosen.Count > 0 && !string.IsNullOrWhiteSpace(OutputFolder) && !_main.IsSignedOut;

        if (chosen.Count == 0) { Summary = "No folders selected."; return; }
        Summary = $"{chosen.Count:N0} folder{(chosen.Count == 1 ? "" : "s")} · {total:N0} items selected";
    }

    [RelayCommand] void SelectAll() { foreach (var r in Roots) r.SetChecked(true, true); }
    [RelayCommand] void SelectNone() { foreach (var r in Roots) r.SetChecked(false, true); }

    [RelayCommand]
    void BrowseOutput()
    {
        var dlg = new OpenFolderDialog { Title = "Choose where to save the converted files" };
        if (dlg.ShowDialog() == true) OutputFolder = dlg.FolderName;
    }

    [RelayCommand]
    void ClearDates() { DateFrom = null; DateTo = null; }

    [RelayCommand]
    void Convert()
    {
        var options = new ConversionOptions
        {
            OutputDir = OutputFolder,
            Format = SelectedFormat.Format,
            FolderNids = CheckedNids().ToHashSet(),
            IncludeSystemFolders = IncludeSystemFolders,
            From = DateFrom,
            To = DateTo,
            Entitlement = _main.Entitlement,
            SplitSizeBytes = SelectedSplit.Bytes,
        };
        _main.StartConversion(options);
    }

    [RelayCommand] void ChooseAnotherFile() => _main.NewFile();

    async Task LoadItemsAsync(FolderNode? node)
    {
        _itemsCts?.Cancel();
        Items.Clear();
        SelectedItem = null;
        ItemsNote = "";
        if (node is null || node.Count == 0) return;

        var cts = _itemsCts = new CancellationTokenSource();
        IsLoadingItems = true;
        try
        {
            var rows = await Task.Run(() => ReadRows(node, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;
            foreach (var r in rows) Items.Add(r);
            ItemsNote = node.Count > MaxPreviewRows ? $"Showing the first {MaxPreviewRows:N0} of {node.Count:N0} items." : "";
        }
        catch (OperationCanceledException) { }
        finally { if (_itemsCts == cts) IsLoadingItems = false; }
    }

    List<MessageRow> ReadRows(FolderNode node, CancellationToken ct)
    {
        var rows = new List<MessageRow>();
        foreach (var nid in node.Folder.GetMessageIds().Take(MaxPreviewRows))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var m = _store.OpenMessage(nid);
                if (m is null) continue;
                var kind = ItemKinds.Classify(m.MessageClass);
                var date = MimeBuilder.MessageDate(m);
                var who = kind == ItemKind.Mail ? (m.SenderName.Length > 0 ? m.SenderName : m.SenderAddress) : "";
                var subject = m.Subject.Length > 0 ? m.Subject : (m.GetString(PropTag.DisplayName) ?? "");
                var shownSubject = subject.Length > 0 ? subject : "(no subject)";
                rows.Add(new MessageRow(m, date?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "", who, shownSubject, kind.ToString()));
            }
            catch (PffFormatException) { /* damaged item: shown nowhere in the preview, reported at conversion time */ }
        }
        return rows;
    }

    void ShowPreview(MessageRow? row)
    {
        if (row is null) { PreviewHeader = ""; PreviewBody = "Select a message to preview it."; return; }
        var m = row.Message;
        var sb = new StringBuilder();
        if (row.From.Length > 0) sb.AppendLine("From:  " + row.From + (m.SenderAddress.Length > 0 && m.SenderAddress != row.From ? $" <{m.SenderAddress}>" : ""));
        var to = string.Join("; ", m.Recipients.Where(r => r.Kind == 1).Select(r => r.Name.Length > 0 ? r.Name : r.Address));
        if (to.Length > 0) sb.AppendLine("To:  " + to);
        if (row.Date.Length > 0) sb.AppendLine("Date:  " + row.Date);
        sb.Append("Subject:  " + row.Subject);
        var names = m.Attachments.Select(a => a.FileName).ToList();
        if (names.Count > 0) sb.Append($"\nAttachments ({names.Count}):  " + string.Join(", ", names.Take(8)) + (names.Count > 8 ? ", …" : ""));
        PreviewHeader = sb.ToString();

        var body = BodyText.Get(m);
        PreviewBody = body.Length == 0 ? "(no text content)" : body.Length > 100_000 ? body[..100_000] + "\n\n[preview truncated]" : body;
    }
}
