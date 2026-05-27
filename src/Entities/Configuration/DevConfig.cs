namespace Entities.Configuration;

public class DevConfig(Microsoft.Extensions.Configuration.IConfigurationSection config) : BaseConfig(config)
{
    [ConfigValue(true)]
    public string DefaultSharePointSite { get; set; } = string.Empty;

    [ConfigValue(true)]
    public bool ResetDb { get; set; } = false;
}
