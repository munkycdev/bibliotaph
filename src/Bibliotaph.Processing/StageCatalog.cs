using Bibliotaph.Core;

namespace Bibliotaph.Processing;

/// <summary>
/// The stages indexing runs, for whatever needs to know about them: which lane runs each, what follows each, and
/// what the app calls each.
/// </summary>
public interface IStageCatalog
{
    /// <summary>Every stage, in pipeline order.</summary>
    IReadOnlyList<IStage> All { get; }

    /// <summary>The stage that does <paramref name="stage"/>, or null when indexing doesn't run it.</summary>
    IStage? Find(Stage stage);

    /// <summary>The stages queued whenever <paramref name="stage"/> finishes, complete or partly, in pipeline order.</summary>
    IReadOnlyList<Stage> Following(Stage stage);
}

/// <summary>The catalog of the stages given to indexing. Each step has at most one stage.</summary>
public sealed class StageCatalog : IStageCatalog
{
    readonly Dictionary<Stage, IStage> _byStage = [];
    readonly Dictionary<Stage, IReadOnlyList<Stage>> _following;

    public StageCatalog(IEnumerable<IStage> stages)
    {
        All = [.. stages.OrderBy(s => s.Stage)];
        foreach (var stage in All)
            if (!_byStage.TryAdd(stage.Stage, stage)) throw new ArgumentException($"More than one stage does {stage.Stage}.", nameof(stages));
        _following = All.Where(s => s.After is not null).GroupBy(s => s.After!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Stage>)[.. g.Select(s => s.Stage)]);
    }

    public IReadOnlyList<IStage> All { get; }

    public IStage? Find(Stage stage) => _byStage.GetValueOrDefault(stage);

    public IReadOnlyList<Stage> Following(Stage stage) => _following.GetValueOrDefault(stage) ?? [];
}
