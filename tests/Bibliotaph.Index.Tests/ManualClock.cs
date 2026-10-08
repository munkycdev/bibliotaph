namespace Bibliotaph.Index.Tests;

/// <summary>A clock the test moves by hand.</summary>
sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
