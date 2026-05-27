using Microsoft.Extensions.Logging;
using Entities.Configuration;

namespace Migration.Engine;

public abstract class BaseComponent(Config config, ILogger logger)
{
    protected Config _config = config ?? throw new ArgumentNullException(nameof(config));
    protected ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
}
