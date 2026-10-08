namespace Bibliotaph.Pdf.Host;

public class WorkerException(string message) : Exception(message);

public sealed class WorkerTimeoutException(TimeSpan timeout)
    : WorkerException($"Worker did not answer within {timeout.TotalSeconds:0.#} s and was killed.");

public sealed class WorkerCrashedException(string detail) : WorkerException($"Worker exited unexpectedly ({detail}).");

/// <summary>Thrown without contacting a worker: this page has already killed one twice.</summary>
public sealed class PagePoisonedException(PageKey page)
    : WorkerException($"Page {page.PageIndex} of {page.Path} has killed the PDF worker {PoisonTracker.KillsToPoison} times and is skipped.")
{
    public PageKey Page { get; } = page;
}
