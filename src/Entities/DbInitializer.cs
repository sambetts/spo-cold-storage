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

        await ApplyColdStorageSchemaUpgradesAsync(context);
    }

    /// <summary>
    /// Idempotent DDL for the cold-storage lifecycle tables. Run after
    /// <see cref="DbContext.Database"/>.EnsureCreated so existing deployments
    /// pick up the new tables/indexes without an EF migration.
    /// </summary>
    private static async Task ApplyColdStorageSchemaUpgradesAsync(SPOColdStorageDbContext context)
    {
        const string createContainersSql = @"
IF OBJECT_ID('dbo.cold_storage_containers', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.cold_storage_containers (
        id INT IDENTITY(1,1) PRIMARY KEY,
        name NVARCHAR(128) NOT NULL,
        display_name NVARCHAR(256) NOT NULL,
        blob_container_name NVARCHAR(63) NOT NULL,
        storage_account_uri NVARCHAR(2048) NOT NULL CONSTRAINT DF_cold_storage_containers_uri DEFAULT(''),
        is_default BIT NOT NULL CONSTRAINT DF_cold_storage_containers_default DEFAULT(0),
        sort_order INT NOT NULL CONSTRAINT DF_cold_storage_containers_sort DEFAULT(0),
        description NVARCHAR(MAX) NULL,
        CONSTRAINT UX_cold_storage_containers_name UNIQUE(name)
    );
END;

IF OBJECT_ID('dbo.cold_storage_container_acls', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.cold_storage_container_acls (
        id INT IDENTITY(1,1) PRIMARY KEY,
        container_id INT NOT NULL,
        principal_id NVARCHAR(64) NOT NULL,
        principal_type INT NOT NULL,
        principal_display NVARCHAR(256) NULL,
        can_browse BIT NOT NULL CONSTRAINT DF_csca_browse DEFAULT(0),
        can_migrate BIT NOT NULL CONSTRAINT DF_csca_migrate DEFAULT(0),
        can_restore BIT NOT NULL CONSTRAINT DF_csca_restore DEFAULT(0),
        CONSTRAINT FK_csca_container FOREIGN KEY(container_id)
            REFERENCES dbo.cold_storage_containers(id) ON DELETE CASCADE,
        CONSTRAINT UX_csca_principal UNIQUE(container_id, principal_id, principal_type)
    );
END;";

        const string createJobsSql = @"
IF OBJECT_ID('dbo.migration_jobs', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.migration_jobs (
        job_id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
        operation INT NOT NULL,
        requested_by_upn NVARCHAR(256) NOT NULL,
        site_url NVARCHAR(2048) NOT NULL,
        web_url NVARCHAR(2048) NOT NULL CONSTRAINT DF_jobs_web DEFAULT(''),
        container_id INT NULL,
        conflict_behavior INT NOT NULL CONSTRAINT DF_jobs_conflict DEFAULT(0),
        status INT NOT NULL CONSTRAINT DF_jobs_status DEFAULT(0),
        created_at DATETIME2 NOT NULL CONSTRAINT DF_jobs_created DEFAULT(SYSUTCDATETIME()),
        updated_at DATETIME2 NOT NULL CONSTRAINT DF_jobs_updated DEFAULT(SYSUTCDATETIME()),
        completed_at DATETIME2 NULL,
        summary NVARCHAR(1024) NULL,
        CONSTRAINT FK_jobs_container FOREIGN KEY(container_id)
            REFERENCES dbo.cold_storage_containers(id)
    );
END;

IF OBJECT_ID('dbo.migration_job_items', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.migration_job_items (
        item_id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
        job_id UNIQUEIDENTIFIER NOT NULL,
        item_kind INT NOT NULL,
        recursive BIT NOT NULL CONSTRAINT DF_items_recursive DEFAULT(0),
        sp_site_url NVARCHAR(2048) NOT NULL,
        sp_web_url NVARCHAR(2048) NOT NULL,
        sp_server_relative_url NVARCHAR(2048) NOT NULL,
        sp_subpath NVARCHAR(2048) NOT NULL CONSTRAINT DF_items_subpath DEFAULT(''),
        file_size BIGINT NOT NULL CONSTRAINT DF_items_size DEFAULT(0),
        source_last_modified DATETIME2 NULL,
        container_id INT NULL,
        blob_container_name NVARCHAR(63) NULL,
        blob_path NVARCHAR(1024) NULL,
        blob_url NVARCHAR(2048) NULL,
        placeholder_server_relative_url NVARCHAR(2048) NULL,
        content_md5_base64 NVARCHAR(64) NULL,
        permissions_json NVARCHAR(MAX) NULL,
        status INT NOT NULL CONSTRAINT DF_items_status DEFAULT(0),
        last_error NVARCHAR(2048) NULL,
        attempts INT NOT NULL CONSTRAINT DF_items_attempts DEFAULT(0),
        validated_at DATETIME2 NULL,
        copied_at DATETIME2 NULL,
        source_deleted_at DATETIME2 NULL,
        placeholder_created_at DATETIME2 NULL,
        restored_at DATETIME2 NULL,
        completed_at DATETIME2 NULL,
        created_at DATETIME2 NOT NULL CONSTRAINT DF_items_created DEFAULT(SYSUTCDATETIME()),
        updated_at DATETIME2 NOT NULL CONSTRAINT DF_items_updated DEFAULT(SYSUTCDATETIME()),
        CONSTRAINT FK_items_job FOREIGN KEY(job_id)
            REFERENCES dbo.migration_jobs(job_id) ON DELETE CASCADE,
        CONSTRAINT FK_items_container FOREIGN KEY(container_id)
            REFERENCES dbo.cold_storage_containers(id)
    );
END;

IF OBJECT_ID('dbo.migration_job_logs', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.migration_job_logs (
        id INT IDENTITY(1,1) PRIMARY KEY,
        job_id UNIQUEIDENTIFIER NOT NULL,
        item_id UNIQUEIDENTIFIER NULL,
        timestamp DATETIME2 NOT NULL CONSTRAINT DF_jlogs_ts DEFAULT(SYSUTCDATETIME()),
        level INT NOT NULL CONSTRAINT DF_jlogs_level DEFAULT(0),
        status INT NOT NULL CONSTRAINT DF_jlogs_status DEFAULT(0),
        message NVARCHAR(4000) NOT NULL,
        exception NVARCHAR(MAX) NULL,
        CONSTRAINT FK_jlogs_job FOREIGN KEY(job_id)
            REFERENCES dbo.migration_jobs(job_id) ON DELETE CASCADE,
        CONSTRAINT FK_jlogs_item FOREIGN KEY(item_id)
            REFERENCES dbo.migration_job_items(item_id)
    );
END;";

        const string addColdStorageItemColumnsSql = @"
IF COL_LENGTH('dbo.migration_job_items', 'original_created_by') IS NULL
    ALTER TABLE dbo.migration_job_items ADD original_created_by NVARCHAR(256) NULL;

IF COL_LENGTH('dbo.migration_job_items', 'original_modified_by') IS NULL
    ALTER TABLE dbo.migration_job_items ADD original_modified_by NVARCHAR(256) NULL;

IF COL_LENGTH('dbo.migration_job_items', 'original_created') IS NULL
    ALTER TABLE dbo.migration_job_items ADD original_created DATETIME2 NULL;
";

        const string createExclusionsSql = @"
IF OBJECT_ID('dbo.cold_storage_exclusions', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.cold_storage_exclusions (
        id INT IDENTITY(1,1) PRIMARY KEY,
        site_url NVARCHAR(2048) NULL,
        server_relative_prefix NVARCHAR(2048) NULL,
        description NVARCHAR(512) NULL,
        enabled BIT NOT NULL CONSTRAINT DF_cs_excl_enabled DEFAULT(1),
        created_by NVARCHAR(256) NULL,
        created_at DATETIME2 NOT NULL CONSTRAINT DF_cs_excl_created DEFAULT(SYSUTCDATETIME())
    );
END;";

        const string createColdStorageIndexesSql = @"
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_migration_job_items_job' AND object_id = OBJECT_ID('dbo.migration_job_items'))
    CREATE INDEX IX_migration_job_items_job ON dbo.migration_job_items(job_id);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_migration_job_items_sp_url' AND object_id = OBJECT_ID('dbo.migration_job_items'))
    CREATE INDEX IX_migration_job_items_sp_url ON dbo.migration_job_items(sp_server_relative_url);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_migration_job_items_job_status' AND object_id = OBJECT_ID('dbo.migration_job_items'))
    CREATE INDEX IX_migration_job_items_job_status ON dbo.migration_job_items(job_id, status);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_migration_job_logs_job_ts' AND object_id = OBJECT_ID('dbo.migration_job_logs'))
    CREATE INDEX IX_migration_job_logs_job_ts ON dbo.migration_job_logs(job_id, timestamp);
";

        await context.Database.ExecuteSqlRawAsync(createContainersSql);
        await context.Database.ExecuteSqlRawAsync(createJobsSql);
        await context.Database.ExecuteSqlRawAsync(createExclusionsSql);
        await context.Database.ExecuteSqlRawAsync(addColdStorageItemColumnsSql);
        await context.Database.ExecuteSqlRawAsync(createColdStorageIndexesSql);
    }
}
