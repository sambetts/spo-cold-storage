using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;
using Entities;
using Entities.Configuration;
using Web.Services;

namespace Web.Server;

public class Program
{
    public async static Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddControllers().AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
            // Serialize enums (MigrationLifecycleStatus, ColdStorageItemKind, ConflictBehavior, MigrationOperationKind)
            // as their member names rather than ints. The SPFx TypeScript client compares against the string names
            // ("Queued", "ColdStorageMigrationCompleted", …) so without this converter every status check on the
            // client silently mismatches.
            options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
            // Tolerate non-ISO date strings on request bodies (e.g. SharePoint's
            // locale-formatted Modified display value sent by SPFx). See class docs.
            options.JsonSerializerOptions.Converters.Add(new Web.Server.Json.LenientNullableDateTimeConverter());
        });

        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();

        // CORS for the SPFx command set in SharePoint. SPFx code runs in the user's
        // browser at the tenant SharePoint origin (https://<tenant>.sharepoint.com)
        // and calls this API directly with an AAD bearer token, so we need to allow
        // that origin in the CORS preflight. The React SPA shipped with this same
        // Web App is served from the same origin and doesn't need CORS.
        //
        // Allowed origins come from configuration:
        //   Cors:AllowedOrigins   (comma- or semicolon-separated list, preferred)
        //   BaseServerAddress     (fallback - the tenant SharePoint root)
        // The deploy script writes BaseServerAddress automatically, so the default
        // setup works without any extra config.
        const string SpfxCorsPolicy = "SpfxOrigins";
        var allowedOrigins = new List<string>();
        var corsConfig = builder.Configuration["Cors:AllowedOrigins"];
        if (!string.IsNullOrWhiteSpace(corsConfig))
        {
            allowedOrigins.AddRange(corsConfig.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        var baseServerAddress = builder.Configuration["BaseServerAddress"];
        if (!string.IsNullOrWhiteSpace(baseServerAddress))
        {
            allowedOrigins.Add(baseServerAddress.TrimEnd('/'));
        }
        allowedOrigins = allowedOrigins.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        builder.Services.AddCors(o => o.AddPolicy(SpfxCorsPolicy, p =>
        {
            if (allowedOrigins.Count > 0)
            {
                p.WithOrigins(allowedOrigins.ToArray())
                 .AllowAnyHeader()
                 .AllowAnyMethod()
                 .WithExposedHeaders("WWW-Authenticate");
            }
        }));

        var config = new Config(builder.Configuration);
        builder.Services.AddSingleton(config);

        // Wire up Azure Monitor (Application Insights) via OpenTelemetry when configured.
        if (config.HaveAppInsightsConfigured)
        {
            var connectionString = config.AppInsightsInstrumentationKey.Contains('=', StringComparison.Ordinal)
                ? config.AppInsightsInstrumentationKey
                : $"InstrumentationKey={config.AppInsightsInstrumentationKey}";

            builder.Services.AddOpenTelemetry().UseAzureMonitor(o => o.ConnectionString = connectionString);
        }

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));

        // UsageStatsReport
        builder.Services.AddDbContext<SPOColdStorageDbContext>(options =>
            options.UseSqlServer(config.ConnectionStrings.SQLConnectionString));

        // Cold-storage services backing the new SPFx-facing API surface.
        builder.Services.AddScoped<ISiteOwnerAuthorizationService, SiteOwnerAuthorizationService>();
        builder.Services.AddScoped<IContainerAccessService, ContainerAccessService>();
        builder.Services.AddScoped<IColdStorageAdminAuthorizationService, ColdStorageAdminAuthorizationService>();
        builder.Services.AddSingleton<Migration.Engine.Migration.IArchiveExclusionSource>(sp =>
            new Migration.Engine.Migration.DbArchiveExclusionSource(
                sp.GetRequiredService<Config>(),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger("ArchiveExclusions")));
        builder.Services.AddSingleton<Migration.Engine.Migration.IFileReadActivitySource>(sp =>
            new Migration.Engine.Migration.DbFileReadActivitySource(
                sp.GetRequiredService<Config>(),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger("ArchiveReadActivity")));
        builder.Services.AddSingleton<Migration.Engine.Migration.IArchiveEligibilityEvaluator, Migration.Engine.Migration.ArchiveEligibilityEvaluator>();
        builder.Services.AddSingleton<IColdStorageBusPublisher, ColdStorageBusPublisher>();

        var app = builder.Build();

        // Ensure DB
        var optionsBuilder = new DbContextOptionsBuilder<SPOColdStorageDbContext>();
        optionsBuilder.UseSqlServer(config.ConnectionStrings.SQLConnectionString);

        using (var db = new SPOColdStorageDbContext(config))
        {
            var logger = LoggerFactory.Create(c =>
            {
                c.AddConsole();
            }).CreateLogger("DB init");
            logger.LogInformation($"Using SQL connection-string: {config.ConnectionStrings.SQLConnectionString}");

            await DbInitializer.Init(db, config.DevConfig);

            // Auto-seed a default cold-storage container + admin ACL on a fresh DB.
            // Without this seed step, the SPFx Migrate / Restore commands fail with
            // "no permission to migrate to any configured cold-storage container"
            // because container access is gated by the cold_storage_container_acls
            // table, not by SharePoint permissions or storage RBAC. The seeder is
            // idempotent and a no-op once at least one container exists.
            await ColdStorageContainerSeeder.SeedDefaultIfEmptyAsync(db, builder.Configuration, logger);
        }

        // https://learn.microsoft.com/en-us/visualstudio/javascript/tutorial-asp-net-core-with-react?view=vs-2022#publish-the-project
        app.UseDefaultFiles();
        app.UseStaticFiles();

        // Configure the HTTP request pipeline.
        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        // UseCors must run after UseRouting (implicit here) and before UseAuthentication /
        // UseAuthorization / MapControllers so CORS preflight OPTIONS requests aren't
        // rejected by the auth middleware.
        app.UseCors(SpfxCorsPolicy);

        app.UseAuthorization();

        app.MapControllers();

        // SPA fallback: anything that wasn't matched by static files or a controller
        // route falls back to index.html so client-side router paths like
        // /cold-storage/download/:itemId work on a cold navigation (placeholder
        // .url file double-clicked from SharePoint). Without this, deep links
        // return 404 because IIS can't find a physical file at that path.
        app.MapFallbackToFile("index.html");

        app.Run();
    }
}
