using System.Text;
using Bibliotaph.Storage;
using Bibliotaph.Storage.Dropbox;
using Bibliotaph.Storage.OneDrive;

namespace Bibliotaph.Server.Tests;

/// <summary>
/// The change logs a PC asks instead of hearing webhooks: OneDrive's delta and Dropbox's cursor, through the kinds, so the
/// web server's own drives stay on notifications.
/// </summary>
public sealed class StorageChangeLogTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static DriveContext Context(HttpMessageHandler handler, string account, string? kind = null) =>
        new(new HttpClient(handler, disposeHandler: false), account, kind, _ => Task.FromResult("access"), _ => Task.CompletedTask, TimeProvider.System);

    [Fact]
    public async Task OneDrive_hears_changes_after_its_start_and_starts_again_when_its_link_expires()
    {
        var graph = new FakeGraph();
        graph.Add("RPG/one.pdf", Encoding.ASCII.GetBytes("%PDF one"));
        var kind = new OneDriveKind();
        var context = Context(graph, FakeGraph.DriveId);
        var drive = kind.Drive(context);
        Assert.IsNotAssignableFrom<IStorageChangeLog>(drive);
        var log = kind.ChangeLog(drive, context);

        var start = await log.StartAsync(Ct);
        var (quiet, same) = await log.ChangesSinceAsync(start, Ct);
        Assert.False(quiet);

        graph.Add("RPG/two.pdf", Encoding.ASCII.GetBytes("%PDF two"));
        var (changed, next) = await log.ChangesSinceAsync(same, Ct);
        Assert.True(changed);
        Assert.False((await log.ChangesSinceAsync(next, Ct)).Changed);

        var (expired, fresh) = await log.ChangesSinceAsync($"https://graph.microsoft.com/v1.0/drives/{FakeGraph.DriveId}/root/delta?token=expired", Ct);
        Assert.True(expired);
        Assert.False((await log.ChangesSinceAsync(fresh, Ct)).Changed);
    }

    [Fact]
    public async Task Dropbox_hears_changes_after_its_start_and_starts_again_when_its_cursor_is_reset()
    {
        var dropbox = new FakeDropbox();
        dropbox.Add("RPG/one.pdf", Encoding.ASCII.GetBytes("%PDF one"));
        var kind = new DropboxKind();
        var context = Context(dropbox, FakeDropbox.AccountId, "personal");
        var drive = kind.Drive(context);
        Assert.IsNotAssignableFrom<IStorageChangeLog>(drive);
        var log = kind.ChangeLog(drive, context);

        var start = await log.StartAsync(Ct);
        Assert.False((await log.ChangesSinceAsync(start, Ct)).Changed);

        dropbox.Add("RPG/two.pdf", Encoding.ASCII.GetBytes("%PDF two"));
        var (changed, next) = await log.ChangesSinceAsync(start, Ct);
        Assert.True(changed);
        Assert.False((await log.ChangesSinceAsync(next, Ct)).Changed);

        var (reset, fresh) = await log.ChangesSinceAsync("reset|", Ct);
        Assert.True(reset);
        Assert.False((await log.ChangesSinceAsync(fresh, Ct)).Changed);
    }

    [Fact]
    public void Kinds_without_a_change_log_of_their_own_use_their_drive()
    {
        var webDav = StorageKinds.Find("webdav")!;
        var context = Context(new FakeWebDav(), "account");
        var drive = webDav.Drive(context);
        Assert.Same(drive, webDav.ChangeLog(drive, context));
    }
}
