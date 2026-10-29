using Bibliotaph.App.ViewModels;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Processing;

namespace Bibliotaph.App.Tests;

/// <summary>Needs review's side of the cards: it records what the cards asked for, and each answer's undo counts itself.</summary>
sealed class FakeReviewActions : IReviewActions
{
    public List<string> Calls { get; } = [];

    /// <summary>Each <see cref="IReviewActions.Decided"/>: -1 as a card is decided, +1 as it is undone.</summary>
    public List<int> Decisions { get; } = [];

    public int Undone { get; private set; }

    /// <summary>Holds Accept until it is set, to see what a card does while an answer is being saved.</summary>
    public TaskCompletionSource? HoldAccept { get; set; }

    /// <summary>What Edit, Set pages and Accept adventures say is wrong with what was typed; null when it is fine.</summary>
    public string? Problem { get; set; }

    public IReadOnlyList<ProposedPart>? AcceptedParts { get; private set; }

    public List<RevisionPlace> UsedPlaces { get; } = [];

    Task<Func<Task>> Answer(string call)
    {
        Calls.Add(call);
        return Task.FromResult(Undo);
    }

    Func<Task> Undo => () =>
    {
        Undone++;
        return Task.CompletedTask;
    };

    public async Task<Func<Task>> AcceptAsync(ReviewItem item)
    {
        Calls.Add("accept");
        if (HoldAccept is { } hold) await hold.Task;
        return Undo;
    }

    public Task AcceptAllAsync(ReviewItem item)
    {
        Calls.Add("accept all");
        return Task.CompletedTask;
    }

    public Task<Func<Task>> RejectAsync(ReviewItem item) => Answer("reject");

    public Task<(string? Problem, Func<Task>? Undo)> EditAsync(ReviewItem item, string typed)
    {
        Calls.Add($"edit {typed}");
        return Task.FromResult<(string?, Func<Task>?)>(Problem is null ? (null, Undo) : (Problem, null));
    }

    public Task<Func<Task>> AddTermAsync(PendingTerm term) => Answer($"add {term.Term.Label}");

    public Task<Func<Task>> MapTermAsync(PendingTerm term, Term target) => Answer($"map {term.Term.Label} to {target.Key}");

    public Task<Func<Task>> RejectTermAsync(PendingTerm term) => Answer($"reject {term.Term.Label}");

    public Task<Func<Task>> CombineTermsAsync(OverlapProposal proposal, long keepId) => Answer($"combine into {keepId}");

    public Task<Func<Task>> KeepTermsApartAsync(OverlapProposal proposal) => Answer($"keep apart {proposal.Id}");

    public Task<Func<Task>> AnswerVersionAsync(VersionItem item, VersionAnswer answer) => Answer($"version {answer}");

    public Task<Func<Task>> AnswerPackAsync(PackProposal proposal, PackAnswer answer) => Answer($"pack {answer}");

    public Task<Func<Task>> AnswerElsewhereAsync(ElsewhereItem item, ElsewhereAnswer answer) => Answer($"elsewhere {answer}");

    public Task<Func<Task>?> UsePlaceAsync(RevisionPlace place)
    {
        UsedPlaces.Add(place);
        return Task.FromResult<Func<Task>?>(Undo);
    }

    public Task<IReadOnlyList<string?>> GetPageLabelsAsync(long documentId) => Task.FromResult<IReadOnlyList<string?>>([]);

    public Task<(string? Problem, Func<Task>? Undo)> SetPartPagesAsync(UnplacedPart part, PartPages pages) =>
        Task.FromResult<(string?, Func<Task>?)>(Problem is null ? (null, Undo) : (Problem, null));

    public Task<Func<Task>> RemovePartAsync(UnplacedPart part) => Answer("remove part");

    public Task<(string? Problem, Func<Task>? Undo)> AcceptPartsAsync(PartProposalItem item, IReadOnlyList<ProposedPart> chosen)
    {
        Calls.Add("accept parts");
        AcceptedParts = chosen;
        return Task.FromResult<(string?, Func<Task>?)>(Problem is null ? (null, Undo) : (Problem, null));
    }

    public Task<Func<Task>> NotSplitAsync(PartProposalItem item) => Answer("not split");

    public Task<Func<Task>> DismissCorrectionAsync(CorrectionItem item) => Answer("dismiss correction");

    public void FixCorrection(CorrectionItem item) => Calls.Add("fix correction");

    public Task<Func<Task>> AnswerRuleAsync(FolderRuleInfo rule, bool use) => Answer($"rule {use}");

    public Task<Func<Task>> AnswerFolderAsync(FolderCollectionProposal proposal, CollectionAnswer answer) => Answer($"folder {answer}");

    public Task<Func<Task>> AnswerSuggestionAsync(CollectionSuggestion suggestion, CollectionAnswer answer) => Answer($"suggestion {answer}");

    public void OpenPlace(RevisionItem item, RevisionPlace place) => Calls.Add("open place");

    public Task OpenPlaceInWindowAsync(RevisionItem item, RevisionPlace place) => Call("open place in window");

    public void ShowFolder(string path) => Calls.Add("show folder");

    public void Open(ReviewItem item) => Calls.Add("open");

    public Task OpenInWindowAsync(ReviewItem item) => Call("open in window");

    public void OpenDocument(long documentId, string title) => Calls.Add($"open document {documentId}");

    public Task OpenDocumentInWindowAsync(long documentId, string title) => Call($"open document {documentId} in window");

    Task Call(string call)
    {
        Calls.Add(call);
        return Task.CompletedTask;
    }

    public void Decided(int change) => Decisions.Add(change);
}
