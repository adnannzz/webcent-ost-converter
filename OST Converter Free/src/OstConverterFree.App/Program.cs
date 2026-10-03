using Velopack;

namespace OstConverter.App;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        // Must run first: handles the installer's install/update/uninstall callbacks and exits when that is all it was started for.
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
