using Migration.Engine;
using Xunit;

namespace Migration.Engine.Tests.Utils;

/// <summary>
/// The app-only SharePoint token (Sites.FullControl.All) is attached to every request a
/// ClientContext makes, and site URLs arrive from caller-supplied request bodies. These tests lock
/// down the guard that stops an attacker-supplied host being handed that token.
/// </summary>
public class AuthUtilsSiteUrlValidationTests
{
    private const string Tenant = "https://contoso.sharepoint.com";

    [Theory]
    [InlineData("https://contoso.sharepoint.com")]
    [InlineData("https://contoso.sharepoint.com/")]
    [InlineData("https://contoso.sharepoint.com/sites/Finance")]
    [InlineData("https://CONTOSO.SharePoint.com/sites/Finance")]
    [InlineData("https://contoso-my.sharepoint.com/personal/bob_contoso_com")]
    [InlineData("https://contoso-admin.sharepoint.com")]
    public void Accepts_urls_on_the_configured_tenant(string siteUrl)
    {
        var uri = AuthUtils.ValidateSiteUrl(siteUrl, Tenant);
        Assert.NotNull(uri);
    }

    [Theory]
    // Straight exfiltration targets.
    [InlineData("https://attacker.example/sites/x")]
    [InlineData("https://contoso.sharepoint.com.attacker.example/sites/x")]
    [InlineData("https://attacker.example/contoso.sharepoint.com")]
    // Look-alike tenants.
    [InlineData("https://contoso2.sharepoint.com")]
    [InlineData("https://notcontoso.sharepoint.com")]
    [InlineData("https://contoso.sharepoint.cn")]
    // Credential-in-URL trick: the real host is attacker.example.
    [InlineData("https://contoso.sharepoint.com@attacker.example/")]
    // Non-https and non-default ports.
    [InlineData("http://contoso.sharepoint.com")]
    [InlineData("https://contoso.sharepoint.com:8443/sites/x")]
    // Not absolute / not a URL at all.
    [InlineData("/sites/Finance")]
    [InlineData("not a url")]
    [InlineData("")]
    public void Rejects_anything_off_tenant_or_malformed(string siteUrl)
    {
        Assert.Throws<ArgumentException>(() => AuthUtils.ValidateSiteUrl(siteUrl, Tenant));
    }

    [Fact]
    public void Rejects_when_the_tenant_is_not_configured()
    {
        Assert.Throws<ArgumentException>(() => AuthUtils.ValidateSiteUrl("https://contoso.sharepoint.com", ""));
    }

    [Fact]
    public void Returns_the_parsed_uri_for_an_allowed_url()
    {
        var uri = AuthUtils.ValidateSiteUrl("https://contoso.sharepoint.com/sites/Finance", Tenant);
        Assert.Equal("contoso.sharepoint.com", uri.Host);
        Assert.Equal("/sites/Finance", uri.AbsolutePath);
    }
}
