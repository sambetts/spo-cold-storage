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

        // Repair step for databases created before the [MaxLength(450)] attributes
        // were added to SPFile / BaseSharePointFileInfo. In those builds EF mapped
        // string? to NVARCHAR(MAX), which SQL Server rejects as an index key column
        // (the original cause of "Column 'graph_item_id' ... is of a type that is
        // invalid for use as a key column in an index"). max_length = -1 in
        // sys.columns identifies NVARCHAR(MAX); shrink it to NVARCHAR(450) so the
        // filtered index below can be created.
        const string shrinkGraphIdColumnsSql = @"
IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('dbo.files')
             AND name = 'graph_item_id' AND max_length = -1)
    ALTER TABLE dbo.files ALTER COLUMN graph_item_id NVARCHAR(450) NULL;

IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('dbo.files')
             AND name = 'drive_id' AND max_length = -1)
    ALTER TABLE dbo.files ALTER COLUMN drive_id NVARCHAR(450) NULL;

IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('dbo.StagingFiles')
             AND name = 'GraphItemId' AND max_length = -1)
    ALTER TABLE dbo.StagingFiles ALTER COLUMN GraphItemId NVARCHAR(450) NULL;

IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('dbo.StagingFiles')
             AND name = 'DriveId' AND max_length = -1)
    ALTER TABLE dbo.StagingFiles ALTER COLUMN DriveId NVARCHAR(450) NULL;
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
        // Shrink any pre-existing NVARCHAR(MAX) columns to NVARCHAR(450) before
        // trying to index them. Must run as its own batch after the ADD COLUMN
        // batch above, otherwise SQL Server parses references to columns that
        // don't yet exist on a fresh DB.
        await context.Database.ExecuteSqlRawAsync(shrinkGraphIdColumnsSql);
        // Index creation must run after the columns exist and have an
        // index-compatible type; SQL Server compiles the whole batch up-front
        // and would otherwise fail on a fresh DB where the columns didn't
        // exist before this call.
        await context.Database.ExecuteSqlRawAsync(addUnanalyzedIndexSql);
    }
}
