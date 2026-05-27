using Microsoft.SharePoint.Client;
using Migration.Engine.Utils;
using Models;

using Microsoft.Extensions.Logging;
namespace Migration.Engine.Connectors;

public class SPOWebLoader(Web sPWeb, ClientContext clientContext, BaseSharePointConnector baseSharePointConnector) : BaseChildLoader(baseSharePointConnector), IWebLoader<ListItemCollectionPosition>
{
    private readonly ClientContext _clientContext = clientContext;

    public Web SPWeb { get; set; } = sPWeb;

    public async Task<List<IListLoader<ListItemCollectionPosition>>> GetLists()
    {
        var lists = new List<IListLoader<ListItemCollectionPosition>>();

        _clientContext.Load(SPWeb.Lists);
        await _clientContext.ExecuteQueryAsyncWithThrottleRetries(Parent.Logger);

        foreach (var list in SPWeb.Lists)
        {
            var listReadSuccess = false;
            try
            {
                _clientContext.Load(list, l => l.IsSystemList);
                await _clientContext.ExecuteQueryAsyncWithThrottleRetries(Parent.Logger);
                listReadSuccess = true;
            }
            catch (System.Net.WebException ex)
            {
                Parent.Logger.LogInformation($"Got exception '{ex.Message}' loading data for list ID '{list.Id}' - not configured to analyse.");
            }

            if (listReadSuccess)
            {
                // Do not search through system or hidden lists
                if (!list.Hidden && !list.IsSystemList)
                {
                    Parent.Logger.LogInformation($"Found '{list.Title}'...");
                    lists.Add(new SPOListLoader(list, Parent));
                }
                else
                {
                    Parent.Logger.LogInformation($"Ignoring system/hidden list '{list.Title}'.");
                }
            }
        }

        return lists;
    }
}
