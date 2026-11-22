namespace Bibliotaph.Presentation.Services;

/// <summary>How much memory the computer's biggest graphics card has, so Settings > AI can suggest the model that fits.</summary>
public interface IGraphicsMemory
{
    /// <summary>The largest card's memory in bytes, or null when the system doesn't say.</summary>
    long? Bytes { get; }
}
