# SPO Cold Storage - Database Structure Documentation

## Overview
The SPO Cold Storage solution uses SQL Server with Entity Framework Core for data persistence. The database tracks migration targets, files, migration logs, and related SharePoint metadata.

## Database Context
- **DbContext Class**: `SPOColdStorageDbContext`
- **Location**: `Entities\SPOColdStorageDbContext.cs`
- **Provider**: SQL Server with retry-on-failure enabled
- **Command Timeout**: 12 hours (configured for long-running operations)

## Entity Relationship Diagram

```
target_migration_sites
    ?? (configuration only)

sites (1) ???> webs (many)
           ?
webs (1) ????> files (many)
                   ?
files (1) ???????????> file_migration_errors (0..1)
                   ?
files (1) ???????????> file_migrations_completed (0..1)
                   ?
files (many) <???????> file_directories (1) [optional]
                   ?
files (many) <???????> users (1) [last modified by]

staging_files (temporary import tracking)
```

## Tables

### 1. target_migration_sites
**Purpose**: Stores SharePoint sites that should be indexed and migrated to cold storage.

| Column Name | Data Type | Nullable | Description |
|------------|-----------|----------|-------------|
| id | int | No | Primary key (auto-increment) |
| root_url | nvarchar(max) | No | Root URL of the SharePoint site to migrate |
| filter_config_json | nvarchar(max) | No | JSON configuration for filtering lists/libraries and folders |

**Indexes**:
- Primary key on `id`

**Notes**:
- The `filter_config_json` column stores a serialized `SiteListFilterConfig` object
- If `filter_config_json` is empty, all content in the site is migrated
- Filter configuration allows selective migration of specific lists/libraries and folders

**Example filter_config_json**:
```json
{
  "rootURL": "https://contoso.sharepoint.com/sites/ProjectSite",
  "listFilterConfig": [
    {
      "listTitle": "Documents",
      "folderWhiteList": ["Archive", "Reports/*"]
    }
  ]
}
```

---

### 2. sites
**Purpose**: Stores SharePoint site collection URLs discovered during indexing.

| Column Name | Data Type | Nullable | Description |
|------------|-----------|----------|-------------|
| id | int | No | Primary key (auto-increment) |
| url | nvarchar(max) | No | Full URL of the SharePoint site collection |

**Indexes**:
- Primary key on `id`
- Unique index on `url`

**Notes**:
- URLs are stored in lowercase for consistency
- One site can have multiple webs (subsites)

---

### 3. webs
**Purpose**: Stores SharePoint webs (sites/subsites) within site collections.

| Column Name | Data Type | Nullable | Description |
|------------|-----------|----------|-------------|
| id | int | No | Primary key (auto-increment) |
| url | nvarchar(max) | No | Full URL of the SharePoint web |
| site_id | int | No | Foreign key to sites table |

**Indexes**:
- Primary key on `id`
- Unique index on `url`
- Foreign key on `site_id` references `sites(id)`

**Notes**:
- URLs are stored in lowercase
- Represents the immediate parent web of files

---

### 4. file_directories
**Purpose**: Lookup table for file directory paths to avoid duplicating directory strings.

| Column Name | Data Type | Nullable | Description |
|------------|-----------|----------|-------------|
| id | int | No | Primary key (auto-increment) |
| directory_path | nvarchar(500) | No | Full directory path where files are located |

**Indexes**:
- Primary key on `id`
- Unique index on `directory_path`

**Notes**:
- Optional relationship with files
- Helps normalize directory storage
- Maximum length: 500 characters

---

### 5. users
**Purpose**: Stores unique user email addresses for tracking file authors/modifiers.

| Column Name | Data Type | Nullable | Description |
|------------|-----------|----------|-------------|
| id | int | No | Primary key (auto-increment) |
| email | nvarchar(max) | No | User's email address (lowercase) |

**Indexes**:
- Primary key on `id`

**Notes**:
- Email addresses are stored in lowercase for consistency
- Used primarily for tracking last modified by user

---

### 6. files
**Purpose**: Core table storing metadata about files discovered in SharePoint.

| Column Name | Data Type | Nullable | Description |
|------------|-----------|----------|-------------|
| id | int | No | Primary key (auto-increment) |
| url | nvarchar(max) | No | Full SharePoint URL of the file |
| web_id | int | No | Foreign key to webs table |
| directory_id | int | Yes | Foreign key to file_directories table (optional) |
| access_count | int | Yes | Number of times file has been accessed (optional) |
| analysis_completed | datetime2 | Yes | Timestamp when file analysis was completed |
| last_modified | datetime2 | No | Last modified date from SharePoint |
| created_date | datetime2 | Yes | File creation date from SharePoint |
| last_modified_by_user_id | int | No | Foreign key to users table |
| version_count | int | No | Number of versions the file has (default: 0) |
| versions_total_size | bigint | No | Total size of all versions in bytes (default: 0) |
| file_size | bigint | No | Current file size in bytes (default: 0) |

**Indexes**:
- Primary key on `id`
- Unique index on `url`
- Foreign key on `web_id` references `webs(id)`
- Foreign key on `directory_id` references `file_directories(id)`
- Foreign key on `last_modified_by_user_id` references `users(id)`

**Notes**:
- File URLs are stored in lowercase
- `last_modified` is used to determine if a file needs re-migration
- Size fields use `bigint` to support very large files

---

### 7. file_migration_errors
**Purpose**: Logs fatal errors encountered during file migration attempts.

| Column Name | Data Type | Nullable | Description |
|------------|-----------|----------|-------------|
| id | int | No | Primary key (auto-increment) |
| file_id | int | No | Foreign key to files table |
| error | nvarchar(max) | No | Full error message and stack trace |
| timestamp | datetime2 | No | When the error occurred |

**Indexes**:
- Primary key on `id`
- Foreign key on `file_id` references `files(id)`

**Notes**:
- Stores the most recent error for each file
- Updated if file migration is retried and fails again
- Used for troubleshooting and monitoring

---

### 8. file_migrations_completed
**Purpose**: Logs successfully completed file migrations.

| Column Name | Data Type | Nullable | Description |
|------------|-----------|----------|-------------|
| id | int | No | Primary key (auto-increment) |
| file_id | int | No | Foreign key to files table |
| migrated | datetime2 | No | Timestamp when file was successfully migrated |

**Indexes**:
- Primary key on `id`
- Foreign key on `file_id` references `files(id)`

**Notes**:
- One record per file (updated if file is re-migrated)
- Used to track migration completion and prevent unnecessary re-migration
- Combined with `files.last_modified` to determine if re-migration is needed

---

### 9. staging_files (Staging Table)
**Purpose**: Temporary table for tracking files during import/discovery operations.

| Column Name | Data Type | Nullable | Description |
|------------|-----------|----------|-------------|
| id | int | No | Primary key (auto-increment) |
| ImportBlockId | uniqueidentifier | No | GUID identifying the import batch |
| Inserted | datetime2 | No | When the file was added to staging |
| [Additional BaseSharePointFileInfo properties] | Various | Varies | Inherited file metadata properties |

**Indexes**:
- Primary key on `id`

**Notes**:
- Inherits from `BaseSharePointFileInfo` class
- Used during indexing operations
- Temporary data structure for batching operations

---

## Key Relationships

### File Migration Tracking Flow
1. **Indexer** discovers files in SharePoint sites configured in `target_migration_sites`
2. Files are added to `files` table (or updated if URL exists)
3. Messages are sent to Service Bus queue for migration
4. **Migrator** processes queue messages:
   - Downloads file from SharePoint
   - Uploads to Azure Blob Storage
   - On success: creates/updates record in `file_migrations_completed`
   - On failure: creates/updates record in `file_migration_errors`

### Version Checking Logic
A file needs migration if:
- It doesn't exist in blob storage, OR
- No record exists in `file_migrations_completed`, OR
- `files.last_modified` is newer than the last successful migration

## Common Queries

### Find all migration targets
```sql
SELECT id, root_url, filter_config_json 
FROM target_migration_sites
ORDER BY root_url;
```

### Get migration status for a file
```sql
SELECT 
    f.url,
    f.last_modified,
    f.file_size,
    w.url AS web_url,
    u.email AS last_modified_by,
    c.migrated AS last_migration_date,
    e.error AS last_error
FROM files f
LEFT JOIN webs w ON f.web_id = w.id
LEFT JOIN users u ON f.last_modified_by_user_id = u.id
LEFT JOIN file_migrations_completed c ON c.file_id = f.id
LEFT JOIN file_migration_errors e ON e.file_id = f.id
WHERE f.url LIKE '%searchterm%'
ORDER BY f.last_modified DESC;
```

### Find files with migration errors
```sql
SELECT 
    f.url,
    f.file_size,
    e.error,
    e.timestamp AS error_time
FROM files f
INNER JOIN file_migration_errors e ON e.file_id = f.id
LEFT JOIN file_migrations_completed c ON c.file_id = f.id
WHERE c.id IS NULL  -- Not successfully migrated
ORDER BY e.timestamp DESC;
```

### Get migration statistics
```sql
SELECT 
    COUNT(DISTINCT f.id) AS total_files,
    COUNT(DISTINCT c.file_id) AS migrated_files,
    COUNT(DISTINCT e.file_id) AS error_files,
    SUM(f.file_size) AS total_size_bytes,
    SUM(CASE WHEN c.id IS NOT NULL THEN f.file_size ELSE 0 END) AS migrated_size_bytes
FROM files f
LEFT JOIN file_migrations_completed c ON c.file_id = f.id
LEFT JOIN file_migration_errors e ON e.file_id = f.id;
```

### Find largest files not yet migrated
```sql
SELECT TOP 100
    f.url,
    f.file_size,
    f.last_modified,
    w.url AS web_url
FROM files f
INNER JOIN webs w ON f.web_id = w.id
LEFT JOIN file_migrations_completed c ON c.file_id = f.id
WHERE c.id IS NULL
ORDER BY f.file_size DESC;
```

### Find files in a specific directory
```sql
SELECT 
    f.url,
    f.file_size,
    f.last_modified,
    d.directory_path,
    c.migrated
FROM files f
LEFT JOIN file_directories d ON f.directory_id = d.id
LEFT JOIN file_migrations_completed c ON c.file_id = f.id
WHERE d.directory_path LIKE '/sites/ProjectSite/Documents/%'
ORDER BY f.url;
```

## Entity Framework Migrations

### Creating Migrations
```bash
# From the solution root
Add-Migration -Name "MigrationName" -Project "Entities" -StartupProject "Tests" -Context SPOColdStorageDbContext
```

### Applying Migrations
```bash
# The application automatically applies migrations on startup
# Or manually:
Update-Database -Project "Entities" -StartupProject "Tests" -Context SPOColdStorageDbContext
```

### Generating Migration Script
```bash
Script-Migration -Project "Entities" -StartupProject "Tests" -From "PreviousMigration" -Context SPOColdStorageDbContext
```

## Database Initialization

The database is automatically created and migrated when the application starts. The connection string must be configured in:
- **Development**: User secrets or `appsettings.Development.json`
- **Production**: Environment variables or Azure App Service configuration

### Example Connection String
```
Server=spocoldstorage.database.windows.net,1433;Initial Catalog=spocoldstorage;Persist Security Info=False;User ID=sqladmin;Password=<your-password>;MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;
```

### Local Development Connection String
```
Server=(localdb)\\mssqllocaldb;Database=SPOColdStorageDbContextDev;Trusted_Connection=True;MultipleActiveResultSets=true
```

## Performance Considerations

### Indexes
All unique URL columns have indexes for fast lookups. Consider adding additional indexes for:
- `files.last_modified` if frequent date-range queries
- `files.file_size` if frequent size-based queries
- `file_migrations_completed.migrated` for time-series analysis

### Query Optimization
- URLs are stored in lowercase to ensure case-insensitive uniqueness
- The `files` table can grow large (millions of records) - ensure proper indexing
- Consider partitioning strategies for very large deployments (>10M files)

### Maintenance
- Regularly monitor `file_migration_errors` table size
- Archive old migration logs if needed
- Consider cleanup policies for `staging_files` table

## Backup and Recovery

### Recommended Backup Strategy
- **Full backups**: Daily
- **Differential backups**: Every 6 hours
- **Transaction log backups**: Every 15 minutes (for point-in-time recovery)

### Critical Data Priority
1. `target_migration_sites` - Configuration data
2. `files` + `file_migrations_completed` - Migration state
3. `file_migration_errors` - Troubleshooting data
4. `sites`, `webs`, `users`, `file_directories` - Metadata (can be rebuilt)

## Troubleshooting

### Common Issues

**Issue**: Duplicate key errors on `files.url`
- **Cause**: Attempting to insert file with existing URL
- **Solution**: Check `files` table first, update existing record instead of insert

**Issue**: Foreign key constraint violations
- **Cause**: Parent records (web, site, user) not created before file record
- **Solution**: Ensure proper order of operations in `DbExtentions.GetDbFileForFileInfo`

**Issue**: Migration stuck/long command timeout
- **Cause**: Large migration operation
- **Solution**: Command timeout is set to 12 hours by default in `SPOColdStorageDbContext`

## Security Considerations

- **SQL Authentication**: Currently uses SQL username/password authentication
- **Azure AD Authentication**: Can be migrated to use Managed Identity (see README.md RBAC section)
- **Encryption**: Enable Always Encrypted for sensitive columns if needed
- **Access Control**: Limit database user permissions to minimum required (see RBAC roles in README.md)

## See Also
- [Main README](README.md) - Setup and configuration instructions
- [RBAC Configuration](README.md#configure-rbac-roles-for-azure-resources) - Role-based access control setup
- [Performance Tuning](README.md#performance-tuning) - Optimization strategies
