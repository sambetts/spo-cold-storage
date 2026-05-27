using Entities.Configuration;
using Entities.DBEntities;

namespace Entities;

public class DbInitializer
{

    /// <summary>
    /// Creates new tenant DB if needed
    /// </summary>
    /// <returns>If DB was created</returns>
    public async static Task<bool> Init(SPOColdStorageDbContext context, DevConfig config)
    {
        context.Database.EnsureCreated();
        if (context.TargetSharePointSites.Any() || config == null)
        {
            return false;
        }

        // Add default data
        if (!string.IsNullOrEmpty(config.DefaultSharePointSite))
        {
            context.TargetSharePointSites.Add(new TargetMigrationSite { RootURL = config.DefaultSharePointSite });
            await context.SaveChangesAsync();
        }

        return true;
    }
}
