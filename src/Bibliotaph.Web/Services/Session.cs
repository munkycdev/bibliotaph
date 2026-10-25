using Bibliotaph.Api.Contracts;

namespace Bibliotaph.Web.Services;

/// <summary>Who is signed in, asked of the server once and kept for the page's lifetime.</summary>
public sealed class Session(ApiClient api)
{
    Task<CurrentUser?>? _me;

    public Task<CurrentUser?> MeAsync() => _me ??= api.MeAsync();

    public async Task SignOutAsync()
    {
        await api.SignOutAsync();
        _me = Task.FromResult<CurrentUser?>(null);
    }
}
