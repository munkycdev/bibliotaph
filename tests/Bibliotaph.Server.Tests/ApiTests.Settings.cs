using System.Net;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Catalog;
using Bibliotaph.Pdf.Host.Tests;
using Bibliotaph.Server.Libraries;
using Bibliotaph.Server.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bibliotaph.Server.Tests;

public abstract partial class ApiTests
{
    /// <summary>Settings, backups and exports.</summary>
    public sealed class SettingsTests(SyntheticPdfs pdfs) : ApiTests(pdfs)
    {
        [Fact]
        public async Task A_backup_and_both_exports_download_named_for_the_users_day_without_the_servers_sealed_keys()
        {
            var ct = TestContext.Current.CancellationToken;
            var user = await AddUserAsync(admin: false);
            await using (var lease = await _factory.Services.GetRequiredService<UserLibraries>().OpenAsync(user.Id, ct))
                await lease.Library.Get<SettingsStore>().SetAsync(SettingKeys.ServerAiKeys, "{\"https://api.example.com/v1\":\"CfDJ8-sealed-key\"}", ct);
            var client = Client(user);

            using var backup = await client.GetAsync("/api/library/backup?today=2026-10-26", ct);
            using var csv = await client.GetAsync("/api/library/export/csv?today=2026-10-26", ct);
            using var json = await client.GetAsync("/api/library/export/json", ct);

            Assert.Equal("Bibliotaph backup 2026-10-26.zip", backup.Content.Headers.ContentDisposition!.FileNameStar);
            using var folder = new TempFolder();
            await using (var zipped = await backup.Content.ReadAsStreamAsync(ct))
                System.IO.Compression.ZipFile.ExtractToDirectory(zipped, folder.Path);
            Assert.True(File.Exists(Path.Combine(folder.Path, "manifest.json")));
            await using (var catalog = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(folder.Path, "catalog.db")};Pooling=False"))
            {
                await catalog.OpenAsync(ct);
                await using var count = catalog.CreateCommand();
                count.CommandText = "SELECT COUNT(*) FROM setting WHERE key = 'server.ai-keys'";
                Assert.Equal(0L, (long)(await count.ExecuteScalarAsync(ct))!);
            }
            Assert.Equal("Bibliotaph library 2026-10-26.csv", csv.Content.Headers.ContentDisposition!.FileNameStar);
            Assert.StartsWith("Title,", (await csv.Content.ReadAsStringAsync(ct)).TrimStart('\uFEFF'), StringComparison.Ordinal);
            var exported = await json.Content.ReadAsStringAsync(ct);
            Assert.Contains("\"format\": \"bibliotaph-catalog\"", exported, StringComparison.Ordinal);
            Assert.DoesNotContain("CfDJ8-sealed-key", exported, StringComparison.Ordinal);
        }

        [Fact]
        public async Task A_weekly_backup_is_kept_and_restoring_it_brings_the_library_back_as_it_was()
        {
            var ct = TestContext.Current.CancellationToken;
            var user = await AddUserAsync(admin: false);
            var client = Client(user);
            client.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");
            var store = _factory.Services.GetRequiredService<IBackupStore>();
            var backups = _factory.Services.GetRequiredService<WebBackups>();
            var libraries = _factory.Services.GetRequiredService<UserLibraries>();
            // Four weekly backups from a year ago: the oldest goes when this week's is made.
            var old = new DateTimeOffset(2025, 10, 6, 9, 0, 0, TimeSpan.Zero);
            for (var week = 0; week < 4; week++)
                await store.UploadAsync(user.Id, BackupNames.For(BackupNames.Weekly, old.AddDays(7 * week)), new MemoryStream([1, 2, 3]), ct);

            KeptBackup? empty, made, again;
            await using (var lease = await libraries.OpenAsync(user.Id, ct)) empty = await backups.BackUpWeeklyIfDueAsync(lease.Library, ct);
            using (await client.PostAsJsonAsync("/api/library/elsewhere", new ElsewhereAdd("Tomb of the Test", "", "", "", [], "", null), ct)) { }
            await using (var lease = await libraries.OpenAsync(user.Id, ct))
            {
                made = await backups.BackUpWeeklyIfDueAsync(lease.Library, ct);
                again = await backups.BackUpWeeklyIfDueAsync(lease.Library, ct);
            }
            var kept = (await client.GetFromJsonAsync<List<KeptBackupInfo>>("/api/library/backups", ct))!;
            using var download = await client.GetAsync($"/api/library/backups/{made!.Name}", ct);
            using var notKept = await client.GetAsync("/api/library/backups/catalog.db", ct);

            using (await client.PostAsJsonAsync("/api/library/elsewhere", new ElsewhereAdd("Vault of the Second", "", "", "", [], "", null), ct)) { }
            using var read = await client.PostAsync($"/api/library/restore?backup={made.Name}", null, ct);
            var preview = (await read.Content.ReadFromJsonAsync<RestoreRead>(ct))!.Preview!;
            using var restore = await client.PostAsync($"/api/library/restore/{preview.Token}", null, ct);
            using var twice = await client.PostAsync($"/api/library/restore/{preview.Token}", null, ct);
            var after = (await client.GetFromJsonAsync<LibraryResults>("/api/library", ct))!;
            var keptAfter = (await client.GetFromJsonAsync<List<KeptBackupInfo>>("/api/library/backups", ct))!;

            using var notABackup = await client.PostAsync("/api/library/restore", new ByteArrayContent([1, 2, 3]), ct);
            var refused = (await notABackup.Content.ReadFromJsonAsync<RestoreRead>(ct))!;
            using var current = await client.GetAsync("/api/library/backup", ct);
            using var upload = await client.PostAsync("/api/library/restore", new StreamContent(await current.Content.ReadAsStreamAsync(ct)), ct);
            var uploaded = (await upload.Content.ReadFromJsonAsync<RestoreRead>(ct))!.Preview!;
            using var cancel = await client.DeleteAsync($"/api/library/restore/{uploaded.Token}", ct);
            using var cancelled = await client.PostAsync($"/api/library/restore/{uploaded.Token}", null, ct);

            Assert.Null(empty);
            Assert.NotNull(made);
            Assert.Null(again);
            Assert.Equal(4, kept.Count);
            Assert.All(kept, b => Assert.True(b.Weekly));
            Assert.Equal(made.Name, kept[0].Name);
            Assert.DoesNotContain(kept, b => b.Name == BackupNames.For(BackupNames.Weekly, old));
            Assert.StartsWith("Bibliotaph weekly backup ", download.Content.Headers.ContentDisposition!.FileNameStar, StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.NotFound, notKept.StatusCode);
            Assert.Equal(1, preview.Books);
            Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
            Assert.Equal(["Tomb of the Test"], after.Books.Select(b => b.Title));
            Assert.Contains(keptAfter, b => !b.Weekly && b.Name.StartsWith(BackupNames.BeforeRestore, StringComparison.Ordinal));
            Assert.Null(refused.Preview);
            Assert.NotNull(refused.Problem);
            Assert.Equal(1, uploaded.Books);
            Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, cancelled.StatusCode);
        }

        [Fact]
        public async Task Settings_library_chooses_the_start_page_and_switches_a_folder_name_off()
        {
            var ct = TestContext.Current.CancellationToken;
            var user = await AddUserAsync(admin: false);
            var connections = _factory.Services.GetRequiredService<StorageConnections>();
            var drive = await connections.SaveAsync(user.Id, OneDriveProvider.Key, FakeGraph.DriveId, "OneDrive · Dana", "personal", "refresh", null, ct);
            _graph.Add("RPG/Adventures/IMG_0042.png", Png());
            var client = Client(user);
            client.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");
            using (await client.PostAsJsonAsync("/api/library/folders", new AddLibraryFolder(drive.Id, "RPG"), ct)) { }
            await WaitAsync(async () => (await client.GetFromJsonAsync<LibraryResults>("/api/library", ct))!.Books is { Count: 1 } one ? one : null, ct);

            var before = (await client.GetFromJsonAsync<LibraryPreferences>("/api/settings/library", ct))!;
            using (await client.PutAsJsonAsync("/api/settings/start", new StartPageChoice(true), ct)) { }
            var label = before.FolderNames.Single(l => l.Folder == "Adventures");
            using var switched = await client.PutAsJsonAsync("/api/settings/folder-names", new FolderNameSwitch(label.Folder, label.Vocabulary, label.Term, false), ct);
            var after = (await switched.Content.ReadFromJsonAsync<LibraryPreferences>(ct))!;

            Assert.False(before.StartOnLibrary);
            Assert.Equal(("Type: Adventure", "1 document under a folder with this name", true), (label.Meaning, label.Detail, label.Enabled));
            Assert.True((await client.GetFromJsonAsync<StartPageChoice>("/api/settings/start", ct))!.Library);
            Assert.True(after.StartOnLibrary);
            Assert.False(after.FolderNames.Single(l => l.Folder == "Adventures").Enabled);
            Assert.Empty(after.FolderRules);
        }

        [Fact]
        public async Task Settings_vocabulary_adds_renames_and_names_a_term_and_keeps_the_users_own_tags()
        {
            var ct = TestContext.Current.CancellationToken;
            var client = Client(await AddUserAsync(admin: false));
            client.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");

            using var added = await client.PostAsJsonAsync("/api/vocabulary/type", new NewTerm("Hexcrawl"), ct);
            var termId = (await added.Content.ReadFromJsonAsync<VocabularyChange>(ct))!.TermId!.Value;
            using (await client.PutAsJsonAsync($"/api/vocabulary/terms/{termId}", new TermRename("Hex crawl", "Hex"), ct)) { }
            using var named = await client.PostAsJsonAsync($"/api/vocabulary/terms/{termId}/aliases", new NewAlias("pointcrawl"), ct);
            var term = (await client.GetFromJsonAsync<List<VocabularyTerm>>("/api/vocabulary/type", ct))!.Single(t => t.Id == termId);
            using var unknown = await client.GetAsync("/api/vocabulary/nonsense", ct);

            Assert.Null((await named.Content.ReadFromJsonAsync<VocabularyChange>(ct))!.Problem);
            Assert.Equal(("Hex crawl", "Hex", true), (term.Label, term.ShortLabel, term.Yours));
            var alias = Assert.Single(term.Aliases, a => a.Text == "pointcrawl");
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            using (await client.DeleteAsync($"/api/vocabulary/aliases/{alias.Id}", ct)) { }
            Assert.DoesNotContain((await client.GetFromJsonAsync<List<VocabularyTerm>>("/api/vocabulary/type", ct))!.Single(t => t.Id == termId).Aliases,
                a => a.Text == "pointcrawl");

            using var saved = await client.PostAsJsonAsync("/api/tags", new UserTagText("Undead", "zombie, lich"), ct);
            var tag = (await saved.Content.ReadFromJsonAsync<UserTagChange>(ct))!.Tag!;
            using var refused = await client.PostAsJsonAsync("/api/tags", new UserTagText("Ghosts", " "), ct);
            using (await client.PutAsJsonAsync($"/api/tags/{tag.Id}", new UserTagText("The undead", "zombie, lich, ghoul"), ct)) { }
            var listed = (await client.GetFromJsonAsync<List<UserTag>>("/api/tags", ct))!.Single();
            using var deleted = await client.DeleteAsync($"/api/tags/{tag.Id}", ct);
            using var again = await client.DeleteAsync($"/api/tags/{tag.Id}", ct);

            Assert.Equal(["zombie", "lich"], tag.Words);
            Assert.Equal(0, tag.Books);
            Assert.NotNull((await refused.Content.ReadFromJsonAsync<UserTagChange>(ct))!.Problem);
            Assert.Equal(("The undead", 3), (listed.Name, listed.Words.Count));
            Assert.Equal((HttpStatusCode.NoContent, HttpStatusCode.NotFound), (deleted.StatusCode, again.StatusCode));
        }

        [Fact]
        public async Task Settings_processing_pauses_a_lane_and_keeps_it_paused_when_the_library_opens_again()
        {
            var ct = TestContext.Current.CancellationToken;
            var user = await AddUserAsync(admin: false);
            var client = Client(user);
            client.DefaultRequestHeaders.Add(ApiHeaders.Client, "1");
            var libraries = _factory.Services.GetRequiredService<UserLibraries>();

            var before = (await client.GetFromJsonAsync<ProcessingState>("/api/processing", ct))!;
            using var paused = await client.PutAsJsonAsync("/api/processing/paused", new ProcessingPauses(false, false, true), ct);
            var after = (await paused.Content.ReadFromJsonAsync<ProcessingState>(ct))!;
            await libraries.CloseIdleAsync(TimeSpan.Zero);
            var reopened = (await client.GetFromJsonAsync<ProcessingState>("/api/processing", ct))!;

            Assert.Equal(("Nothing to process", false, new ProcessingPauses(false, false, false)), (before.Summary, before.HasFolders, before.Paused));
            Assert.Equal(new ProcessingPauses(false, false, true), after.Paused);
            Assert.Equal(new ProcessingPauses(false, false, true), reopened.Paused);
            Assert.Empty(reopened.Now);
        }

        [Fact]
        public async Task Settings_lists_what_each_connection_was_granted_in_plain_words()
        {
            var ct = TestContext.Current.CancellationToken;
            var user = await AddUserAsync(admin: false);
            var connections = _factory.Services.GetRequiredService<StorageConnections>();
            await connections.SaveAsync(user.Id, DropboxProvider.Key, FakeDropbox.AccountId, "Dropbox · Dana", "personal", "refresh",
                "account_info.read files.metadata.read files.content.read", ct);
            await connections.SaveAsync(user.Id, OneDriveProvider.Key, "drive", "OneDrive · Dana", "personal", "refresh",
                "Files.Read openid profile email offline_access Sites.Read.All", ct);

            var accounts = (await Client(user).GetFromJsonAsync<List<StorageAccount>>("/api/storage", ct))!.ToDictionary(a => a.Provider);

            var dropbox = accounts[DropboxProvider.Key];
            Assert.Equal(["see your name", "see your file and folder names", "read your files", "keep access while you're signed out"], dropbox.Access!);
            Assert.Equal("account_info.read files.metadata.read files.content.read", dropbox.Scopes);
            // Anything granted that Bibliotaph has no words for is still listed, by its own name.
            Assert.Equal(["read your files", "keep access while you're signed out", "Sites.Read.All"], accounts[OneDriveProvider.Key].Access!);
        }
    }
}
