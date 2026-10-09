using System.Diagnostics;
using Bibliotaph.Catalog;
using Bibliotaph.Core.Search;
using Bibliotaph.Index;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Bibliotaph.App.Services;

/// <summary>
/// <c>Bibliotaph.exe --measure-search</c>: runs a fixed set of searches against the library in the data root, the way
/// the Library runs them (visible documents, then both tabs at once), and logs p50 and p95 for each and overall
/// against the 1-second p95 target. Results go to the log, not a file: the app doesn't write files. The queries are
/// generic words, so the log names no book.
/// </summary>
static class SearchMeasurement
{
    const int Runs = 20;
    static readonly TimeSpan Target = TimeSpan.FromSeconds(1);

    static readonly string[] Queries =
    [
        "dragon", "goblin*", "\"secret door\"", "tavern OR inn", "red dragon lair", "lich -vampire",
        "title:dragon", "format:pdf map", "-maps", "undead (crypt OR tomb)",
    ];

    public static async Task<int> RunAsync(IServiceProvider services)
    {
        var library = services.GetRequiredService<LibraryStore>();
        var queries = services.GetRequiredService<LibraryQueries>();
        try
        {
            var visible = await library.GetVisibleEntryIdsAsync();
            Log.Information("Measuring search over {Documents} documents, {Runs} runs per query", visible.Count, Runs);
            var all = new List<double>();

            var (browse, listed) = await TimeAsync(async () =>
                $"{(await queries.ListAsync(new LibraryFilter(await library.GetVisibleEntryIdsAsync()))).Count} documents");
            Report("(browse the library)", browse, listed);

            foreach (var text in Queries)
            {
                var plan = SearchPlan.From(SearchQuery.Parse(text));
                var (times, found) = await TimeAsync(async () =>
                {
                    var filter = new LibraryFilter(await library.GetVisibleEntryIdsAsync());
                    var documents = Task.Run(() => queries.SearchDocumentsAsync(plan, filter));
                    var pages = Task.Run(() => queries.SearchPagesAsync(plan, filter));
                    return $"{(await documents).Count} documents, {(await pages).MatchingPages} pages";
                });
                Report(text, times, found);
                all.AddRange(times);
            }

            var p95 = Percentile(all, 0.95);
            Log.Information("Search overall: p50 {P50:F0} ms, p95 {P95:F0} ms over {Count} searches; target p95 under {Target:F0} ms: {Verdict}",
                Percentile(all, 0.5), p95, all.Count, Target.TotalMilliseconds, p95 < Target.TotalMilliseconds ? "PASS" : "FAIL");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Measuring search failed");
            return 1;
        }
    }

    /// <summary>One untimed warm-up, then <see cref="Runs"/> timed runs.</summary>
    static async Task<(List<double> Times, string Found)> TimeAsync(Func<Task<string>> search)
    {
        var found = await search();
        var times = new List<double>(Runs);
        for (var i = 0; i < Runs; i++)
        {
            var started = Stopwatch.GetTimestamp();
            await search();
            times.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        return (times, found);
    }

    static void Report(string query, List<double> times, string found) =>
        Log.Information("Search {Query}: {Found}; p50 {P50:F0} ms, p95 {P95:F0} ms", query, found, Percentile(times, 0.5), Percentile(times, 0.95));

    /// <summary>Nearest-rank percentile.</summary>
    static double Percentile(List<double> values, double p)
    {
        var sorted = values.Order().ToList();
        return sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];
    }
}
