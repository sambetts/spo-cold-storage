using CommandLine;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using LoadGenerator;
using Migration.Engine;

// Example args:
// --web https://contoso.sharepoint.com/sites/MigrationHost --kv https://spocoldstoragedev.vault.azure.net --ClientID 00000000-0000-0000-0000-000000000000
//  --ClientSecret xxxxxxxxxx --BaseServerAddress https://contoso.sharepoint.com --TenantId 11111111-1111-1111-1111-111111111111 --FileCount 6000

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
