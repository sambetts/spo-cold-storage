using Microsoft.SharePoint.Client;
using Migration.Engine.Utils;
using Models;

using Microsoft.Extensions.Logging;
namespace Migration.Engine.Connectors;

public class SPOListLoader(List list, BaseSharePointConnector baseSharePointConnector) : BaseChildLoader(baseSharePointConnector), IListLoader<ListItemCollectionPosition>
{
    private List? _listDef = null;

    public string Title { get; set; } = list.Title;
    public Guid ListId { get; set; } = list.Id;

    public async Task<PageResponse<ListItemCollectionPosition>> GetListItems(ListItemCollectionPosition? position)
    {
        SiteList? listModel = null;
        var pageResults = new PageResponse<ListItemCollectionPosition>();

        // List get-all query
        var camlQuery = new CamlQuery
        {
            ViewXml = "<View Scope=\"RecursiveAll\"><Query>" +
                "<OrderBy><FieldRef Name='ID' Ascending='TRUE'/></OrderBy></Query>" +
                "<RowLimit Paged=\"TRUE\">5000</RowLimit>" +
                "</View>"
        };

        // Large-list support & paging
        ListItemCollection listItems = null!;
        camlQuery.ListItemCollectionPosition = position;

        // For large lists, make sure we refresh the context when the token expires.
        var spClientList = await Parent.TokenManager.GetOrRefreshContext(() => _listDef = null);

        // Load list definition
        if (_listDef == null)
        {
            _listDef = spClientList.Web.Lists.GetById(this.ListId);
            spClientList.Load(_listDef, l => l.BaseType, l => l.ItemCount, l => l.RootFolder, list => list.Title);
            await spClientList.ExecuteQueryAsyncWithThrottleRetries(Parent.Logger);
        }

        // List items
        listItems = _listDef.GetItems(camlQuery);
        spClientList.Load(listItems, l => l.ListItemCollectionPosition);

        if (_listDef.BaseType == BaseType.DocumentLibrary)
        {
            // Load docs
            spClientList.Load(listItems,
                             items => items.Include(
                                item => item.Id,
                                item => item.FileSystemObjectType,
                                item => item["Modified"],
                                item => item["Created"],
                                item => item["Editor"],
                                item => item["File_x0020_Size"],
                                item => item.File.Exists,
                                item => item.File.ServerRelativeUrl,
                                item => item.File.VroomItemID,
                                item => item.File.VroomDriveID
                            )
                        );

            // Set drive ID when 1st results come back
            listModel = new DocLib()
            {
                Title = _listDef.Title,
                ServerRelativeUrl = _listDef.RootFolder.ServerRelativeUrl
            };
        }
        else
        {
            // Generic list, or similar enough. Load attachments
            spClientList.Load(listItems,
                             items => items.Include(
                                item => item.Id,
                                item => item.AttachmentFiles,
                                item => item["Modified"],
                                item => item["Created"],
                                item => item["Editor"],
                                item => item.File.Exists,
                                item => item.File.ServerRelativeUrl
                            )
                        );
            listModel = new SiteList() { Title = _listDef.Title, ServerRelativeUrl = _listDef.RootFolder.ServerRelativeUrl };
        }

        try
        {
            await spClientList.ExecuteQueryAsyncWithThrottleRetries(Parent.Logger);
        }
        catch (System.Net.WebException ex)
        {
            Parent.Logger.LogError(ex, "Unhandled exception");
            Parent.Logger.LogError($"Got error reading list: {ex.Message}.");
        }

        // Remember position, if more than 5000 items are in the list
        pageResults.NextPageToken = listItems.ListItemCollectionPosition;

        foreach (var item in listItems)
        {
            var contentTypeId = item.FieldValues["ContentTypeId"]?.ToString();
            var itemIsFolder = contentTypeId != null && contentTypeId.StartsWith("0x012");
            var itemUrl = item.FieldValues["FileRef"]?.ToString();

            if (!itemIsFolder)
            {
                SharePointFileInfoWithList? foundFileInfo = null;
                if (_listDef.BaseType == BaseType.GenericList)
                {
                    pageResults.FilesFound.AddRange(ProcessListItemAttachments(item, listModel, spClientList));
                }
                else if (_listDef.BaseType == BaseType.DocumentLibrary)
                {
                    // We might be able get the drive Id from the actual list, but not sure how...get it from 1st item instead
                    var docLib = (DocLib)listModel;
                    if (string.IsNullOrEmpty(docLib.DriveId))
                    {
                        try
                        {
                            ((DocLib)listModel).DriveId = item.File.VroomDriveID;
                        }
                        catch (ServerObjectNullReferenceException)
                        {
                            Parent.Logger.LogInformation($"WARNING: Couldn't get Drive info for list {_listDef.Title} on item {itemUrl}. Ignoring.");
                            break;
                        }
                    }

                    foundFileInfo = ProcessDocLibItem(item, listModel, spClientList);
                }
                if (foundFileInfo != null)
                {
                    pageResults.FilesFound.Add(foundFileInfo!);
                }
            }
            else
            {
                pageResults.FoldersFound.Add(itemUrl!);
            }
        }

        return pageResults;
    }

    /// <summary>
    /// Process a single document library item.
    /// </summary>
    private SharePointFileInfoWithList? ProcessDocLibItem(ListItem docListItem, SiteList listModel, ClientContext spClient)
    {
        if (docListItem.FileSystemObjectType == FileSystemObjectType.File && docListItem.File.Exists)
        {
            var foundFileInfo = GetSharePointFileInfo(docListItem, docListItem.File.ServerRelativeUrl, listModel, spClient);
            return foundFileInfo;
        }

        return null;
    }

    /// <summary>
    /// Process custom list item with possibly multiple attachments
    /// </summary>
    private List<SharePointFileInfoWithList> ProcessListItemAttachments(ListItem item, SiteList listModel, ClientContext spClient)
    {
        var attachmentsResults = new List<SharePointFileInfoWithList>();

        foreach (var attachment in item.AttachmentFiles)
        {
            var foundFileInfo = GetSharePointFileInfo(item, attachment.ServerRelativeUrl, listModel, spClient);
            attachmentsResults.Add(foundFileInfo);
        }

        return attachmentsResults;
    }
    SharePointFileInfoWithList GetSharePointFileInfo(ListItem item, string url, SiteList listModel, ClientContext _spClient)
    {
        var dir = "";
        if (item.FieldValues.ContainsKey("FileDirRef"))
        {
            dir = item.FieldValues["FileDirRef"].ToString();
            if (dir!.StartsWith(listModel.ServerRelativeUrl))
            {
                // Truncate list URL from dir value of item
                dir = dir[listModel.ServerRelativeUrl.Length..].TrimStart("/".ToCharArray());
            }
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(item), "Can't find dir column");
        }

        // Get the full directory path (not truncated)
        var fullDirectoryPath = item.FieldValues.ContainsKey("FileDirRef") ? item.FieldValues["FileDirRef"].ToString() : string.Empty;

        var dt = DateTime.MinValue;
        DateTime? createdDate = null;

        // Parse creation date if available
        if (item.FieldValues.ContainsKey("Created") && DateTime.TryParse(item.FieldValues["Created"]?.ToString(), out var createdDt))
        {
            createdDate = createdDt;
        }

        if (DateTime.TryParse(item.FieldValues["Modified"]?.ToString(), out dt))
        {
            var authorFieldObj = item.FieldValues["Editor"];
            if (authorFieldObj != null)
            {
                var authorVal = (FieldUserValue)authorFieldObj;
                var author = !string.IsNullOrEmpty(authorVal.Email) ? authorVal.Email : authorVal.LookupValue;
                var isGraphDriveItem = listModel is DocLib;
                long size = 0;

                // Doc or list-item?
                if (!isGraphDriveItem)
                {
                    var sizeVal = item.FieldValues["SMTotalFileStreamSize"];

                    if (sizeVal != null)
                        long.TryParse(sizeVal.ToString(), out size);

                    // No Graph IDs - probably a list item
                    return new SharePointFileInfoWithList
                    {
                        Author = author,
                        ServerRelativeFilePath = url,
                        LastModified = dt,
                        CreatedDate = createdDate,
                        DirectoryPath = fullDirectoryPath ?? string.Empty,
                        WebUrl = _spClient.Web.Url,
                        SiteUrl = _spClient.Site.Url,
                        Subfolder = dir.TrimEnd("/".ToCharArray()),
                        List = listModel,
                        FileSize = size
                    };
                }
                else
                {
                    var sizeVal = item.FieldValues["File_x0020_Size"];

                    if (sizeVal != null)
                        long.TryParse(sizeVal.ToString(), out size);
                    return new DriveItemSharePointFileInfo
                    {
                        Author = author,
                        ServerRelativeFilePath = url,
                        LastModified = dt,
                        CreatedDate = createdDate,
                        DirectoryPath = fullDirectoryPath ?? string.Empty,
                        WebUrl = _spClient.Web.Url,
                        SiteUrl = _spClient.Site.Url,
                        Subfolder = dir.TrimEnd("/".ToCharArray()),
                        GraphItemId = item.File.VroomItemID,
                        DriveId = item.File.VroomDriveID,
                        List = listModel,
                        FileSize = size
                    };
                }
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(item), "Can't find author column");
            }
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(item), "Can't find modified column");
        }
    }

}
