using Microsoft.Extensions.Logging;
using Entities;
using Migration.Engine;
using Migration.Engine.SnapshotBuilder;
using Migration.Engine.Utils;

Console.WriteLine("SPO Cold Storage - Site Snapshot Builder");
Console.WriteLine("This app will build new space snapshots for configured site-collections.");

var config = ConsoleUtils.GetConfigurationWithDefaultBuilder<Program>();
ConsoleUtils.PrintCommonStartupDetails();

using var loggerFactory = ConsoleUtils.CreateLoggerFactory(config, "SnapshotBuilder");
var logger = loggerFactory.CreateLogger<TenantModelBuilder>();

// Init DB
using (var db = new SPOColdStorageDbContext(config))
{
    await DbInitializer.Init(db, config.DevConfig);
}

var analyser = new TenantModelBuilder(config, logger);
await analyser.Build();

Console.WriteLine("\nAll sites scanned. Finished building snapshot.");