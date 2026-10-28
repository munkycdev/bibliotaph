using Bibliotaph.Core;
using Bibliotaph.Index;

namespace Bibliotaph.Processing;

/// <summary>
/// Finds the ISBNs and product codes a book prints about itself (<see cref="BookIdentifiers"/>), from the text already in
/// index.db and an e-book's package, so a book can be searched for by its ISBN and named exactly by a lookup. It waits
/// for the book's scanned pages, since a scanned back cover's barcode number is only text once OCR has read it, and it
/// never reads the file. Another file that prints the same ISBN is proposed as a new version of it.
/// </summary>
public sealed class IdentifyStage(IndexStore index, IndexQueries queries, VersionFinder versions) : IStage
{
    public Stage Stage => Stage.Identify;

    public Lane Lane => Lane.Index;

    public Stage? After => Stage.Text;

    /// <summary>Books read before it existed get it too, from their stored text.</summary>
    public bool Backfill => true;

    public StageNames Names => new("Finding its ISBN", "Finding its ISBN", "Working on");

    public async Task<StageOutcome> RunAsync(JobRecord job, CancellationToken ct)
    {
        if (await queries.GetStageStatusAsync(job.DocumentId, Stage.Ocr, ct) is StageStatus.Pending or StageStatus.Running)
            return new StageOutcome.Later(StageWaits.ForOcr, "Waiting for its scanned pages to be read.");

        var identifiers = BookIdentifiers.Find(await queries.GetAllPageTextsAsync(job.DocumentId, ct), await queries.GetPackageIsbnAsync(job.DocumentId, ct));
        await index.SetIdentifiersAsync(job.DocumentId, identifiers, ct);
        if (identifiers.Any(i => i.Kind == IdentifierKind.Isbn)) await versions.ProposeByIsbnAsync(job.DocumentId, ct);
        return identifiers.Count > 0 ? StageOutcome.Complete() : new StageOutcome.Done(StageStatus.Skipped, [], "No ISBN or product code printed in it.");
    }
}
