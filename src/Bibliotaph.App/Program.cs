using Velopack;

namespace Bibliotaph.App;

/// <summary>
/// The entry point, in place of the one WPF writes for App.xaml, so that Velopack runs first (slice 4l plan, choice 1).
/// When the installer or an update starts Bibliotaph to run one of its hooks, Velopack handles it and exits here,
/// before a window or a database opens. Otherwise it returns at once, also in a build that isn't installed.
/// </summary>
static class Program
{
    [STAThread]
    static void Main()
    {
        VelopackApp.Build().Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
