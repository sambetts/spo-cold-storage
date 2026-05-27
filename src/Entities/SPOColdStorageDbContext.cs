using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Entities.Configuration;
using Entities.DBEntities;

namespace Entities;
/// <summary>
/// SQL model.
/// </summary>
public class SPOColdStorageDbContext : DbContext
{
    private readonly Config? _config;

    public SPOColdStorageDbContext(Config config)
    {
        this._config = config;
        SetCommandTimeout();
    }
    public SPOColdStorageDbContext(DbContextOptions<SPOColdStorageDbContext> options, Config? config) : base(options)
    {
        this._config = config;
        SetCommandTimeout();
    }

    public SPOColdStorageDbContext(SqlConnection con) : base()
    {
    }

    void SetCommandTimeout()
    {
        const int ONE_HOUR = 3600;
        Database.SetCommandTimeout(ONE_HOUR * 12);
    }

    // Migrations:
    // Add-Migration -Name "FilterConfig" -Project "Entities" -StartupProject "Tests" -Context SPOColdStorageDbContext
    // Script-Migration -Project "Entities" -StartupProject "Tests" -From "PreviousMigration" -Context SPOColdStorageDbContext

    public DbSet<TargetMigrationSite> TargetSharePointSites { get; set; } = null!;
    public DbSet<Site> Sites { get; set; } = null!;
    public DbSet<Web> Webs { get; set; } = null!;
    public DbSet<SPFile> Files { get; set; } = null!;
    public DbSet<FileDirectory> FileDirectories { get; set; } = null!;
    public DbSet<User> Users { get; set; } = null!;
    public DbSet<StagingTempFile> StagingFiles { get; set; } = null!;
    public DbSet<FileMigrationErrorLog> FileMigrationErrors { get; set; } = null!;
    public DbSet<FileMigrationCompletedLog> FileMigrationsCompleted { get; set; } = null!;
    public DbSet<DriveDeltaToken> DriveDeltaTokens { get; set; } = null!;
    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<SPFile>()
            .HasIndex(u => u.Url)
            .IsUnique();

        builder.Entity<Web>()
            .HasIndex(u => u.Url)
            .IsUnique();

        builder.Entity<Site>()
            .HasIndex(u => u.Url)
            .IsUnique();

        builder.Entity<FileDirectory>()
            .HasIndex(u => u.DirectoryPath)
            .IsUnique();

        builder.Entity<DriveDeltaToken>()
            .HasKey(d => d.DriveId);

        builder.Entity<DriveDeltaToken>()
            .HasIndex(d => d.SiteId);
    }
    protected override void OnConfiguring(DbContextOptionsBuilder options)
        => options.UseSqlServer(_config!.ConnectionStrings.SQLConnectionString, op => op.EnableRetryOnFailure());
}

/// <summary>
/// For EF migrations
/// </summary>
public class DbContextFactory : IDesignTimeDbContextFactory<SPOColdStorageDbContext>
{
    public SPOColdStorageDbContext CreateDbContext(string[] args)
    {
        var c = new List<KeyValuePair<string, string>>
        {
            new KeyValuePair<string, string>("KeyVaultUrl", "Unit testing"),
            new KeyValuePair<string, string>("BaseServerAddress", "Unit testing"),
            new KeyValuePair<string, string>("ConnectionStrings:SQLConnectionString", "Server=(localdb)\\mssqllocaldb;Database=SPOColdStorageDbContextDev;Trusted_Connection=True;MultipleActiveResultSets=true")
        };

        var configCollection = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddUserSecrets(System.Reflection.Assembly.GetEntryAssembly()!, true)
            .AddJsonFile("appsettings.json", true).Build();

        var optionsBuilder = new DbContextOptionsBuilder<SPOColdStorageDbContext>();
        optionsBuilder.UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=BuildSyncDev;Trusted_Connection=True;MultipleActiveResultSets=true");

        return new SPOColdStorageDbContext(new Config(configCollection));
    }
}
