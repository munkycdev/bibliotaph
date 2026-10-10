namespace Bibliotaph.App.Services;

/// <summary>The app's screens.</summary>
public enum Route
{
    Home,
    Library,
    Collections,
    Sessions,
    /// <summary>One session pack's page, under Sessions.</summary>
    SessionPack,
    /// <summary>Run mode: a session pack at the table, filling the window.</summary>
    RunSession,
    NeedsReview,
    Settings,
    PilotReview,
    Viewer,
    DownloadCheck,
}
