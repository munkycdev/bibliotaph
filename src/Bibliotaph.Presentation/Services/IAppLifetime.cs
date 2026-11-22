namespace Bibliotaph.Presentation.Services;

/// <summary>Ways a page ends the app: restarting it, or starting over with nothing kept.</summary>
public interface IAppLifetime
{
    /// <summary>Restarts with the same data folder, as a restore needs to swap its catalog in.</summary>
    void Restart();

    /// <summary>Forgets every secret now and restarts, deleting the library data before anything opens it.</summary>
    void StartOver();
}
