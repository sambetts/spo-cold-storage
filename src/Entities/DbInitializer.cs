using Entities.Configuration;
using Entities.DBEntities;
using Microsoft.EntityFrameworkCore;

namespace Entities;

public class DbInitializer
{

    /// <summary>
    /// Creates new tenant DB if needed
    /// </summary>
    /// <returns>If DB was created</returns>
    public async static Task<bool> Init(SPOColdStorageDbContext context, DevConfig config)
    {
        context.Database.EnsureCreated();

        // EnsureCreated() only handles brand-new databases. Existing deployments
        // need schema changes applied separately. Each block below is idempotent
        // (guarded by COL_LENGTH / sys.indexes) so it's safe to run every startup.
        await ApplySchemaUpgradesAsync(context);

        if (context.TargetSharePointSites.Any() || config == null)
        {
            return false;
        }

        // Add default data
        if (!string.IsNullOrEmpty(config.DefaultSharePointSite))
        {
            context.TargetSharePointSites.Add(new TargetMigrationSite { RootURL = config.DefaultSharePointSite });
            await context.SaveChangesAsync();
        }

        return true;
    }

    /// <summary>
    /// Idempotent schema upgrades for databases that pre-date newer columns.
    /// Each statement is guarded so running this on an already-up-to-date DB
    /// is a no-op. Mirrors the canonical script in
    /// <c>src/Migration Scripts/2 - Add graph IDs to files.sql</c>.
    /// </summary>
    private static async Task ApplySchemaUpgradesAsync(SPOColdStorageDbContext context)
    {
        // Migration 2: graph_item_id / drive_id on files + StagingFiles.
        // Required so the snapshot builder can retry analytics on rows where
        // analysis_completed IS NULL without re-crawling the entire drive.
        const string addGraphIdColumnsSql = @"
IF COL_LENGTH('dbo.StagingFiles', 'DriveId') IS NULL
    ALTER TABLE dbo.StagingFiles ADD DriveId NVARCHAR(450) NULL;

IF COL_LENGTH('dbo.StagingFiles', 'GraphItemId') IS NULL
    ALTER TABLE dbo.StagingFiles ADD GraphItemId NVARCHAR(450) NULL;

IF COL_LENGTH('dbo.files', 'drive_id') IS NULL
    ALTER TABLE dbo.files ADD drive_id NVARCHAR(450) NULL;

IF COL_LENGTH('dbo.files', 'graph_item_id') IS NULL
    ALTER TABLE dbo.files ADD graph_item_id NVARCHAR(450) NULL;
";

        const string addUnanalyzedIndexSql = @"
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = 'IX_files_analysis_completed_null' AND object_id = OBJECT_ID('dbo.files')
)
    CREATE INDEX IX_files_analysis_completed_null
        ON dbo.files (graph_item_id, drive_id)
        WHERE analysis_completed IS NULL;
";

        await context.Database.ExecuteSqlRawAsync(addGraphIdColumnsSql);
        // Index creation must run after the columns exist; SQL Server compiles
        // the whole batch up-front and would otherwise fail on a fresh DB
        // where the columns didn't exist before this call.
        await context.Database.ExecuteSqlRawAsync(addUnanalyzedIndexSql);
    }
}
