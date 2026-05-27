using System.Reflection;

namespace Entities.Configuration;

public abstract class BaseConfig
{
    /// <summary>
    /// Load automatically config properties
    /// </summary>
    /// <param name="config">Config to read</param>
    /// <exception cref="ArgumentNullException">If config to read is null</exception>
    /// <exception cref="ConfigurationMissingException">If config has missing required properties</exception>
    public BaseConfig(Microsoft.Extensions.Configuration.IConfiguration config)
    {
        if (config is null)
        {
            throw new ArgumentNullException(nameof(config));
        }

        // Set config props
        var allProps = this.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
        foreach (var prop in allProps)
        {
            // Set values
            var configValAtt = prop.GetCustomAttribute<ConfigValueAttribute>();
            if (configValAtt != null)
            {
                var configVal = config[prop.Name];
                if (!configValAtt.Optional && string.IsNullOrEmpty(configVal))
                {
                    throw new ConfigurationMissingException(prop.Name);
                }
                if (prop.PropertyType == typeof(int))
                {
                    if (int.TryParse(configVal, out var intVal))
                    {
                        prop.SetValue(this, intVal);
                    }
                }
                else
                {
                    prop.SetValue(this, configVal);
                }
            }

            // Set config sub-sections
            var configSectionAtt = prop.GetCustomAttribute<ConfigSectionAttribute>();
            if (configSectionAtt != null)
            {
                var configSection = config.GetSection(configSectionAtt.SectionName);
                var instance = Activator.CreateInstance(prop.PropertyType, configSection);

                prop.SetValue(this, instance);
            }
        }
    }
}

public class ConfigurationMissingException(string propertyName) : Exception($"Missing required configuration value '{propertyName}'")
{
}

/// <summary>
/// Property comes from supplied config section
/// </summary>
public class ConfigValueAttribute : Attribute
{
    public ConfigValueAttribute() { }
    public ConfigValueAttribute(bool optional)
    {
        this.Optional = optional;
    }
    public bool Optional { get; set; } = false;
}

/// <summary>
/// Property has a sub-section
/// </summary>
public class ConfigSectionAttribute(string sectionName) : Attribute
{
    public string SectionName { get; set; } = sectionName;
}
