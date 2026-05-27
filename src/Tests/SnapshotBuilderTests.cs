using Microsoft.EntityFrameworkCore;
using Entities;
using Migration.Engine.SnapshotBuilder;
using Migration.Engine.Utils.Extensions;
using Models;
using Xunit;

namespace Tests;

public class SnapshotBuilderTests : AbstractTest
{
    [Fact(Skip = "Requires live SQL Server and configured Azure resources")]
    public async Task SnapshotBuilderExtensionsTests()
    {
        var list = new List<SharePointFileInfoWithList>();
        var spList = new SiteList() { ServerRelativeUrl = $"/list{DateTime.Now.Ticks}" };

        const int INSERTS = 100;
        for (int i = 0; i < INSERTS; i++)
        {
            var siteUrl = "/site" + DateTime.Now.Ticks;
            var webUrl = siteUrl + "/web" + DateTime.Now.Ticks;
            var newDoc = new DocumentSiteWithMetadata
            {
                AccessCount = i,
                Author = $"Author {i}",
                List = spList,
                DriveId = DateTime.Now.Ticks.ToString(),
                FileSize = i,
                GraphItemId = DateTime.Now.Ticks.ToString(),
                VersionCount = i,
                SiteUrl = siteUrl,
                WebUrl = webUrl,
                ServerRelativeFilePath = webUrl + "/file.aspx",
                LastModified = DateTime.Now
            };

            list.Add(newDoc);
            Assert.True(newDoc.IsValidInfo);
        }

        using var db = new SPOColdStorageDbContext(_config!);
        var preInsert = await db.Files.CountAsync();
        await list.InsertFilesAsync(_config!, new StagingFilesMigrator(), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var postInsert = await db.Files.CountAsync();

        // Make sure we've actually inserted
        Assert.Equal(preInsert + INSERTS, postInsert);
    }
}
