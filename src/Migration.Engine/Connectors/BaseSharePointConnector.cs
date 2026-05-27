using Microsoft.Identity.Client;
using Microsoft.SharePoint.Client;
using Entities.Configuration;
using Migration.Engine.Utils;

using Microsoft.Extensions.Logging;
namespace Migration.Engine.Connectors;

public abstract class BaseSharePointConnector(SPOTokenManager tokenManager, ILogger logger)
{
    private readonly SPOTokenManager tokenManager = tokenManager;
    private readonly ILogger logger = logger;

    public ILogger Logger => logger;
    public SPOTokenManager TokenManager => tokenManager;
}

public abstract class BaseChildLoader(BaseSharePointConnector parent)
{
    public BaseSharePointConnector Parent { get; } = parent;
}
