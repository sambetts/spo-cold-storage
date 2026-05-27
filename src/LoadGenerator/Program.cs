using CommandLine;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using LoadGenerator;
using Migration.Engine;

// Example args:
// --web https://m365x352268.sharepoint.com/sites/MigrationHost --kv https://spocoldstoragedev.vault.azure.net --ClientID 8179f97c-bfd6-4ca0-9b69-a02fc2430121
//  --ClientSecret xxxxxxxxxx --BaseServerAddress https://m365x352268.sharepoint.com --TenantId ffcdb539-892e-4eef-94f6-0d9851c479ba --FileCount 6000

using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole());
var logger = loggerFactory.CreateLogger("LoadGenerator");

await Parser.Default.ParseArguments<Options>(args).WithParsedAsync(async o =>
{
    Console.WriteLine($"Running against {o.TargetWeb}");

    // Warn if just running the app
    if (!System.Diagnostics.Debugger.IsAttached)
    {
        Console.WriteLine($"\nLAST WARNING! This program will be highly destructive to your web '{o.TargetWeb}'. " +
        $"\nPress any key to confirm you understand '{o.TargetWeb}' will be destroyed...");
        Console.ReadKey();
    }

    var ctx = await AuthUtils.GetClientContext(o.TargetWeb!, o.TenantId!, o.ClientID!, o.ClientSecret!, o.KeyVaultUrl!, o.BaseServerAddress!, logger);
    var gen = new SharePointLoadGenerator(o, logger);
    await gen.CreateFiles(o.FileCount);

});

Console.WriteLine("All done");
