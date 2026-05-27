using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;
using Entities;
using Entities.Configuration;

namespace Web.Server;

public class Program
{
    public async static Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddControllers().AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
        });

        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();

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

        app.UseAuthorization();

        app.MapControllers();

        app.Run();
    }
}
