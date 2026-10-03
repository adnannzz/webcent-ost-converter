#if DEBUG
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OstConverter.App.ViewModels;
using OstConverterFree.Accounts;

namespace OstConverter.App;

/// <summary>
/// Debug-only: renders screens to PNG files so the UI can be checked, and documentation pictures made, without a display
/// session. Enabled by the OST_SNAP_DIR environment variable; OST_SNAP_FILE names a file to open and OST_SNAP_DEMOLIST=1
/// replaces the files found on this PC with invented entries. Use only invented data (ostcli demo) for published pictures.
/// </summary>
static class DevSnapshot
{
    public static bool Requested => Environment.GetEnvironmentVariable("OST_SNAP_DIR") is { Length: > 0 };

    public static async Task RunAsync(MainWindow window, MainViewModel vm, AccountManager account)
    {
        var dir = Environment.GetEnvironmentVariable("OST_SNAP_DIR")!;
        var file = Environment.GetEnvironmentVariable("OST_SNAP_FILE");
        Directory.CreateDirectory(dir);

        await Task.Delay(700);
        if (Environment.GetEnvironmentVariable("OST_SNAP_DEMOLIST") == "1" && vm.CurrentPage is SelectViewModel select)
        {
            select.Detected.Clear();
            select.Detected.Add(new DetectedFile("", "Demo Mailbox.ost", "118 MB", "OST"));
            select.Detected.Add(new DetectedFile("", "Archive 2023.pst", "342 MB", "PST"));
            await Task.Delay(300);
        }
        Save(window, Path.Combine(dir, "1-select.png"));

        if (Environment.GetEnvironmentVariable("OST_SNAP_ACCOUNT") == "1")
        {
            var vmAccount = new AccountViewModel(account);
            var win = new AccountWindow(vmAccount) { Owner = window };
            win.Show();
            await Task.Delay(1000);
            Save(win, Path.Combine(dir, "account-a.png"));
            win.Close();
        }

        if (!string.IsNullOrEmpty(file))
        {
            await vm.OpenFileAsync(file);
            if (vm.CurrentPage is OptionsViewModel options)
            {
                await WaitFor(() => !options.IsLoadingTree && !options.IsLoadingItems);
                if (options.Items.Count > 0) options.SelectedItem = options.Items[0];
                await Task.Delay(600);
                Save(window, Path.Combine(dir, "2-options.png"));
            }
        }
        Application.Current.Shutdown();
    }

    static async Task WaitFor(Func<bool> condition, int seconds = 60)
    {
        for (int i = 0; i < seconds * 10 && !condition(); i++) await Task.Delay(100);
    }

    static void Save(Window w, string path)
    {
        w.UpdateLayout();
        var root = (FrameworkElement)w.Content;
        int width = (int)w.ActualWidth, height = (int)w.ActualHeight;
        var bmp = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        bmp.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
#endif
