using Microsoft.Extensions.Logging;

namespace Migration.Engine.Connectors;

/// <summary>
/// Base class for Graph API-based connectors
/// Similar to BaseSharePointConnector but for Graph API
/// </summary>
public abstract class BaseGraphConnector
{
    private readonly GraphClientManager _graphClientManager;
    private readonly ILogger _logger;

    protected BaseGraphConnector(GraphClientManager graphClientManager, ILogger logger)
    {
        _graphClientManager = graphClientManager ?? throw new ArgumentNullException(nameof(graphClientManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public GraphClientManager GraphClientManager => _graphClientManager;
    public ILogger Logger => _logger;
}

/// <summary>
/// Base class for child loaders (Web, List)
/// </summary>
public abstract class BaseGraphChildLoader
{
    protected BaseGraphChildLoader(BaseGraphConnector parent)
    {
        Parent = parent ?? throw new ArgumentNullException(nameof(parent));
    }

    public BaseGraphConnector Parent { get; }
}
