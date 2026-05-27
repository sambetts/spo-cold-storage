using System.Text;

namespace Entities.Configuration;

public class ConnectionStrings(Microsoft.Extensions.Configuration.IConfigurationSection config) : BaseConfig(config)
{
    [ConfigValue]
    public string Storage { get; set; } = string.Empty;

    [ConfigValue]
    public string SQLConnectionString { get; set; } = string.Empty;

    [ConfigValue]
    public string ServiceBus { get; set; } = string.Empty;

}
