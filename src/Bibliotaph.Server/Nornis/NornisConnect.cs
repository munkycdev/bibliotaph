using System.Globalization;
using Bibliotaph.Catalog;
using Bibliotaph.Processing;
using Bibliotaph.Server.Auth;
using Bibliotaph.Server.Libraries;
using Bibliotaph.Server.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace Bibliotaph.Server.Nornis;

/// <summary>
/// Connect Nornis, which a game master starts and ends in Nornis: /connect/nornis signs them in through Auth0 (silent,
/// as both apps share the tenant), connects the nornis storage provider, makes the Nornis collection and sends the
/// browser back to Nornis's return address with ?bibliotaph=connected, full or failed. Only an address on Nornis's own
/// site is ever redirected to.
/// </summary>
public static class NornisConnect
{
    /// <summary>The sign-in's item that marks it as Connect Nornis, so an unknown subject makes an account.</summary>
    public const string ConnectItem = "connect";

    /// <summary>Nornis's return address, carried through the sign-in and the storage grant.</summary>
    public const string ReturnItem = "nornisReturn";

    public const string Connected = "connected";
    public const string Full = "full";
    public const string Failed = "failed";

    public static void MapNornisConnect(this IEndpointRouteBuilder app)
    {
        var connect = app.MapGroup("/connect/nornis");

        connect.MapGet("", ([FromQuery(Name = "return")] string? returnTo, IConfiguration configuration) =>
        {
            if (Ready(configuration) is not { } nornis) return Results.NotFound();
            if (!nornis.IsOwn(returnTo)) return Results.BadRequest();
            var properties = new OpenIdConnectChallengeProperties { RedirectUri = "/auth/complete" };
            properties.Items[ConnectItem] = NornisProvider.Key;
            properties.Items[ReturnItem] = returnTo;
            properties.Items["returnUrl"] = Step("storage", returnTo!);
            return Results.Challenge(properties, [Auth0SignIn.Id]);
        }).AllowAnonymous();

        // Granted again even when connected already: it's silent, and it ties the connection to whoever Nornis has signed in.
        connect.MapGet("/storage", ([FromQuery(Name = "return")] string? returnTo, IConfiguration configuration) =>
        {
            if (Ready(configuration) is not { } nornis) return Results.NotFound();
            if (!nornis.IsOwn(returnTo)) return Results.BadRequest();
            var properties = new AuthenticationProperties { RedirectUri = Step("finish", returnTo!) };
            properties.Items[ReturnItem] = returnTo;
            return Results.Challenge(properties, [NornisProvider.Key]);
        }).RequireAuthorization();

        connect.MapGet("/finish", async ([FromQuery(Name = "return")] string? returnTo, HttpContext context, IConfiguration configuration,
            StorageConnections connections, UserLibraries libraries, ILoggerFactory logs) =>
        {
            if (Ready(configuration) is not { } nornis) return Results.NotFound();
            if (!nornis.IsOwn(returnTo)) return Results.BadRequest();
            var userId = context.User.UserId()!;
            var ct = context.RequestAborted;
            if (!(await connections.ListAsync(userId, ct)).Any(c => c.Provider == NornisProvider.Key && !c.NeedsReconnect))
                return Results.Redirect(Back(returnTo!, Failed));
            await using (var lease = await libraries.OpenAsync(userId, ct))
                await EnsureCollectionAsync(lease.Library, ct);
            logs.CreateLogger(typeof(NornisConnect)).LogInformation("{User} connected Nornis", userId);
            return Results.Redirect(Back(returnTo!, Connected));
        }).RequireAuthorization();
    }

    /// <summary>Nornis's settings when this server can connect it: the nornis scheme's and the provider's.</summary>
    public static NornisOptions? Ready(IConfiguration configuration) =>
        NornisOptions.From(configuration) is { } nornis && StorageProviders.Find(NornisProvider.Key)!.IsAvailable(configuration) ? nornis : null;

    /// <summary>The address to send the browser back to Nornis by, with how connecting went.</summary>
    public static string Back(string returnTo, string outcome) => QueryHelpers.AddQueryString(returnTo, "bibliotaph", outcome);

    /// <summary>Where a sign-in or grant that went wrong part way through Connect Nornis sends the browser, if it was one.</summary>
    public static string? FailedReturn(AuthenticationProperties? properties, IConfiguration configuration, string outcome = Failed) =>
        properties?.Items.TryGetValue(ReturnItem, out var returnTo) == true && NornisOptions.From(configuration) is { } nornis && nornis.IsOwn(returnTo)
            ? Back(returnTo!, outcome)
            : null;

    /// <summary>The top-level collection the worlds' collections go in, made once and made again if the user deleted it.</summary>
    public static async Task<long> EnsureCollectionAsync(UserLibrary library, CancellationToken ct)
    {
        var settings = library.Get<SettingsStore>();
        var collections = library.Get<CollectionsService>();
        if (long.TryParse(await settings.GetAsync(SettingKeys.NornisCollection, ct), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            && (await collections.ListAsync(ct)).Any(c => c.Id == id))
            return id;
        var made = await collections.CreateAsync("Nornis", null, null, ct);
        await settings.SetAsync(SettingKeys.NornisCollection, made.Id.ToString(CultureInfo.InvariantCulture), ct);
        return made.Id;
    }

    static string Step(string step, string returnTo) => $"/connect/nornis/{step}?return={Uri.EscapeDataString(returnTo)}";
}
