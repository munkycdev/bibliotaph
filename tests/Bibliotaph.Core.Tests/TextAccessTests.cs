namespace Bibliotaph.Core.Tests;

public sealed class TextAccessTests
{
    [Theory]
    [InlineData(StageStatus.Blocked, TextAccessReasons.Locked, TextAccess.Locked)]
    [InlineData(StageStatus.Failed, TextAccessReasons.Protected, TextAccess.Protected)]
    [InlineData(StageStatus.Skipped, TextAccessReasons.Forgotten, TextAccess.Forgotten)]
    // Only the exact pair: a damaged file, a folder offline, or a reason in another state is an ordinary file.
    [InlineData(StageStatus.Failed, "This file isn't a readable PDF; it may be damaged or incomplete.", TextAccess.Readable)]
    [InlineData(StageStatus.Blocked, "No copy of this file can be read right now: its folder is offline or the file has gone.", TextAccess.Readable)]
    [InlineData(StageStatus.Failed, TextAccessReasons.Locked, TextAccess.Readable)]
    [InlineData(StageStatus.Complete, null, TextAccess.Readable)]
    public void A_stages_status_and_reason_say_whether_its_text_is_withheld(StageStatus status, string? reason, TextAccess expected) =>
        Assert.Equal(expected, TextAccessReasons.Of(status, reason));

    [Fact]
    public void A_stage_that_never_ran_withholds_nothing() => Assert.Equal(TextAccess.Readable, TextAccessReasons.Of(null, null));
}
