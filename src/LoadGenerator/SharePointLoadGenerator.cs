using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SharePoint.Client;
using Migration.Engine;
using Migration.Engine.Utils;
using System.Text;

namespace LoadGenerator;

internal class SharePointLoadGenerator(Options options, ILogger logger)
{
    private readonly Options _options = options;
    private readonly ILogger _debugTracer = logger ?? NullLogger.Instance;

    public async Task CreateFiles(int fileCount)
    {
        int filesAdded = 0;

        const int MAX_FILES_PER_THREAD = 500;

        var threadsNeeded = fileCount / MAX_FILES_PER_THREAD;
        if (threadsNeeded == 0)
        {
            threadsNeeded = 1;
        }
        var tasks = new List<Task>();

        for (int threadIndex = 0; threadIndex < threadsNeeded; threadIndex++)
        {
            var filesToInsert = MAX_FILES_PER_THREAD;
            if (threadIndex == threadsNeeded - 1)
            {
                filesToInsert = fileCount - filesAdded;
            }

            // Multi-thread the file create
            tasks.Add(AddFiles(filesAdded, filesToInsert, threadIndex));
            filesAdded += MAX_FILES_PER_THREAD;

#if DEBUG
            Console.Write($"+#{threadIndex}/{threadsNeeded}...");
#endif
        }
        await Task.WhenAll([.. tasks]);

    }

    private async Task AddFiles(int fileStartIndex, int filesToInsert, int threadIndex)
    {
        var ctx = await AuthUtils.GetClientContext(_options.TargetWeb!, _options.TenantId!, _options.ClientID!, _options.ClientSecret!, _options.KeyVaultUrl!, _options.BaseServerAddress!, _debugTracer);

        var targetLists = await GetAllListsAllWebs(ctx);

        for (int i = 0; i < filesToInsert; i++)
        {
            foreach (var list in targetLists)
            {
                try
                {
                    if (list.BaseType == BaseType.GenericList)
                    {
                        await AddFileToCustomList(list, ctx);
                    }
                    else if (list.BaseType == BaseType.DocumentLibrary)
                    {
                        await AddFileToDocLib(list, ctx);
                    }
                    Console.WriteLine($"{threadIndex}: {i}/{filesToInsert}");
                }
                catch (System.Net.WebException ex)
                {
                    Console.WriteLine($"Got error on thread {threadIndex} creating file: {ex.Message}.");
                }
                catch (ServerException ex)
                {
                    Console.WriteLine($"Got server error on thread {threadIndex} creating file: {ex.Message}.");
                }
            }
        }
    }

    public async Task<IEnumerable<List>> GetAllListsAllWebs(ClientContext ctx)
    {
        var results = new List<List>();
        var rootWeb = ctx.Web;
        ctx.Load(rootWeb);
        ctx.Load(rootWeb.Webs);
        await ctx.ExecuteQueryAsyncWithThrottleRetries(_debugTracer);

        results.AddRange(await GetAllLists(rootWeb, ctx));

        foreach (var subSweb in rootWeb.Webs)
        {
            results.AddRange(await GetAllLists(subSweb, ctx));
        }

        return results;
    }

    private async Task<IEnumerable<List>> GetAllLists(Web web, ClientContext ctx)
    {
        var results = new List<List>();
        ctx.Load(web.Lists);
        ctx.Load(web.Webs);
        await ctx.ExecuteQueryAsync();

        foreach (var list in web.Lists)
        {
            ctx.Load(list, l => l.BaseType, l => l.IsSystemList);
            ctx.Load(list.RootFolder);
            ctx.Load(list, l => l.RootFolder.Name);

            var addList = false;
            try
            {
                await ctx.ExecuteQueryAsyncWithThrottleRetries(_debugTracer);
                addList = true;
            }
            catch (ServerUnauthorizedAccessException)
            {
                Console.WriteLine($"Couldn't read '{list.Title}' - Unauthorized Access");
            }

            // Only upload to safe lists
            if (addList && !list.IsSystemList && !list.Hidden)
            {
                results.Add(list);
            }
        }

        return results;
    }

    private async Task AddFileToDocLib(List list, ClientContext ctx)
    {
        await list.SaveFile(ctx, $"test{DateTime.Now.Ticks}.txt", Encoding.UTF8.GetBytes("bum"), _debugTracer);
    }

    private async Task AddFileToCustomList(List list, ClientContext ctx)
    {
        var newName = DateTime.Now.Ticks.ToString();
        var newItemCreateInfo = new ListItemCreationInformation();
        var oListItem = list.AddItem(newItemCreateInfo);
        oListItem["Title"] = newName;

        oListItem.Update();

        await ctx.ExecuteQueryAsyncWithThrottleRetries(_debugTracer);

        var attInfo = new AttachmentCreationInformation
        {
            FileName = newName + ".txt",
            ContentStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("bum"))
        };

        Attachment att = oListItem.AttachmentFiles.Add(attInfo); //Add to File

        ctx.Load(att);

        try
        {
            await ctx.ExecuteQueryAsyncWithThrottleRetries(_debugTracer);
        }
        catch (ServerException ex)
        {
            Console.WriteLine($"Got unexpected error saving against list '{list.Title}': {ex.Message}");
        }
    }
}
