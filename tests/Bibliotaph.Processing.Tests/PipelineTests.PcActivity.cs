using Bibliotaph.Api.Contracts;
using Bibliotaph.Core;
using Bibliotaph.Pdf.Host.Tests;

namespace Bibliotaph.Processing.Tests;

public abstract partial class PipelineTests
{
    /// <summary>The web library as a PC tells it what its AI is doing, in memory: every report, null for "nothing to do".</summary>
    sealed class FakeActivityChannel : IPcActivityChannel
    {
        public bool IsOn { get; set; } = true;

        public List<PcActivity?> Said { get; } = [];

        public Task<bool> ReportActivityAsync(PcActivity? activity, CancellationToken ct)
        {
            lock (Said) Said.Add(activity);
            return Task.FromResult(true);
        }
    }

    /// <summary>A connected PC telling the web what its AI is doing, for Settings › Processing there.</summary>
    public sealed class PcActivityTests(SyntheticPdfs synthetic) : PipelineTests(synthetic)
    {
        readonly FakeActivityChannel _channel = new();

        PcActivityReport Report() => new(_service, _queries, _ai, _channel);

        [Fact]
        public async Task Nothing_is_said_while_the_AI_isnt_set_up_or_the_PC_isnt_connected()
        {
            Copy(_pdfs.KnownText, "Adventures/Known Text.pdf");
            await _roots.AddAsync(_library, Ct);
            await _service.StartAsync(Ct);
            await SettleAsync();
            var report = Report();

            await report.ReportNowAsync(Ct);
            await SwitchAiOnAsync();
            _model.Down = true;
            _channel.IsOn = false;
            await report.ReportNowAsync(Ct);

            Assert.Empty(_channel.Said);
        }

        [Fact]
        public async Task The_AI_waiting_is_said_with_its_counts_and_its_finishing_once()
        {
            _model.Down = true;
            Copy(_pdfs.KnownText, "Adventures/Known Text.pdf");
            await _roots.AddAsync(_library, Ct);
            await SwitchAiOnAsync();
            await _service.StartAsync(Ct);
            await SettleAsync();
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct))
            {
                timeout.CancelAfter(Patience);
                while (_service.Unavailable(Lane.Classify) is null) await Task.Delay(50, timeout.Token);
            }
            var report = Report();

            await report.ReportNowAsync(Ct);
            _model.Down = false;
            await SettleClassifyAsync();
            await report.ReportNowAsync(Ct);
            await report.ReportNowAsync(Ct);

            Assert.Equal(2, _channel.Said.Count);
            var waiting = _channel.Said[0]!;
            Assert.Contains("Nothing is answering", waiting.Waiting, StringComparison.Ordinal);
            Assert.Equal((false, 0L, 1L), (waiting.Paused, waiting.Classified, waiting.ToClassify));
            // Done: said once, then nothing until there is work again.
            Assert.Null(_channel.Said[1]);
        }
    }
}
