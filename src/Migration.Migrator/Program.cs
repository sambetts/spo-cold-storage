using Microsoft.Extensions.Logging;
using Migration.Engine;
using Migration.Engine.Utils;

Console.WriteLine("SPO Cold Storage - Migrator Listener");
Console.WriteLine("This app will listen for messages from service-bus and handle them when they arrive, untill you close this application.");

var config = ConsoleUtils.GetConfigurationWithDefaultBuilder<Program>();
ConsoleUtils.PrintCommonStartupDetails();

using var loggerFactory = ConsoleUtils.CreateLoggerFactory(config, "Migrator");
var logger = loggerFactory.CreateLogger<ServiceBusMigrationListener>();

var listener = new ServiceBusMigrationListener(config, logger);
await listener.ListenForFilesToMigrate();
