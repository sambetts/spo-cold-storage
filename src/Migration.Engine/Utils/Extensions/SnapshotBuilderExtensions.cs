using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Entities;
using Entities.Configuration;
using Entities.DBEntities;
using Migration.Engine.SnapshotBuilder;
using Models;

using Microsoft.Extensions.Logging;
namespace Migration.Engine.Utils.Extensions;

public static class SnapshotBuilderExtensions
{
    private static readonly SemaphoreSlim ss = new(1, 1);
    public static async Task InsertFilesAsync(this List<SharePointFileInfoWithList> files, Config config, StagingFilesMigrator stagingFilesMigrator, ILogger logger)
    {
        await ss.WaitAsync();

        try
        {
            using var db = new SPOColdStorageDbContext(config);
            var executionStrategy = db.Database.CreateExecutionStrategy();

            try
            {
                await executionStrategy.Execute(async () =>
                {
                    using var trans = await db.Database.BeginTransactionAsync();
                    var blockGuid = Guid.NewGuid();
                    var inserted = DateTime.Now;

                    // Insert staging data
                    var stagingFiles = new List<StagingTempFile>();
                    foreach (var insertedFile in files)
                    {
                        if (insertedFile.IsValidInfo)
                        {
                            var f = new StagingTempFile(insertedFile, blockGuid, inserted);
                            stagingFiles.Add(f);
                        }
                        else
                        {
                            logger.LogWarning($"Warning: found invalid file '{insertedFile.FullSharePointUrl}'. Ignoring");
                        }
                    }
                    await db.StagingFiles.AddRangeAsync(stagingFiles);
                    await db.SaveChangesAsync();

                    // Merge from staging to tables
                    await stagingFilesMigrator.MigrateBlockAndCleanFromStaging(db, blockGuid);

                    await trans.CommitAsync();
                });

            }
            catch (SqlException ex)
            {
                logger.LogError(ex, "Unhandled exception");
                logger.LogCritical($"Got fatal SQL error saving file info block to SQL: {ex.Message}");
            }
        }
        finally
        {
            ss.Release();
        }
    }
}
