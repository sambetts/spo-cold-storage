using Microsoft.Extensions.Logging;
using Entities;
using Migration.Engine;
using Migration.Engine.Utils;

Console.WriteLine("SPO Cold Storage - SharePoint Indexer");

var config = ConsoleUtils.GetConfigurationWithDefaultBuilder<Program>();
ConsoleUtils.PrintCommonStartupDetails();

using var loggerFactory = ConsoleUtils.CreateLoggerFactory(config, "Indexer");
var logger = loggerFactory.CreateLogger<SharePointContentIndexer>();

// Init DB
using (var db = new SPOColdStorageDbContext(config))
{
    await DbInitializer.Init(db, config.DevConfig);
}

// Start discovery
var discovery = new SharePointContentIndexer(config, logger);
await discovery.StartMigrateAllSites();

Console.WriteLine("\nAll sites scanned. Finished indexing.");
