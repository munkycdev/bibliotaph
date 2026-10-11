namespace Bibliotaph.App.Services;

/// <summary>The app's screens.</summary>
public enum Route
{
    Home,
    Library,
    Collections,
    Sessions,
    /// <summary>One binder: its pages in the reader, under Binders.</summary>
    SessionPack,
    /// <summary>Editing one binder: its items in order, with sections and notes.</summary>
    SessionEdit,
    NeedsReview,
    Settings,
    PilotReview,
    Viewer,
    DownloadCheck,
    /// <summary>What the AI is reading now, and the last books it read (Built-in AI plan, choice 13).</summary>
    AiReading,
    /// <summary>Books owned on online services, imported from their library pages (online libraries, O2).</summary>
    OnlineLibraries,
}
