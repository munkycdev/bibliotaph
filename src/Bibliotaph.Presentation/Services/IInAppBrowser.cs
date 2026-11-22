namespace Bibliotaph.Presentation.Services;

/// <summary>Bibliotaph's own browser, which books owned online open in; the view that shows one is the platform's.</summary>
public interface IInAppBrowser
{
    /// <summary>Whether it can run here; without it, books owned online open in the everyday browser.</summary>
    bool IsInstalled { get; }
}
