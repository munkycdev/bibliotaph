using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Bibliotaph.Api.Contracts;
using Bibliotaph.Index;
using Bibliotaph.Processing;

namespace Bibliotaph.App.Services;

/// <summary>
/// bibliotaph.online for the smoke test: a web library with one folder holding one made-up book, which it has read
/// already, so a connected PC lists and searches the book without downloading it and downloads it only to open it.
/// Anything else it's asked, such as library sync, is unavailable, as when the server is down.
/// </summary>
sealed class SmokeWebLibrary : HttpMessageHandler
{
    public const string Title = "The Glass Orchard";

    /// <summary>What its second page says, which a search finds before the book is ever downloaded.</summary>
    public const string Phrase = "orchard of glass bells";

    public const long FolderId = 70;

    public static byte[] Book { get; } = SmokePdfs.Plain(Title,
        [[Title, "A made-up book for the smoke test."], ["The orchard", $"An {Phrase} rings at dusk."]]);

    public static string Hash { get; } = Convert.ToHexStringLower(SHA256.HashData(Book));

    static readonly WebShelf Shelf = new("smoke-1",
        [new WebShelfFolder(FolderId, "RPG", "OneDrive · Smoke › RPG",
            [new WebShelfFile($"Adventures/{Title}.pdf", Hash, Book.Length, new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc))])]);

    static readonly SharedReading Reading = new("pdf", Title, 2, null, null, false, true, Title, null, null, null, "Bibliotaph smoke fixture",
        "None", """{"canCopy":true,"canPrint":true,"encrypted":false}""", null,
        [
            new StoredPage(0, null, 612, 792, $"{Title}\nA made-up book for the smoke test.", "pdf", 0.95, false, null),
            new StoredPage(1, null, 612, 792, $"The orchard\nAn {Phrase} rings at dusk.", "pdf", 0.95, false, null),
        ],
        [], "Complete", null);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var get = request.Method == HttpMethod.Get;
        HttpResponseMessage response =
            get && path == "/api/devices/me" ? Json(new DeviceIdentity("smoke-pc", "Smoke PC", "Smoke"))
            : request.Method == HttpMethod.Delete && path == "/api/devices/me" ? new(HttpStatusCode.NoContent)
            : get && path == "/api/pc/library" ? Json(Shelf)
            : get && path == $"/api/pc/books/{Hash}/reading" ? Json(Reading)
            : get && path == $"/api/pc/books/{Hash}/file" ? new(HttpStatusCode.OK) { Content = new ByteArrayContent(Book) }
            : new(HttpStatusCode.ServiceUnavailable);
        response.RequestMessage = request;
        return Task.FromResult(response);
    }

    static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
}
