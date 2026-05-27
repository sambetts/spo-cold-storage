using Microsoft.EntityFrameworkCore;
using Entities;
using System.Reflection;

namespace Migration.Engine.SnapshotBuilder;
/// <summary>
/// Migrates data from StagingFiles to proper tables + lookups with raw SQL for speed.
/// </summary>
public class StagingFilesMigrator
{
    private static string _sqlTemplate = string.Empty;

    /// <summary>
    /// Migrate from staging to real tables a specific block ID (guid). Staging cleaned after migrate.
    /// </summary>
    public async Task MigrateBlockAndCleanFromStaging(SPOColdStorageDbContext context, Guid blockGuid)
    {
        if (string.IsNullOrEmpty(_sqlTemplate))
        {
            _sqlTemplate = ReadResource("Migration.Engine.SQL.MergeStagingFiles.sql");
        }
        var blockSql = _sqlTemplate.Replace("--[blockset]--", $"SET @blockGuid='{blockGuid}';");

        await context.Database.ExecuteSqlRawAsync(blockSql);
    }

    public async Task CleanStagingAll(SPOColdStorageDbContext context)
    {
        var blockSql = "delete from [StagingFiles]";
        await context.Database.ExecuteSqlRawAsync(blockSql);
    }

    protected string ReadResource(string resourcePath)
    {
        var assembly = Assembly.GetExecutingAssembly();

        // Format: "{Namespace}.{Folder}.{filename}.{Extension}"
        _ = assembly.GetManifestResourceNames();

        using var stream = assembly.GetManifestResourceStream(resourcePath);
        if (stream != null)
        {
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(resourcePath), $"No resource found by name '{resourcePath}'");
        }
    }

}
