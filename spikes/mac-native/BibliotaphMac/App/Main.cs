using AppKit;
using Foundation;

namespace Bibliotaph.MacSpike;

static class Program
{
    static void Main(string[] args)
    {
        NSApplication.Init();
        NSApplication.SharedApplication.Delegate = new AppDelegate();
        NSApplication.Main(args);
    }
}

[Register(nameof(AppDelegate))]
sealed class AppDelegate : NSApplicationDelegate
{
    MainWindowController? _main;

    public override void DidFinishLaunching(NSNotification notification)
    {
        _main = new MainWindowController();
        NSApplication.SharedApplication.MainMenu = Menus.Build(_main);
        _main.ShowWindow(this);
        NSApplication.SharedApplication.Activate();
    }

    public override bool ApplicationShouldTerminateAfterLastWindowClosed(NSApplication sender) => true;
}
