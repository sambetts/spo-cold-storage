using Microsoft.EntityFrameworkCore;
using Entities;
using Entities.Configuration;
using Entities.DBEntities;
using Migration.Engine.Utils.Extensions;
using Migration.Engine.Utils.Http;
using Models;

using Microsoft.Extensions.Logging;
namespace Migration.Engine.SnapshotBuilder;

public class TenantModelBuilder(Config config, ILogger ILogger) : BaseComponent(config, ILogger)
{
    private readonly List<Task> _updateTasks = [];
    private readonly StagingFilesMigrator _stagingFilesMigrator = new();

    public async Task Build()
    {
        using var db = new SPOColdStorageDbContext(this._config);
        // Clean staging table 1st
        await _stagingFilesMigrator.CleanStagingAll(db);

        // Start analysis
        var sitesToAnalyse = await db.TargetSharePointSites.ToListAsync();

        if (sitesToAnalyse.Count == 0)
        {
            // Analyse all site-collections
            var url = $"https://graph.microsoft.com/beta/sites";
            var httpClient = new GraphThrottledHttpClient(_config, false, _logger);

            var sites = await httpClient.LoadGraphPageable<SiteCollectionsResult, SiteCollection>(url, _logger);

            var defaultSitesAll = new List<TargetMigrationSite>();
            if (sites != null)
            {
                foreach (var sp in sites)
                {
                    defaultSitesAll.Add(new TargetMigrationSite { RootURL = sp.WebUrl });
                }
            }

            await AnalyseSites(defaultSitesAll);
        }
        else
            _logger.LogWarning($"Taking snapshot of {sitesToAnalyse.Count} site(s):");
        await AnalyseSites(sitesToAnalyse);
    }

    async Task AnalyseSites(IEnumerable<TargetMigrationSite> sitesToAnalyse)
    {
        foreach (var s in sitesToAnalyse)
        {
            _logger.LogInformation($"--BEGIN: {s.RootURL}:");
            await StartSiteAnalysisAsync(s);
        }
    }

    private async Task<SiteSnapshotModel> StartSiteAnalysisAsync(TargetMigrationSite site)
    {
        var s = new SiteModelBuilder(base._config, base._logger, site);

        var siteModel = await s.Build(100,
            async filesDiscovered => await filesDiscovered.InsertFilesAsync(_config, _stagingFilesMigrator, _logger),
            async updatedFiles => await UpdateFiles(updatedFiles)
        );

        await Task.WhenAll(_updateTasks);
        _logger.LogInformation($"--FINISHED: {site.RootURL}");
        return siteModel;
    }

    Task UpdateFiles(List<DocumentSiteWithMetadata> updatedFiles)
    {
        _updateTasks.Add(Task.Run(async () =>
        {
            int updated = 0, inserted = 0;
            using var db = new SPOColdStorageDbContext(this._config);
            _logger.LogInformation($"Updating {updatedFiles.Count} files to DB from downloaded metadata");
            foreach (var updatedFile in updatedFiles)
            {
                var r = await UpdateStats(updatedFile, db);
                if (r == StatsSaveResult.New) inserted++;
                else if (r == StatsSaveResult.Updated) updated++;
            }
            await db.SaveChangesAsync();
        }));
        return Task.CompletedTask;
    }

    async Task<StatsSaveResult> UpdateStats(DocumentSiteWithMetadata updatedFile, SPOColdStorageDbContext db)
    {
        var results = StatsSaveResult.New;
        var existingFile = await db.Files.Where(f => f.Url == updatedFile.ServerRelativeFilePath).SingleOrDefaultAsync();
        if (existingFile == null)
        {
            _logger.LogInformation($"Got update for a file that we haven't inserted yet...");
            existingFile = await updatedFile.GetDbFileForFileInfo(db);
        }
        if (existingFile.AnalysisCompleted.HasValue)
        {
            results = StatsSaveResult.Updated;
        }

        // Set stats
        existingFile.AnalysisCompleted = DateTime.Now;
        existingFile.AccessCount = updatedFile.AccessCount;
        existingFile.VersionCount = updatedFile.VersionCount;
        existingFile.VersionHistorySize = updatedFile.VersionHistorySize;
        existingFile.LastModified = updatedFile.LastModified;
        existingFile.CreatedDate = updatedFile.CreatedDate;
        existingFile.FileSize = updatedFile.FileSize;

        return results;
    }

    enum StatsSaveResult
    {
        New,
        Updated
    }

}
