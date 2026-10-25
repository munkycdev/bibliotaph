using System.Diagnostics;
using System.Text.Json;
using Spike.Bench;

// The native baseline: the same library and searches as tools/scale.mjs, with native SQLite on this machine's disk.
// Usage: Spike.Native <folder> [books] [pages]
var folder = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "bibliotaph-spike");
var books = args.Length > 1 ? int.Parse(args[1]) : 500;
var pages = args.Length > 2 ? int.Parse(args[2]) : 100;
Directory.CreateDirectory(folder);
var path = Path.Combine(folder, "index.db");
foreach (var file in Directory.GetFiles(folder, "index.db*")) File.Delete(file);

var (library, opened) = await Library.OpenAsync(path);
var watch = Stopwatch.StartNew();
for (var first = 1; first <= books; first += 50) await library.AddBooksAsync(first, Math.Min(50, books - first + 1), pages);
var addMs = watch.ElapsedMilliseconds;
var searches = new List<object>();
foreach (var query in Library.Queries)
    foreach (var run in new[] { "cold", "warm" })
        searches.Add(new { run, result = await library.SearchAsync(query) });
Console.WriteLine(JsonSerializer.Serialize(new { where = "native", books, pages, addMs, stats = Library.Stats(path), searches }));
await library.CloseAsync();
