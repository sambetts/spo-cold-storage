using CommandLine;

namespace LoadGenerator;

public class Options
{

    [Option("web", Required = true, HelpText = "SPWeb to fill with data.")]
    public string? TargetWeb { get; set; }

    [Option("kv", Required = true, HelpText = "Keyvault URL. Example: https://spocoldstorage.vault.azure.net")]
    public string? KeyVaultUrl { get; set; }

    [Option("ClientID", Required = true, HelpText = "App ID to access SharePoint CSOM.")]
    public string? ClientID { get; set; }

    [Option("ClientSecret", Required = true, HelpText = "App secret to access SharePoint CSOM.")]
    public string? ClientSecret { get; set; }

    [Option("BaseServerAddress", Required = true, HelpText = "Root SharePoint address. Example: https://contoso.sharepoint.com")]
    public string? BaseServerAddress { get; set; }

    [Option("TenantId", Required = true, HelpText = "Azure AD tenant GUID.")]
    public string? TenantId { get; set; }

    [Option("FileCount", Required = true, HelpText = "# of files to create")]
    public int FileCount { get; set; }
}
