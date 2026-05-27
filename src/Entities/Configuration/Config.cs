namespace Entities.Configuration;

public class Config(Microsoft.Extensions.Configuration.IConfiguration config) : BaseConfig(config)
{
    [ConfigValue]
    public string BaseServerAddress { get; set; } = string.Empty;

    public string ServiceBusQueueName => "filediscovery";

    [ConfigValue]
    public string KeyVaultUrl { get; set; } = string.Empty;

    [ConfigValue]
    public string BlobContainerName { get; set; } = string.Empty;

    [ConfigValue(true)]
    public string AppInsightsInstrumentationKey { get; set; } = string.Empty;

    public bool HaveAppInsightsConfigured => !string.IsNullOrEmpty(AppInsightsInstrumentationKey);

    [ConfigValue(true)]
    public int AnalysisSkipHours { get; set; } = 24;

    [ConfigSection("AzureAd")]
    public AzureAdConfig AzureAdConfig { get; set; } = null!;

    [ConfigSection("ConnectionStrings")]
    public ConnectionStrings ConnectionStrings { get; set; } = null!;

    [ConfigSection("Dev")]
    public DevConfig DevConfig { get; set; } = null!;

    [ConfigSection("Search")]
    public SearchConfig SearchConfig { get; set; } = null!;
}

public class ConfigException(string message) : Exception(message)
{
}
