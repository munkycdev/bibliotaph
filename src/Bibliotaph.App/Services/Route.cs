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
    /// <summary>What the AI is reading now, and the last books it read.</summary>
    AiReading,
    /// <summary>A book owned online, read on its service's page; by route alone, the Library.</summary>
    OnlineReader,
}
