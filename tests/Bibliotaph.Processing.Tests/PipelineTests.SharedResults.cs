using System.Text.Json;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Catalog;
using Bibliotaph.Classification;
using Bibliotaph.Core;
using Bibliotaph.Core.Metadata;
using Bibliotaph.Pdf.Host.Tests;
using Dapper;

namespace Bibliotaph.Processing.Tests;

public abstract partial class PipelineTests
{
    /// <summary>The web library's store of shared results, in memory: the newest for each file and kind, numbered as they arrive.</summary>
    sealed class FakeHub : IResultChannel
    {
        readonly Lock _lock = new();
        readonly Dictionary<(string Hash, string Kind), (long Seq, SharedResult Result, bool Ours)> _held = [];
        long _seq;

        public bool IsOn { get; set; } = true;

        /// <summary>Every result this device sent, in order, including those the hub kept already.</summary>
        public List<SharedResult> Sent { get; } = [];

        /// <summary>A result from another device.</summary>
        public void Add(SharedResult result)
        {
            lock (_lock) _held[(result.ContentHash, result.Kind)] = (++_seq, result, false);
        }

        public SharedResult? Find(string hash, string kind)
        {
            lock (_lock) return _held.TryGetValue((hash, kind), out var held) ? held.Result : null;
        }

        public Task<SharedResultPage?> ReadResultsAsync(long after, CancellationToken ct)
        {
            lock (_lock)
            {
                var newer = _held.Values.Where(h => h.Seq > after).OrderBy(h => h.Seq).ToList();
                return Task.FromResult<SharedResultPage?>(new SharedResultPage([.. newer.Where(h => !h.Ours).Select(h => h.Result)],
                    newer.Count > 0 ? newer[^1].Seq : after, More: false));
            }
        }

        public Task<bool> WriteResultsAsync(IReadOnlyList<SharedResult> results, CancellationToken ct)
        {
            lock (_lock)
                foreach (var result in results)
                {
                    Sent.Add(result);
                    if (_held.TryGetValue((result.ContentHash, result.Kind), out var held) && held.Result.FinishedUtc >= result.FinishedUtc) continue;
                    _held[(result.ContentHash, result.Kind)] = (++_seq, result, true);
                }
            return Task.FromResult(true);
        }
    }

    async Task<string> OnlyHashAsync()
    {
        await using var c = _index.OpenRead();
        return await c.QuerySingleAsync<string>("SELECT content_hash FROM doc");
    }

    /// <summary>
    /// Sharing what the AI worked out through the web library, with a hub in memory in its place: a book another device
    /// read isn't sent to the model here, and one read here is shared once.
    /// </summary>
    public sealed class SharedResultsTests(SyntheticPdfs synthetic) : PipelineTests(synthetic)
    {
        static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

        static SharedResult Reading(string hash, DateTime finished, params SharedClaim[] claims) =>
            new(hash, SharedResultKinds.Classification, ClassifierPrompt.Version, "run-from-the-other-pc", finished,
                JsonSerializer.Serialize(new SharedClassification("other-model-14b", ClassifierPrompt.SchemaVersion, [0, 1, 2], finished.AddMinutes(-1), claims), Web));

        [Fact]
        public async Task A_book_another_device_read_gets_its_suggestions_and_is_never_sent_to_the_model_here()
        {
            Copy(_pdfs.Injected, "Adventures/Lantern.pdf");
            await _roots.AddAsync(_library, Ct);
            await _service.StartAsync(Ct);
            await SettleAsync();
            var hash = await OnlyHashAsync();
            var entryId = await OnlyEntryAsync();
            _hub.Add(Reading(hash, new DateTime(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc),
                new SharedClaim("levels", "3", null, "characters of 3rd level", [0], false),
                new SharedClaim("setting", "lantern-coast", "Lantern Coast", "the Lantern Coast", [1], false)));

            await _shared.SyncNowAsync(Ct);
            var metadata = (await _metadataStore.GetAsync(entryId, Ct)).Compute();
            await SwitchAiOnAsync();
            var progress = await SettleClassifyAsync();
            await _shared.SyncNowAsync(Ct);

            // The other device's values are suggestions here, as if this PC's AI had read the book; its new term waits in Needs review.
            var levels = Assert.Single(metadata[MetadataFields.Levels].Values);
            Assert.Equal(("3", AssertionOrigin.Ai), (levels.Value, levels.Origin));
            var pending = Assert.Single(await _vocabulary.GetPendingAsync(Ct));
            Assert.Equal("Lantern Coast", pending.Term.Label);
            Assert.True(await _runs.HasSharedRunAsync(hash, ClassifierPrompt.Version, Ct));

            // The model was never asked, the book counts as classified, and nothing went back to the hub.
            Assert.Empty(_model.Requests);
            Assert.Equal(1, progress.Classified);
            Assert.Empty(_hub.Sent);
            Assert.Equal(new ClassificationSummary(1, 0), await _runs.SummarizeAsync("fake-8b", ClassifierPrompt.Version, Ct));
        }

        [Fact]
        public async Task A_book_read_here_is_shared_once_and_a_newer_reading_from_elsewhere_replaces_it()
        {
            _model.Answer = _ => ObedientAnswer;
            Copy(_pdfs.Injected, "Adventures/Lantern.pdf");
            await _roots.AddAsync(_library, Ct);
            await SwitchAiOnAsync();
            await _service.StartAsync(Ct);
            await SettleClassifyAsync();
            var hash = await OnlyHashAsync();
            var entryId = await OnlyEntryAsync();

            await _shared.SyncNowAsync(Ct);
            await _shared.SyncNowAsync(Ct);
            var sent = Assert.Single(_hub.Sent);
            var content = JsonSerializer.Deserialize<SharedClassification>(sent.Content, Web)!;

            // Another device reads it again later, with a newer model, and finds the levels only.
            _hub.Add(Reading(hash, DateTime.UtcNow.AddMinutes(5), new SharedClaim("levels", "3", null, "characters of 3rd level", [0], false)));
            await _shared.SyncNowAsync(Ct);
            var metadata = (await _metadataStore.GetAsync(entryId, Ct)).Compute();

            Assert.Equal((hash, SharedResultKinds.Classification, ClassifierPrompt.Version), (sent.ContentHash, sent.Kind, sent.PromptVersion));
            Assert.Equal("fake-8b", content.Model);
            Assert.Contains(content.Claims, c => c.Field == "title" && c.Value == "The Sunken Lantern" && c.Quote == "THE SUNKEN LANTERN");
            Assert.Contains(content.Claims, c => c.Field == "authors" && c.Value == "Ana Ruiz");
            Assert.Single(_model.Requests);
            // The newer reading's values replace the older one's undecided suggestions.
            Assert.Equal("3", Assert.Single(metadata[MetadataFields.Levels].Values).Value);
            Assert.Empty(metadata[MetadataFields.Authors].Values);
            Assert.Single(_hub.Sent);
        }
    }
}
