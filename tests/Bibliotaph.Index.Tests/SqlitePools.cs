namespace Bibliotaph.Index.Tests;

/// <summary>
/// Test classes that call SqliteConnection.ClearAllPools, as the app does when it closes, run here, one at a time and
/// after the other classes. ClearAllPools disposes every pool's idle connections, so a test opening a connection on
/// another thread at that moment fails with ObjectDisposedException. Other classes clear only their own file's pool.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SqlitePools
{
    public const string Name = "Clears every SQLite pool";
}
