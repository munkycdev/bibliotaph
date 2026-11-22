using Bibliotaph.Presentation.Services;

namespace Bibliotaph.App.Services;

/// <summary><see cref="IAppLifetime"/> through <see cref="AppRestart"/> and <see cref="Services.StartOver"/>.</summary>
public sealed class AppLifetime(AppRestart restart, StartOver startOver) : IAppLifetime
{
    public void Restart() => restart.Run();

    public void StartOver() => startOver.Run();
}
