namespace Entities.Configuration;

public class SearchConfig(Microsoft.Extensions.Configuration.IConfigurationSection config) : BaseConfig(config)
{
    [ConfigValue(true)]
    public string IndexName { get; set; } = string.Empty;

    [ConfigValue(true)]
    public string ServiceName { get; set; } = string.Empty;

    [ConfigValue(true)]
    public string QueryKey { get; set; } = string.Empty;
}
