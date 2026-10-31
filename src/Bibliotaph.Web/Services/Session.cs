using Bibliotaph.Api.Contracts;

namespace Bibliotaph.Web.Services;

/// <summary>Who is signed in, asked of the server once and kept for the page's lifetime.</summary>
public sealed class Session(AccountClient accountApi)
{
    Task<CurrentUser?>? _me;

    public Task<CurrentUser?> MeAsync() => _me ??= accountApi.MeAsync();

    /// <summary>Signs out, and says where the page goes next to end the sign-in provider's session too.</summary>
    public async Task<string> SignOutAsync()
    {
        var signedOut = await accountApi.SignOutAsync();
        _me = Task.FromResult<CurrentUser?>(null);
        return signedOut.Next;
    }

    /// <summary>Closes the account and signs out, saying where the page goes next as <see cref="SignOutAsync"/> does.</summary>
    public async Task<string> CloseAccountAsync()
    {
        var signedOut = await accountApi.CloseAccountAsync();
        _me = Task.FromResult<CurrentUser?>(null);
        return signedOut.Next;
    }
}
