namespace Bibliotaph.App.Services;

/// <summary>
/// Whether the app started with --dev, which shows Settings > Start over. Without it nothing leads there, so nobody
/// else finds a button that deletes everything.
/// </summary>
public sealed class DevMode(bool on)
{
    public bool IsOn { get; set; } = on;
}
