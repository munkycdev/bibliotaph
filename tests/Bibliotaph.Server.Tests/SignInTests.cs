using System.Security.Claims;
using Bibliotaph.Server.Auth;

namespace Bibliotaph.Server.Tests;

public sealed class SignInTests
{
    static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "test"));

    [Fact]
    public void An_Auth0_sign_in_is_known_by_its_sub_which_names_the_connection()
    {
        var identity = Auth0SignIn.Identify(Principal(("sub", "google-oauth2|1234"), ("name", "Dana"), ("email", "dana@example.org"), ("email_verified", "true")));

        Assert.Equal(new Bibliotaph.Server.Accounts.ExternalIdentity("auth0", "google-oauth2|1234", "Dana", "dana@example.org"),
            identity! with { FormerSubjects = [] });
        Assert.Equal(["oauth2|google-oauth2|1234"], identity.FormerSubjects);
    }

    [Fact]
    public void An_email_counts_only_when_Auth0_says_it_was_verified() =>
        Assert.Null(Auth0SignIn.Identify(Principal(("sub", "auth0|1"), ("email", "dana@example.org"), ("email_verified", "false")))!.Email);

    [Fact]
    public void A_token_without_a_sub_is_not_a_sign_in() =>
        Assert.Null(Auth0SignIn.Identify(Principal(("email", "dana@example.org"))));

    [Theory]
    [InlineData("auth0", "google-oauth2|1234", "Google")]
    [InlineData("auth0", "windowslive|abc", "Microsoft")]
    [InlineData("auth0", "oauth2|discord|5678", "Discord")]
    [InlineData("auth0", "discord|5678", "Discord")]
    [InlineData("auth0", "apple|001", "Apple")]
    [InlineData("auth0", "auth0|65f0", "Email")]
    [InlineData("auth0", "github|9", "Github")]
    [InlineData("developer", "local", "Developer")]
    public void Settings_names_a_sign_in_by_the_connection_it_came_through(string provider, string subject, string expected) =>
        Assert.Equal(expected, Auth0SignIn.Describe(provider, subject));

    [Theory]
    [InlineData("oauth2|discord|5678", "discord|5678")]
    [InlineData("discord|5678", "oauth2|discord|5678")]
    [InlineData("google-oauth2|1234", "oauth2|google-oauth2|1234")]
    [InlineData("auth0|65f0", null)]
    [InlineData("email|65f0", null)]
    [InlineData("oddly|shaped|sub|here", null)]
    public void A_sub_from_the_other_kind_of_connection_is_the_same_person(string sub, string? former) =>
        Assert.Equal(former is null ? [] : [former], Auth0SignIn.FormerSubjects(sub));

    [Fact]
    public void Signing_out_ends_the_Auth0_session_and_comes_back_home() =>
        Assert.Equal("https://tenant.example.auth0.com/v2/logout?client_id=abc&returnTo=https%3A%2F%2Fbibliotaph.online%2F",
            Auth0SignIn.LogoutUrl(new Auth0Client { Domain = "tenant.example.auth0.com", ClientId = "abc", ClientSecret = "s" }, "https://bibliotaph.online/"));

    [Theory]
    [InlineData("/settings", "/settings")]
    [InlineData("//evil.example", "/home")]
    [InlineData("/\\evil.example", "/home")]
    [InlineData("https://evil.example", "/home")]
    [InlineData(null, "/home")]
    public void After_sign_in_only_a_path_on_this_site_is_followed(string? asked, string expected) =>
        Assert.Equal(expected, AuthEndpoints.LocalOrHome(asked));
}
