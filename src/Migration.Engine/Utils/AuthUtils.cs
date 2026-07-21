using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Azure.Security.KeyVault.Certificates;
using Microsoft.Identity.Client;
using Microsoft.SharePoint.Client;
using Entities.Configuration;
using Migration.Engine.Utils;
using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;

using Microsoft.Extensions.Logging;
namespace Migration.Engine;

public class AuthUtils
{
    private static X509Certificate2? _cachedCert = null;

    // Confidential-client apps cached per (tenant|client|auth) so MSAL's in-memory token
    // cache is reused across items. Without this, every ClientContext creation rebuilt the
    // app and re-acquired an app-only token from AAD — needless latency and an extra throttle
    // surface on a large migration. One shared app => one token fetch per ~1h, auto-refreshed.
    private static readonly ConcurrentDictionary<string, Lazy<Task<IConfidentialClientApplication>>> _appCache = new();
    
    /// <summary>
    /// Retrieves a certificate from Azure Key Vault.
    /// Only used when AuthenticationMode is set to "Certificate".
    /// </summary>
    public static async Task<X509Certificate2> RetrieveKeyVaultCertificate(string name, string tenantId, string clientId, string clientSecret, string keyVaultUrl)
    {
        if (_cachedCert == null)
        {
            var keyClient = new CertificateClient(vaultUri: new Uri(keyVaultUrl), credential: new ClientSecretCredential(tenantId, clientId, clientSecret));

            var certificate = await keyClient.DownloadCertificateAsync(name);
            _cachedCert = certificate.Value;
        }
        return _cachedCert;

    }
    public async static Task<ClientContext> GetClientContext(string siteUrl, string tenantId, string clientId, string clientSecret, string keyVaultUrl, string baseServerAddress, ILogger logger)
    {
        return await GetClientContext(siteUrl, tenantId, clientId, clientSecret, keyVaultUrl, baseServerAddress, logger, null, true, "AzureAutomationSPOAccess");
    }
    
    public async static Task<ClientContext> GetClientContext(string siteUrl, string tenantId, string clientId, string clientSecret, string keyVaultUrl, string baseServerAddress, ILogger logger, Action<AuthenticationResult>? authResultDelegate)
    {
        return await GetClientContext(siteUrl, tenantId, clientId, clientSecret, keyVaultUrl, baseServerAddress, logger, authResultDelegate, true, "AzureAutomationSPOAccess");
    }

    public async static Task<ClientContext> GetClientContext(string siteUrl, string tenantId, string clientId, string clientSecret, string keyVaultUrl, string baseServerAddress, ILogger logger, Action<AuthenticationResult>? authResultDelegate, bool useCertificateAuth, string certificateName, bool warmUpWeb = true)
    {
        if (string.IsNullOrEmpty(siteUrl))
        {
            throw new ArgumentException($"'{nameof(siteUrl)}' cannot be null or empty.", nameof(siteUrl));
        }

        if (string.IsNullOrEmpty(tenantId))
        {
            throw new ArgumentException($"'{nameof(tenantId)}' cannot be null or empty.", nameof(tenantId));
        }

        if (string.IsNullOrEmpty(clientId))
        {
            throw new ArgumentException($"'{nameof(clientId)}' cannot be null or empty.", nameof(clientId));
        }

        if (string.IsNullOrEmpty(clientSecret))
        {
            throw new ArgumentException($"'{nameof(clientSecret)}' cannot be null or empty.", nameof(clientSecret));
        }

        if (useCertificateAuth && string.IsNullOrEmpty(keyVaultUrl))
        {
            throw new ArgumentException($"'{nameof(keyVaultUrl)}' cannot be null or empty when using certificate authentication.", nameof(keyVaultUrl));
        }

        if (string.IsNullOrEmpty(baseServerAddress))
        {
            throw new ArgumentException($"'{nameof(baseServerAddress)}' cannot be null or empty.", nameof(baseServerAddress));
        }

        var app = await GetNewClientApp(tenantId, clientId, clientSecret, keyVaultUrl, useCertificateAuth, certificateName);
        var result = await app.AuthForSharePointOnline(baseServerAddress);
        if (authResultDelegate != null)
        {
            authResultDelegate(result);
        }

        var ctx = new ClientContext(siteUrl);
        ctx.ExecutingWebRequest += (s, e) =>
        {
            e.WebRequestExecutor.RequestHeaders["Authorization"] = "Bearer " + result.AccessToken;
        };

        // Optional warm-up. It costs a SharePoint round-trip per context, so the migrate/restore
        // hot path skips it (warmUpWeb: false) to reduce SP calls + throttling — the callers there
        // query the file/folder directly (GetFileByServerRelativeUrl) and never read a pre-loaded
        // Web property. Keep it on by default for callers that expect ctx.Web to be populated.
        if (warmUpWeb)
        {
            ctx.Load(ctx.Web);
            await ctx.ExecuteQueryAsyncWithThrottleRetries(logger);
        }

        return ctx;
    }
    public async static Task<ClientContext> GetClientContext(IConfidentialClientApplication app, string baseServerAddress, string siteUrl, ILogger logger)
    {
        var result = await app.AuthForSharePointOnline(baseServerAddress);

        var ctx = new ClientContext(siteUrl);
        ctx.ExecutingWebRequest += (s, e) =>
        {
            e.WebRequestExecutor.WebRequest.UserAgent = "NONISV|GitHubSamBetts|SPOColdStorageMigration/1.0";
            e.WebRequestExecutor.RequestHeaders["Authorization"] = "Bearer " + result.AccessToken;
        };

        ctx.Load(ctx.Web);
        await ctx.ExecuteQueryAsyncWithThrottleRetries(logger);

        return ctx;
    }

    /// <summary>
    /// Creates a confidential client application with certificate-based authentication (legacy method for backward compatibility).
    /// </summary>
    public static async Task<IConfidentialClientApplication> GetNewClientApp(string tenantId, string clientId, string clientSecret, string keyVaultUrl)
    {
        return await GetNewClientApp(tenantId, clientId, clientSecret, keyVaultUrl, true, "AzureAutomationSPOAccess");
    }

    /// <summary>
    /// Creates (or returns a cached) confidential client application with either certificate or
    /// client secret authentication. Cached per (tenant|client|auth) so MSAL reuses its token cache.
    /// </summary>
    /// <param name="useCertificateAuth">If true, uses certificate from Key Vault; if false, uses client secret directly</param>
    /// <param name="certificateName">Name of certificate in Key Vault (only used if useCertificateAuth is true)</param>
    public static async Task<IConfidentialClientApplication> GetNewClientApp(string tenantId, string clientId, string clientSecret, string keyVaultUrl, bool useCertificateAuth, string certificateName)
    {
        var key = $"{tenantId}|{clientId}|{useCertificateAuth}|{certificateName}";
        var lazy = _appCache.GetOrAdd(key, _ => new Lazy<Task<IConfidentialClientApplication>>(
            () => BuildClientAppAsync(tenantId, clientId, clientSecret, keyVaultUrl, useCertificateAuth, certificateName)));
        try
        {
            return await lazy.Value.ConfigureAwait(false);
        }
        catch
        {
            // Don't cache a transient build failure (e.g. Key Vault blip) forever.
            _appCache.TryRemove(key, out _);
            throw;
        }
    }

    private static async Task<IConfidentialClientApplication> BuildClientAppAsync(string tenantId, string clientId, string clientSecret, string keyVaultUrl, bool useCertificateAuth, string certificateName)
    {
        var builder = ConfidentialClientApplicationBuilder.Create(clientId)
                                              .WithAuthority($"https://login.microsoftonline.com/{tenantId}");

        if (useCertificateAuth)
        {
            // Certificate-based authentication (requires Key Vault)
            var appRegistrationCert = await AuthUtils.RetrieveKeyVaultCertificate(certificateName, tenantId, clientId, clientSecret, keyVaultUrl);
            builder.WithCertificate(appRegistrationCert);
        }
        else
        {
            // Client secret authentication (no certificate needed)
            builder.WithClientSecret(clientSecret);
        }

        return builder.Build();
    }

    public static async Task<ClientContext> GetClientContext(Config config, string siteUrl, ILogger logger, Action<AuthenticationResult>? authResultDelegate, bool warmUpWeb = true)
    {
        return await GetClientContext(siteUrl, config.AzureAdConfig.TenantId!, config.AzureAdConfig.ClientID!,
            config.AzureAdConfig.Secret!, config.KeyVaultUrl, config.BaseServerAddress, logger, authResultDelegate,
            config.AzureAdConfig.UseCertificateAuth, config.AzureAdConfig.CertificateName, warmUpWeb);
    }

    public static async Task<IConfidentialClientApplication> GetNewClientApp(Config config)
    {
        return await GetNewClientApp(config.AzureAdConfig.TenantId!,
            config.AzureAdConfig.ClientID!, config.AzureAdConfig.Secret!, config.KeyVaultUrl,
            config.AzureAdConfig.UseCertificateAuth, config.AzureAdConfig.CertificateName);
    }
}

public static class ConfidentialClientApplicationAuth
{
    public async static Task<AuthenticationResult> AuthForSharePointOnline(this IConfidentialClientApplication app, string baseServerAddress)
    {
        var scopes = new string[] { $"{baseServerAddress}/.default" };
        var result = await app.AcquireTokenForClient(scopes).ExecuteAsync();
        return result;
    }
}
