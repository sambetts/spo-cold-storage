using Microsoft.Extensions.Logging.Abstractions;
using Migration.Engine.SnapshotBuilder;
using Migration.Engine.Utils.Extensions;
using Models;

namespace LoadGenerator;

internal class SqlSaveLoadGenerator
{
    public static async Task Go(Entities.Configuration.Config config)
    {
        var tasks = new List<Task>();

        StagingFilesMigrator stagingFilesMigrator = new();
        for (int i = 0; i < 100; i++)
        {
            await Insert(config, 1000, stagingFilesMigrator);
        }

        await Task.WhenAll(tasks);
    }

    public static async Task Insert(Entities.Configuration.Config config, int docsToInsert, StagingFilesMigrator stagingFilesMigrator)
    {

        var list = new List<SharePointFileInfoWithList>();
        var spList = new SiteList() { ServerRelativeUrl = $"/list{DateTime.Now.Ticks}" };

        for (int i = 0; i < docsToInsert; i++)
        {
            list.Add(new DocumentSiteWithMetadata
            {
                AccessCount = i,
                Author = $"Author {i}",
                List = spList,
                DriveId = DateTime.Now.Ticks.ToString(),
                FileSize = i,
                GraphItemId = DateTime.Now.Ticks.ToString(),
                VersionCount = i
            });
        }

        Console.WriteLine("Saving fakes");

        await list.InsertFilesAsync(config, stagingFilesMigrator, NullLogger.Instance);

    }
}
