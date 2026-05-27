-- =============================================================================
-- Migration: Add drive_id and graph_item_id to files (and StagingFiles).
--
-- Why: Snapshot builder needs to call /drives/{driveId}/items/{itemId}/analytics
-- for files in dbo.files that still have NULL analysis_completed / NULL
-- access_count. Without the Graph IDs we cannot retry analytics on later runs,
-- so any file that the analytics phase didn't process the first time stays
-- NULL forever.
--
-- Both columns are added as nullable so they're safe to apply to a populated
-- DB. They'll get backfilled on the next snapshot run by MergeStagingFiles.sql
-- (which now UPDATEs existing rows when staging has the IDs).
-- =============================================================================

-- StagingFiles: holds Graph IDs in flight between in-memory model and merge.
IF COL_LENGTH('dbo.StagingFiles', 'DriveId') IS NULL
BEGIN
    ALTER TABLE dbo.StagingFiles ADD DriveId NVARCHAR(450) NULL;
END;

IF COL_LENGTH('dbo.StagingFiles', 'GraphItemId') IS NULL
BEGIN
    ALTER TABLE dbo.StagingFiles ADD GraphItemId NVARCHAR(450) NULL;
END;

-- files: persists the IDs so analytics can be re-run for un-analyzed rows.
IF COL_LENGTH('dbo.files', 'drive_id') IS NULL
BEGIN
    ALTER TABLE dbo.files ADD drive_id NVARCHAR(450) NULL;
END;

IF COL_LENGTH('dbo.files', 'graph_item_id') IS NULL
BEGIN
    ALTER TABLE dbo.files ADD graph_item_id NVARCHAR(450) NULL;
END;

-- Helper index for the re-enqueue query that finds files needing analytics.
-- Filtered index keeps it small (only rows still to process).
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = 'IX_files_analysis_completed_null' AND object_id = OBJECT_ID('dbo.files')
)
BEGIN
    CREATE INDEX IX_files_analysis_completed_null
        ON dbo.files (graph_item_id, drive_id)
        WHERE analysis_completed IS NULL;
END;
