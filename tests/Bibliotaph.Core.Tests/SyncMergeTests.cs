using System.Text;
using Bibliotaph.Core.Sync;

namespace Bibliotaph.Core.Tests;

/// <summary>Library sync's rules: newest change per field wins, deletions win over older changes, and merging in any order gives the same result.</summary>
public class SyncMergeTests
{
    static readonly DateTime T0 = new(2026, 10, 11, 9, 0, 0, DateTimeKind.Utc);

    static DateTime At(int minutes) => T0.AddMinutes(minutes);

    static SyncRecord Record(string key, params (string Name, string? Value, int Minute)[] fields) =>
        new(key, fields.ToDictionary(f => f.Name, f => new SyncField(f.Value, At(f.Minute)), StringComparer.Ordinal));

    static Dictionary<string, string> Values(params (string Name, string Value)[] fields) => fields.ToDictionary(f => f.Name, f => f.Value, StringComparer.Ordinal);

    [Fact]
    public void The_newer_change_to_a_field_wins_whichever_side_it_is_on()
    {
        var older = Record("h:a", ("m.title", "\"Old Title\"", 1));
        var newer = Record("h:a", ("m.title", "\"New Title\"", 5));

        Assert.Equal("\"New Title\"", SyncMerge.Merge(older, newer).Fields["m.title"].Value);
        Assert.Equal("\"New Title\"", SyncMerge.Merge(newer, older).Fields["m.title"].Value);
    }

    [Fact]
    public void Two_PCs_editing_different_fields_of_the_same_book_both_survive()
    {
        var first = Record("h:a", ("m.title", "\"Sunken Vault\"", 3), ("fav", "true", 1));
        var second = Record("h:a", ("m.title", "\"Untitled\"", 1), ("note", "\"Use for session 4\"", 4));

        var merged = SyncMerge.Merge(first, second);

        Assert.Equal("\"Sunken Vault\"", merged.Fields["m.title"].Value);
        Assert.Equal("true", merged.Fields["fav"].Value);
        Assert.Equal("\"Use for session 4\"", merged.Fields["note"].Value);
    }

    [Fact]
    public void A_cleared_field_wins_over_an_older_value_so_an_unmarked_heart_stays_unmarked()
    {
        var marked = Record("h:a", ("fav", "true", 1));
        var unmarked = Record("h:a", ("fav", null, 2));

        var merged = SyncMerge.Merge(marked, unmarked);

        Assert.Null(merged.Fields["fav"].Value);
        Assert.Empty(merged.Values);
    }

    [Fact]
    public void A_deletion_wins_over_older_changes_and_a_later_change_brings_the_record_back()
    {
        var collection = Record("c:1", ("name", "\"Campaign\"", 1));
        var deleted = collection with { DeletedUtc = At(2) };

        Assert.True(SyncMerge.Merge(collection, deleted).IsDeleted);
        Assert.True(SyncMerge.Merge(deleted, collection).IsDeleted);

        var renamedLater = Record("c:1", ("name", "\"Campaign notes\"", 3));
        Assert.False(SyncMerge.Merge(deleted, renamedLater).IsDeleted);
    }

    [Fact]
    public void Merging_is_idempotent_and_order_does_not_matter()
    {
        var a = Record("h:a", ("m.title", "\"A\"", 1), ("fav", "true", 4));
        var b = Record("h:a", ("m.title", "\"B\"", 2), ("note", "\"n\"", 1)) with { DeletedUtc = At(3) };
        var c = Record("h:a", ("note", "\"later\"", 5));

        var abc = SyncMerge.Merge(SyncMerge.Merge(a, b), c);
        var cba = SyncMerge.Merge(SyncMerge.Merge(c, b), a);

        Assert.True(abc.SameAs(cba));
        Assert.True(SyncMerge.Merge(abc, abc).SameAs(abc));
        Assert.True(SyncMerge.Merge(abc, b).SameAs(abc));
    }

    [Fact]
    public void Equal_times_with_different_values_settle_the_same_way_on_every_PC()
    {
        var x = Record("h:a", ("m.title", "\"Alpha\"", 1));
        var y = Record("h:a", ("m.title", "\"Beta\"", 1));

        Assert.Equal(SyncMerge.Merge(x, y).Fields["m.title"], SyncMerge.Merge(y, x).Fields["m.title"]);
    }

    [Fact]
    public void Stamping_times_only_the_fields_that_changed_and_clears_those_that_went()
    {
        var known = Record("h:a", ("m.title", "\"Same\"", 1), ("fav", "true", 1), ("note", "\"old\"", 1));

        var stamped = SyncMerge.Stamp(known, Values(("m.title", "\"Same\""), ("note", "\"new\"")), At(10));

        Assert.Equal(At(1), stamped.Fields["m.title"].ChangedUtc);
        Assert.Equal(new SyncField("\"new\"", At(10)), stamped.Fields["note"]);
        Assert.Equal(new SyncField(null, At(10)), stamped.Fields["fav"]);
    }

    [Fact]
    public void Stamping_what_is_already_known_changes_nothing()
    {
        var known = Record("c:1", ("name", "\"Campaign\"", 1));

        Assert.Same(known, SyncMerge.Stamp(known, Values(("name", "\"Campaign\"")), At(10)));
    }

    [Fact]
    public void A_change_made_on_a_PC_whose_clock_is_behind_still_wins_over_the_value_it_replaced()
    {
        // The other PC's clock was ahead: its change is stamped later than this PC's now.
        var known = Record("h:a", ("m.title", "\"From the fast PC\"", 30));

        var stamped = SyncMerge.Stamp(known, Values(("m.title", "\"Edited here\"")), At(10));

        Assert.True(stamped.Fields["m.title"].ChangedUtc > At(30));
        Assert.Equal("\"Edited here\"", SyncMerge.Merge(known, stamped).Fields["m.title"].Value);
    }

    [Fact]
    public void A_record_that_comes_back_after_its_deletion_outlives_the_tombstone()
    {
        var deleted = Record("c:1", ("name", "\"Campaign\"", 1)) with { DeletedUtc = At(5) };

        var back = SyncMerge.Stamp(deleted, Values(("name", "\"Campaign\"")), At(3));

        Assert.False(back.IsDeleted);
        Assert.False(SyncMerge.Merge(deleted, back).IsDeleted);
    }

    [Fact]
    public void A_deletion_on_this_PC_comes_after_every_change_it_knows_of()
    {
        var known = Record("c:1", ("name", "\"Campaign\"", 30));

        var deleted = SyncMerge.Delete(known, At(10));

        Assert.True(deleted.IsDeleted);
        Assert.True(SyncMerge.Merge(known, deleted).IsDeleted);
    }

    [Fact]
    public async Task A_sync_file_reads_back_as_written()
    {
        var records = new[]
        {
            Record("h:" + new string('a', 64), ("m.title", SyncJson.Value("Crypt of the “Ash” King\nPart 2"), 1), ("fav", null, 2)) with
            {
                Aliases = ["h:" + new string('b', 64)],
            },
            Record("c:7f0c3a2e-5b1d-4c9a-8e2f-0a1b2c3d4e5f", ("name", SyncJson.Value("Café \"quotes\" \\ slash"), 3)) with { DeletedUtc = At(4) },
            Record("bi:00000000-0000-0000-0000-000000000001", ("pages", SyncJson.Value(new { Hash = "abc", First = 0, Last = 2 }), 5)),
        };
        using var stream = new MemoryStream();
        await SyncFile.WriteAsync(stream, "DESKTOP-1", Guid.Parse("11111111-2222-3333-4444-555555555555"), "0.4.0", At(9), records,
            TestContext.Current.CancellationToken);

        stream.Position = 0;
        var read = await SyncFile.ReadAsync(stream, TestContext.Current.CancellationToken);

        Assert.Equal("DESKTOP-1", read.Machine);
        Assert.Equal(Guid.Parse("11111111-2222-3333-4444-555555555555"), read.MachineId);
        Assert.Equal(At(9), read.WrittenUtc);
        Assert.Equal(records.Length, read.Records.Count);
        foreach (var record in records)
        {
            var back = read.Records.Single(r => r.Key == record.Key);
            Assert.True(back.SameAs(record), $"{record.Key} didn't read back the same.");
            Assert.Equal(record.Aliases, back.Aliases);
        }
    }

    [Fact]
    public async Task A_sync_file_from_a_newer_Bibliotaph_or_something_else_is_refused()
    {
        var newer = new MemoryStream(Encoding.UTF8.GetBytes("""{"format":"bibliotaph-sync","formatVersion":99,"records":[]}"""));
        var other = new MemoryStream(Encoding.UTF8.GetBytes("""{"format":"bibliotaph-catalog","formatVersion":1}"""));
        var broken = new MemoryStream(Encoding.UTF8.GetBytes("""{"format":"bibl"""));

        await Assert.ThrowsAsync<InvalidDataException>(() => SyncFile.ReadAsync(newer, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => SyncFile.ReadAsync(other, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => SyncFile.ReadAsync(broken, TestContext.Current.CancellationToken));
    }
}
