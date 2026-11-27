using System.Security.Cryptography;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Catalog;
using Bibliotaph.Core;
using Bibliotaph.Index;

namespace Bibliotaph.Processing.Tests;

public abstract partial class PipelineTests
{
    public sealed partial class WebLibraryTests
    {
        static SharedReading Titled(string title) => new("pdf", title, 1, null, null, false, true, title, "Ana Ruiz", null, null, null, "None",
            """{"canCopy":true,"canPrint":true,"encrypted":false}""", null, [new StoredPage(0, "1", 612, 792, $"The pages of {title}", "pdf", 0.95, false, null)],
            [], "Complete", null);

        [Fact]
        public async Task Favorites_and_binders_are_kept_offline_once_and_let_go_when_they_no_longer_need_them()
        {
            // Three books only the web library has, and one in a cloud folder on this PC.
            var names = new[] { "Lantern", "Harbor", "Crypt" };
            var hashes = new Dictionary<string, string>();
            var bytes = new Dictionary<string, byte[]>();
            foreach (var (name, i) in names.Select((n, i) => (n, i)))
            {
                bytes[name] = Enumerable.Repeat((byte)(i + 1), 500 + i).ToArray();
                hashes[name] = Convert.ToHexStringLower(SHA256.HashData(bytes[name]));
                _web.Readings[hashes[name]] = Titled($"The {name}");
                if (name != "Crypt") _web.Files[hashes[name]] = bytes[name];
            }
            _web.Shelf = new WebShelf("v1", [Folder(7, "RPG", [.. names.Select(n => new WebShelfFile($"{n}.pdf", hashes[n], bytes[n].Length, Modified))])]);
            Copy(_pdfs.WatermarkedForAna, "Abbey.pdf");
            var local = Path.Combine(_library, "Abbey.pdf");
            _cloud.CloudPaths.Add(local);
            await _roots.AddAsync(_library, Ct);
            await _service.StartAsync(Ct);
            await Sync().SyncNowAsync(Ct);
            await SettleAsync();
            async Task<EntryId> CardAsync(string hash) =>
                (await _entries.GetCardAsync((await _libraryStore.FindDocumentAsync(ContentHash.Parse(hash), Ct))!.Value, Ct))!.Value;
            var (lantern, harbor, crypt) = (await CardAsync(hashes["Lantern"]), await CardAsync(hashes["Harbor"]), await CardAsync(hashes["Crypt"]));
            var abbeyDocument = (await _libraryStore.GetRelativePathsAsync(Ct)).Single(p => p.RelativePath == "Abbey.pdf").DocumentId;
            var abbey = (await _entries.GetCardAsync(abbeyDocument, Ct))!.Value;

            var favorites = new FavoritesService(_favorites, _entries, _projector);
            var sessions = new SessionsService(_sessions, _entries, _libraryStore, _queries, _projector, _places);
            var keeper = new OfflineKeeper(new OfflineBooks(_favorites, _entries, sessions), _libraryStore, _webCopies, _cloud, _settings, new DiskSpace(),
                favorites, sessions);
            await favorites.SetAsync([lantern, abbey], favorite: true, Ct);
            var binder = await sessions.CreateAsync("Night one", ct: Ct);
            await sessions.AddAsync(binder.Id, [new NewSessionItem(harbor), new NewSessionItem(crypt)], ct: Ct);

            await keeper.KeepNowAsync(Ct);
            var first = keeper.Status;
            var held = new[] { "Lantern", "Harbor" }.Select(n => _webCopies.Has(hashes[n])).ToArray();
            var why = (keeper.WhyKept(hashes["Lantern"]), keeper.WhyKept(hashes["Harbor"]));

            // The one that couldn't be had waits for Download now, rather than being tried on every pass.
            _web.Files[hashes["Crypt"]] = bytes["Crypt"];
            await keeper.KeepNowAsync(Ct);
            var untried = keeper.Status;
            keeper.Refresh();
            await keeper.KeepNowAsync(Ct);
            var retried = keeper.Status;
            var downloads = _web.Downloads;

            // Unfavorited, a book joins the other downloads, and the cloud file goes back to its sync app.
            await favorites.SetAsync([lantern, abbey], favorite: false, Ct);
            await keeper.KeepNowAsync(Ct);
            var lanternHeld = await _webCopies.IsPreparedAsync(hashes["Lantern"], Ct);
            var harborHeld = await _webCopies.IsPreparedAsync(hashes["Harbor"], Ct);
            var afterUnfavorite = keeper.Status;

            await keeper.SetOnAsync(false, Ct);
            await keeper.KeepNowAsync(Ct);
            var off = keeper.Status;

            Assert.Equal(new OfflineKeepStatus(true, 4, 3, 0, 1, first.Bytes, false), first);
            Assert.True(first.Bytes > 1000);
            Assert.Equal([true, true], held);
            Assert.Equal(("a favorite", "in the binder “Night one”"), why);
            Assert.Equal((3, 1), (untried.Ready, untried.Failed));
            Assert.Equal((4, 0), (retried.Ready, retried.Failed));
            Assert.Equal(3, downloads);
            Assert.Equal([local], _cloud.KeptOnDevice);
            Assert.Equal([local], _cloud.LetGo);
            Assert.False(lanternHeld);
            Assert.True(_webCopies.Has(hashes["Lantern"]));
            Assert.True(harborHeld);
            Assert.Equal((2, 2), (afterUnfavorite.Books, afterUnfavorite.Ready));
            Assert.False(off.On);
            Assert.False(await _webCopies.IsPreparedAsync(hashes["Harbor"], Ct));
            Assert.Equal(3, _web.Downloads);
        }
    }
}
