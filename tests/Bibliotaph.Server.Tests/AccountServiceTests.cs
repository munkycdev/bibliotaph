using Bibliotaph.Server.Accounts;

namespace Bibliotaph.Server.Tests;

public sealed class AccountServiceTests : IDisposable
{
    readonly TempFolder _folder = new();
    readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));
    readonly AccountsDatabase _database;
    readonly AccountService _accounts;

    static readonly ExternalIdentity Dana = new("microsoft", "oid-1@tenant-1", "Dana", "dana@example.com");
    static readonly ExternalIdentity DanaOnGoogle = new("google", "google-sub-1", "Dana", "dana@example.org");

    public AccountServiceTests()
    {
        _database = new AccountsDatabase(Path.Combine(_folder.Path, "accounts.db"));
        _database.MigrateAsync().GetAwaiter().GetResult();
        _accounts = new AccountService(_database, _clock);
    }

    public void Dispose()
    {
        _database.ClearPool();
        _folder.Dispose();
    }

    [Fact]
    public async Task An_invitation_makes_an_account_once()
    {
        var (code, _) = await _accounts.CreateInviteAsync(null, "Dana", admin: false);

        var first = await _accounts.SignInAsync(Dana, code, null);
        var again = await _accounts.SignInAsync(DanaOnGoogle, code, null);

        Assert.Equal(SignInResult.Created, first.Result);
        Assert.Equal("Dana", first.User!.Name);
        Assert.False(first.User.IsAdmin);
        Assert.Equal(SignInResult.InviteNotValid, again.Result);
    }

    [Fact]
    public async Task A_known_sign_in_needs_no_invitation()
    {
        var (code, _) = await _accounts.CreateInviteAsync(null, null, admin: false);
        var created = await _accounts.SignInAsync(Dana, code, null);

        var later = await _accounts.SignInAsync(Dana, null, null);

        Assert.Equal(SignInResult.SignedIn, later.Result);
        Assert.Equal(created.User!.Id, later.User!.Id);
    }

    [Fact]
    public async Task Without_an_invitation_a_stranger_is_turned_away()
    {
        Assert.Equal(SignInResult.NeedsInvite, (await _accounts.SignInAsync(Dana, null, null)).Result);
        Assert.Equal(SignInResult.InviteNotValid, (await _accounts.SignInAsync(Dana, "not-a-code", null)).Result);
    }

    [Fact]
    public async Task An_invitation_expires_after_two_weeks()
    {
        var (code, _) = await _accounts.CreateInviteAsync(null, null, admin: false);
        _clock.Advance(AccountService.InviteLifetime + TimeSpan.FromMinutes(1));

        Assert.Equal(SignInResult.InviteNotValid, (await _accounts.SignInAsync(Dana, code, null)).Result);
    }

    [Fact]
    public async Task An_admin_invitation_makes_an_admin()
    {
        var (code, _) = await _accounts.CreateInviteAsync(null, null, admin: true);

        Assert.True((await _accounts.SignInAsync(Dana, code, null)).User!.IsAdmin);
    }

    [Fact]
    public async Task A_second_sign_in_links_to_the_account_signed_in()
    {
        var (code, _) = await _accounts.CreateInviteAsync(null, null, admin: false);
        var user = (await _accounts.SignInAsync(Dana, code, null)).User!;

        var linked = await _accounts.SignInAsync(DanaOnGoogle, null, user.Id);
        var viaGoogle = await _accounts.SignInAsync(DanaOnGoogle, null, null);

        Assert.Equal(SignInResult.Linked, linked.Result);
        Assert.Equal(user.Id, viaGoogle.User!.Id);
        Assert.Equal(2, (await _accounts.LoginsAsync(user.Id)).Count);
    }

    [Fact]
    public async Task A_sign_in_that_belongs_to_someone_else_is_not_moved()
    {
        var (first, _) = await _accounts.CreateInviteAsync(null, null, admin: false);
        var (second, _) = await _accounts.CreateInviteAsync(null, null, admin: false);
        var dana = (await _accounts.SignInAsync(Dana, first, null)).User!;
        var other = (await _accounts.SignInAsync(DanaOnGoogle, second, null)).User!;

        var outcome = await _accounts.SignInAsync(DanaOnGoogle, null, dana.Id);

        Assert.Equal(SignInResult.BelongsToSomeoneElse, outcome.Result);
        Assert.Single(await _accounts.LoginsAsync(other.Id));
    }

    [Fact]
    public async Task A_sign_in_saved_under_a_former_subject_moves_to_the_new_one()
    {
        var (code, _) = await _accounts.CreateInviteAsync(null, null, admin: false);
        var before = new ExternalIdentity("auth0", "oauth2|discord|5678", "Dana", null);
        var user = (await _accounts.SignInAsync(before, code, null)).User!;
        var after = new ExternalIdentity("auth0", "discord|5678", "Dana", null) { FormerSubjects = ["oauth2|discord|5678"] };

        var moved = await _accounts.SignInAsync(after, null, null);
        var again = await _accounts.SignInAsync(after, null, null);

        Assert.Equal(SignInResult.SignedIn, moved.Result);
        Assert.Equal(user.Id, moved.User!.Id);
        Assert.Equal(user.Id, again.User!.Id);
        Assert.Equal("discord|5678", Assert.Single(await _accounts.LoginsAsync(user.Id)).Subject);
        Assert.Equal(SignInResult.NeedsInvite, (await _accounts.SignInAsync(before, null, null)).Result);
    }

    [Fact]
    public async Task The_code_is_redeemed_by_its_hash()
    {
        var (code, _) = await _accounts.CreateInviteAsync(null, null, admin: false);

        var stored = Assert.Single(await _accounts.InvitesAsync());
        Assert.DoesNotContain(code, stored.CodeHash, StringComparison.Ordinal);
        Assert.Equal(64, stored.CodeHash.Length);
        Assert.Equal(code, stored.Code);
    }

    [Fact]
    public async Task An_open_invitations_code_can_be_had_again_until_it_is_used()
    {
        var (code, invite) = await _accounts.CreateInviteAsync(null, "Dana", admin: false);
        Assert.Equal(code, await _accounts.InviteCodeAsync(invite.Id));

        await _accounts.SignInAsync(Dana, code, null);

        Assert.Null(await _accounts.InviteCodeAsync(invite.Id));
        Assert.Null(Assert.Single(await _accounts.InvitesAsync()).Code);
    }

    [Fact]
    public async Task An_expired_invitations_code_is_not_given()
    {
        var (_, invite) = await _accounts.CreateInviteAsync(null, null, admin: false);
        _clock.Advance(AccountService.InviteLifetime + TimeSpan.FromMinutes(1));

        Assert.Null(await _accounts.InviteCodeAsync(invite.Id));
        Assert.Null(await _accounts.InviteCodeAsync("not-an-invitation"));
    }

    [Fact]
    public async Task A_deleted_invitation_no_longer_signs_anyone_up()
    {
        var (code, invite) = await _accounts.CreateInviteAsync(null, null, admin: false);

        Assert.True(await _accounts.DeleteInviteAsync(invite.Id));

        Assert.False(await _accounts.DeleteInviteAsync(invite.Id));
        Assert.Empty(await _accounts.InvitesAsync());
        Assert.Equal(SignInResult.InviteNotValid, (await _accounts.SignInAsync(Dana, code, null)).Result);
    }

    [Fact]
    public async Task Deleting_a_used_invitation_keeps_its_account()
    {
        var (code, invite) = await _accounts.CreateInviteAsync(null, null, admin: false);
        var user = (await _accounts.SignInAsync(Dana, code, null)).User!;

        Assert.True(await _accounts.DeleteInviteAsync(invite.Id));

        Assert.Equal(SignInResult.SignedIn, (await _accounts.SignInAsync(Dana, null, null)).Result);
        Assert.NotNull(await _accounts.FindUserAsync(user.Id));
    }

    [Fact]
    public async Task A_closed_account_is_gone_with_its_sign_ins_and_waits_to_have_its_files_deleted()
    {
        var (code, _) = await _accounts.CreateInviteAsync(null, null, admin: true);
        var user = (await _accounts.SignInAsync(Dana, code, null)).User!;
        Assert.True(await _accounts.IsOpenAsync(user.Id));

        Assert.True(await _accounts.CloseAsync(user.Id));

        Assert.False(await _accounts.IsOpenAsync(user.Id));
        Assert.Null(await _accounts.FindUserAsync(user.Id));
        Assert.Empty(await _accounts.LoginsAsync(user.Id));
        Assert.Equal(SignInResult.NeedsInvite, (await _accounts.SignInAsync(Dana, null, null)).Result);
        Assert.Equal(user.Id, Assert.Single(await _accounts.UnpurgedAsync()).UserId);
        Assert.False(await _accounts.CloseAsync(user.Id));

        await _accounts.MarkPurgedAsync(user.Id);
        Assert.Empty(await _accounts.UnpurgedAsync());
    }

    [Theory]
    [InlineData("abs://stbibliotaph@replica", "https://stbibliotaph.blob.core.windows.net/replica", "")]
    [InlineData("abs://stbibliotaph@replica/prod/", "https://stbibliotaph.blob.core.windows.net/replica", "prod/")]
    public void A_replica_address_names_the_container_Litestream_writes_to(string url, string container, string root)
    {
        var (uri, path) = BlobUserBlobs.Replica(url);

        Assert.Equal(container, uri.ToString());
        Assert.Equal(root, path);
    }

    [Fact]
    public async Task Open_sign_up_makes_accounts_without_an_invitation_until_the_server_is_full()
    {
        var open = new AccountService(_database, _clock, new SignUpOptions { Open = true, MostAccounts = 2 });
        var (code, _) = await open.CreateInviteAsync(null, null, admin: true);

        var first = await open.SignInAsync(Dana, null, null);
        var second = await open.SignInAsync(new ExternalIdentity("google", "sub-2", "Ana", null), null, null);
        var full = await open.SignInAsync(new ExternalIdentity("google", "sub-3", "Dale", null), null, null);
        var invited = await open.SignInAsync(new ExternalIdentity("google", "sub-4", "Sam", null), code, null);

        Assert.Equal(SignInResult.Created, first.Result);
        Assert.False(first.User!.IsAdmin);
        Assert.Equal(SignInResult.Created, second.Result);
        Assert.Equal(SignInResult.Full, full.Result);
        // An invitation makes an account however full sign-up is.
        Assert.Equal(SignInResult.Created, invited.Result);
        Assert.True(invited.User!.IsAdmin);
        Assert.Equal(SignInResult.NeedsInvite, (await _accounts.SignInAsync(new ExternalIdentity("google", "sub-5", "Lee", null), null, null)).Result);
    }
}
