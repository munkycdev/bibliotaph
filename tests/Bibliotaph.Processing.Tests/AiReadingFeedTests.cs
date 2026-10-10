using Bibliotaph.Classification;

namespace Bibliotaph.Processing.Tests;

public sealed class AiReadingFeedTests
{
    [Fact]
    public void Follows_a_book_through_reading_and_writing_to_done()
    {
        var feed = new AiReadingFeed();
        var progress = feed.Start(7, "The Sunken Lantern", "Qwen3 8B (built in)");
        Assert.Equal(AiReadingStep.Waiting, feed.Current!.Step);

        progress.Report(new ModelProgress(ModelStep.Reading, "", 0.5));
        Assert.Equal((AiReadingStep.Reading, 0.5), (feed.Current.Step, feed.Current.Read));

        progress.Report(new ModelProgress(ModelStep.Writing, """{"title":[{"value":"The Sunken Lantern","page":1"""));
        Assert.Equal(AiReadingStep.Writing, feed.Current.Step);
        Assert.Equal(1, feed.Current.Read);
        Assert.Equal(["The Sunken Lantern"], Assert.Single(feed.Current.Fields).Values);

        feed.Finish(7, new ClassifierResult("built-in", "Qwen3 8B (built in)", [], []));
        Assert.Null(feed.Current);
        var done = Assert.Single(feed.Recent);
        Assert.Equal((AiReadingStep.Done, 7), (done.Step, done.DocumentId));
        Assert.NotNull(done.Took);

        // A report that comes after the book ended changes nothing.
        progress.Report(new ModelProgress(ModelStep.Writing, "{}"));
        Assert.Null(feed.Current);
    }

    [Fact]
    public void Keeps_the_last_twenty_books_and_forgets_one_the_server_never_read()
    {
        var feed = new AiReadingFeed();
        for (var id = 1; id <= 25; id++)
        {
            feed.Start(id, $"Book {id}", "model");
            if (id % 2 == 0) feed.Fail(id, "The model's answer wasn't JSON.");
            else feed.Finish(id, new ClassifierResult("ollama", "model", [], []));
        }
        feed.Start(26, "Book 26", "model");
        feed.Abandon(26);

        Assert.Equal(AiReadingFeed.Keep, feed.Recent.Count);
        Assert.Equal((25, 6), (feed.Recent[0].DocumentId, feed.Recent[^1].DocumentId));
        Assert.Equal("The model's answer wasn't JSON.", feed.Recent[1].Problem);
        Assert.Null(feed.Current);
    }
}
