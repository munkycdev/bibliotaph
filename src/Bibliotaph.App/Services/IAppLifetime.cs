namespace Bibliotaph.App.Services;

/// <summary>Ways a page ends the app: restarting it, or starting over with nothing kept.</summary>
public interface IAppLifetime
{
    /// <summary>Restarts with the same data folder, as a restore needs to swap its catalog in.</summary>
    void Restart();

    /// <summary>Forgets every secret now and restarts, deleting the library data before anything opens it.</summary>
    void StartOver();
}

/// <summary><see cref="IAppLifetime"/> through <see cref="AppRestart"/> and <see cref="Services.StartOver"/>.</summary>
public sealed class AppLifetime(AppRestart restart, StartOver startOver) : IAppLifetime
{
    public void Restart() => restart.Run();

    public void StartOver() => startOver.Run();
}
