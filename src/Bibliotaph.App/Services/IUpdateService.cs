using System.ComponentModel;

namespace Bibliotaph.App.Services;

/// <summary>
/// Updates from GitHub Releases: checked at start and once a day, downloaded in the background, then offered in the
/// sidebar as "Update ready: restart to update". Bibliotaph never restarts by itself, since the user may be mid-session
/// at the table. An interface so the smoke test can show an update as ready without one, and never restart.
/// </summary>
public interface IUpdateService : INotifyPropertyChanged
{
    /// <summary>True once an update has downloaded and waits for a restart.</summary>
    bool IsReady { get; }

    /// <summary>"Version 0.5.1", for the sidebar; empty until an update is ready.</summary>
    string ReadyText { get; }

    /// <summary>Starts checking, once the window shows. Does nothing in a build that wasn't installed.</summary>
    void Start();

    /// <summary>Closes Bibliotaph, applies the update and opens it again. Only when the user asks.</summary>
    void RestartToUpdate();
}
