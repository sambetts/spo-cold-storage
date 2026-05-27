using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Models;
/// <summary>
/// SharePoint Online file metadata for base file-type
/// </summary>
public class BaseSharePointFileInfo
{
    public BaseSharePointFileInfo() { }
    public BaseSharePointFileInfo(BaseSharePointFileInfo driveArg) : this()
    {
        this.SiteUrl = driveArg.SiteUrl;
        this.WebUrl = driveArg.WebUrl;
        this.ServerRelativeFilePath = driveArg.ServerRelativeFilePath;
        this.Author = driveArg.Author;
        this.Subfolder = driveArg.Subfolder;
        this.DirectoryPath = driveArg.DirectoryPath;
        this.LastModified = driveArg.LastModified;
        this.CreatedDate = driveArg.CreatedDate;
        this.FileSize = driveArg.FileSize;
        this.DriveId = driveArg.DriveId;
        this.GraphItemId = driveArg.GraphItemId;
    }

    /// <summary>
    /// Example: https://m365x352268.sharepoint.com/sites/MigrationHost
    /// </summary>
    public string SiteUrl { get; set; } = string.Empty;

    /// <summary>
    /// Example: https://m365x352268.sharepoint.com/sites/MigrationHost/subsite
    /// </summary>
    public string WebUrl { get; set; } = string.Empty;

    /// <summary>
    /// Example: /sites/MigrationHost/Shared%20Documents/Contoso.pptx
    /// </summary>
    public string ServerRelativeFilePath { get; set; } = string.Empty;

    public string Author { get; set; } = string.Empty;

    /// <summary>
    /// Item sub-folder name. Cannot start or end with a slash
    /// </summary>
    public string Subfolder { get; set; } = string.Empty;

    /// <summary>
    /// Directory path where the file was found
    /// </summary>
    public string DirectoryPath { get; set; } = string.Empty;

    public DateTime LastModified { get; set; } = DateTime.MinValue;

    public DateTime? CreatedDate { get; set; }

    /// <summary>
    /// Bytes
    /// </summary>
    public long FileSize { get; set; } = 0;

    /// <summary>
    /// Graph drive id. Set when this file came from a Drive scan; required to
    /// call /drives/{id}/items/{itemId}/analytics. Persisted in the files
    /// table so analytics can be retried on later runs without a full crawl.
    /// </summary>
    // MaxLength 450 mirrors the EF mapping on SPFile so the StagingFiles
    // table is created with NVARCHAR(450) instead of NVARCHAR(MAX), keeping
    // both tables consistent and indexable.
    [MaxLength(450)]
    public string? DriveId { get; set; }

    /// <summary>
    /// Graph drive-item id. Pairs with <see cref="DriveId"/> to identify the
    /// item across Graph API calls.
    /// </summary>
    [MaxLength(450)]
    public string? GraphItemId { get; set; }

    /// <summary>
    /// Calculated.
    /// </summary>
    [JsonIgnore]
    public bool IsValidInfo => !string.IsNullOrEmpty(ServerRelativeFilePath) &&
        !string.IsNullOrEmpty(SiteUrl) &&
        !string.IsNullOrEmpty(WebUrl) &&
        this.LastModified > DateTime.MinValue &&
        this.WebUrl.StartsWith(this.SiteUrl) &&
        this.FullSharePointUrl.StartsWith(this.WebUrl) &&
        ValidSubFolderIfSpecified;

    bool ValidSubFolderIfSpecified
    {
        get
        {
            if (string.IsNullOrEmpty(Subfolder))
            {
                return true;
            }
            else
            {
                return !Subfolder.StartsWith("/") && !Subfolder.EndsWith("/") && !Subfolder.Contains(@"//");
            }
        }
    }

    public override string ToString()
    {
        return $"{this.ServerRelativeFilePath}";
    }

    /// <summary>
    /// Calculated. Web + file URL, minus overlap, if both are valid.
    /// </summary>
    [JsonIgnore]
    public string FullSharePointUrl
    {
        get
        {
            // Strip out relative web part of file URL
            const string DOMAIN = "sharepoint.com";
            var domainStart = WebUrl.IndexOf(DOMAIN, StringComparison.CurrentCultureIgnoreCase);
            if (domainStart > -1 && ValidSubFolderIfSpecified)      // Basic checks. IsValidInfo uses this prop so can't use that.
            {
                var webMinusServer = WebUrl.Substring(domainStart + DOMAIN.Length, (WebUrl.Length - domainStart) - DOMAIN.Length);

                if (ServerRelativeFilePath.StartsWith(webMinusServer))
                {
                    var filePathWithoutWeb = ServerRelativeFilePath[webMinusServer.Length..];

                    return WebUrl + filePathWithoutWeb;
                }
                else
                {
                    return ServerRelativeFilePath;
                }
            }
            else
            {
                return ServerRelativeFilePath;
            }
        }
    }
}

public class SharePointFileInfoWithList : BaseSharePointFileInfo
{
    public SharePointFileInfoWithList() { }
    public SharePointFileInfoWithList(DriveItemSharePointFileInfo driveArg) : base(driveArg)
    {
        this.List = driveArg.List;
    }

    /// <summary>
    /// Parent list
    /// </summary>
    public SiteList List { get; set; } = new SiteList();

}

public class DriveItemSharePointFileInfo : SharePointFileInfoWithList
{
    public DriveItemSharePointFileInfo() : base() { }
    public DriveItemSharePointFileInfo(DriveItemSharePointFileInfo driveArg) : base(driveArg) { }
}
