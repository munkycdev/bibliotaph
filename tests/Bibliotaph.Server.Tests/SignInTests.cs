using System.Security.Claims;
using Bibliotaph.Server.Auth;

namespace Bibliotaph.Server.Tests;

public sealed class SignInTests
{
    static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "test"));

    static SignInProvider Provider(string id) => SignInProvider.All.Single(p => p.Id == id);

    [Fact]
    public void A_Microsoft_sign_in_is_known_by_object_and_tenant_ids()
    {
        var identity = Provider("microsoft").Identify(Principal(("oid", "o1"), ("tid", "t1"), ("sub", "per-app"), ("preferred_username", "dana@example.com")));

        Assert.Equal("o1@t1", identity!.Subject);
        Assert.Equal("dana@example.com", identity.Email);
    }

    [Fact]
    public void A_Google_email_counts_only_when_Google_verified_it()
    {
        var unverified = Provider("google").Identify(Principal(("sub", "s1"), ("email", "dana@example.org"), ("email_verified", "false")));
        var verified = Provider("google").Identify(Principal(("sub", "s1"), ("email", "dana@example.org"), ("email_verified", "true")));

        Assert.Null(unverified!.Email);
        Assert.Equal("dana@example.org", verified!.Email);
    }

    [Fact]
    public void A_token_without_a_stable_id_is_not_a_sign_in() =>
        Assert.Null(Provider("microsoft").Identify(Principal(("sub", "per-app"))));

    [Theory]
    [InlineData("/settings", "/settings")]
    [InlineData("//evil.example", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("https://evil.example", "/")]
    [InlineData(null, "/")]
    public void After_sign_in_only_a_path_on_this_site_is_followed(string? asked, string expected) =>
        Assert.Equal(expected, AuthEndpoints.LocalOrHome(asked));
}
