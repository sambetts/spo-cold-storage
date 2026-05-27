using Microsoft.SharePoint.Client;
using System.Text;

using Microsoft.Extensions.Logging;
namespace Migration.Engine.Utils;

public static class SharePointUtils
{
    /// <summary>
    /// Save file & return new item GUID
    /// </summary>
    public static async Task<Guid> SaveFile(this List targetList, ClientContext ctx, string fileName, byte[] contents, ILogger ILogger)
    {
        var fileCreationInfo = new FileCreationInformation
        {
            Content = contents,
            Overwrite = true,
            Url = fileName
        };
        var uploadFile = targetList.RootFolder.Files.Add(fileCreationInfo);
        ctx.Load(uploadFile);
        await ctx.ExecuteQueryAsyncWithThrottleRetries(ILogger);

        return uploadFile.UniqueId;
    }

    /// <summary>
    /// Fully load a document listitem
    /// </summary>
    public static async Task<ListItem> FullLoadListItemDoc(this ListItem targetItem, ClientContext ctx)
    {

        ctx.Load(targetItem,
                    item => item.Id,
                    item => item.FileSystemObjectType,
                    item => item["Modified"],
                    item => item["Editor"],
                    item => item.File.Exists,
                    item => item.File.ServerRelativeUrl,
                    item => item.File.VroomItemID,
                    item => item.File.VroomDriveID
                    );

        await ctx.ExecuteQueryAsync();

        return targetItem;
    }
}
