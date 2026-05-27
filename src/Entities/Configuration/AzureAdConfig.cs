using System.Text;

namespace Entities.Configuration;

public class AzureAdConfig(Microsoft.Extensions.Configuration.IConfigurationSection config) : BaseConfig(config)
{
    [ConfigValue]
    public string? Secret { get; set; } = string.Empty;

    [ConfigValue]
    public string? ClientID { get; set; } = string.Empty;

    [ConfigValue]
    public string? TenantId { get; set; } = string.Empty;

    /// <summary>
    /// Authentication mode: "Certificate" (default) or "ClientSecret"
    /// - Certificate: Uses certificate from Key Vault (requires KeyVaultUrl and CertificateName)
    /// - ClientSecret: Uses client secret directly (similar to PowerShell script approach)
    /// </summary>
    [ConfigValue(true)]
    public string AuthenticationMode { get; set; } = "Certificate";

    /// <summary>
    /// Name of the certificate in Key Vault (only used when AuthenticationMode = "Certificate")
    /// </summary>
    [ConfigValue(true)]
    public string CertificateName { get; set; } = "AzureAutomationSPOAccess";

    public bool UseCertificateAuth => AuthenticationMode?.Equals("Certificate", StringComparison.OrdinalIgnoreCase) ?? true;
    public bool UseClientSecretAuth => AuthenticationMode?.Equals("ClientSecret", StringComparison.OrdinalIgnoreCase) ?? false;
}
