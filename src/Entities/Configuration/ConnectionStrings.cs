using System.Text;

namespace Entities.Configuration;

public class ConnectionStrings(Microsoft.Extensions.Configuration.IConfigurationSection config) : BaseConfig(config)
{
    /// <summary>
    /// Storage connection string - only required for migration operations
    /// </summary>
    [ConfigValue(true)]
    public string Storage { get; set; } = string.Empty;

    [ConfigValue]
    public string SQLConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Service Bus connection string - only required for distributed migration operations
    /// </summary>
    [ConfigValue(true)]
    public string ServiceBus { get; set; } = string.Empty;

}
