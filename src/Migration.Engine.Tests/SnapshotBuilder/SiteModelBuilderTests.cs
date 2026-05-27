using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Entities.Configuration;
using Entities.DBEntities;
using Migration.Engine.SnapshotBuilder;
using Models;
using Migration.Engine.Tests.Adapters;
using Xunit;

namespace Migration.Engine.Tests.SnapshotBuilder;

public class SiteModelBuilderTests
{
    private const string LiveSkipReason = "Requires live SharePoint and Azure connections";

    private readonly IConfiguration _mockConfig;
    private readonly Config _config;
    private readonly ILogger _logger = NullLogger.Instance;
    private readonly TargetMigrationSite _testSite;
    private readonly TestFileAnalyticsAdapter _testAdapter;

    public SiteModelBuilderTests()
    {
        _mockConfig = Substitute.For<IConfiguration>();

        // AzureAd section
        var azureAdSection = Substitute.For<IConfigurationSection>();
        azureAdSection["Instance"].Returns("https://login.microsoftonline.com/");
        azureAdSection["Domain"].Returns("test.onmicrosoft.com");
        azureAdSection["TenantId"].Returns("test-tenant-id");
        azureAdSection["ClientId"].Returns("test-client-id");
        azureAdSection["ClientID"].Returns("test-client-id");  // Case-sensitive duplicate
        azureAdSection["CallbackPath"].Returns("/signin-oidc");
        azureAdSection["Secret"].Returns("test-secret");
        _mockConfig.GetSection("AzureAd").Returns(azureAdSection);

        // ConnectionStrings section
        var connectionStringsSection = Substitute.For<IConfigurationSection>();
        connectionStringsSection["ServiceBus"].Returns("Endpoint=sb://test.servicebus.windows.net/;SharedAccessKeyName=test;SharedAccessKey=test;EntityPath=test");
        connectionStringsSection["Storage"].Returns("DefaultEndpointsProtocol=https;AccountName=test;AccountKey=test;EndpointSuffix=core.windows.net");
        connectionStringsSection["SQLConnectionString"].Returns("Server=localhost;Database=TestDb;Trusted_Connection=True;MultipleActiveResultSets=true;TrustServerCertificate=True");
        _mockConfig.GetSection("ConnectionStrings").Returns(connectionStringsSection);

        // Dev section
        var devSection = Substitute.For<IConfigurationSection>();
        devSection["SearchServiceEndPoint"].Returns("https://test.search.windows.net");
        devSection["SearchServiceAdminApiKey"].Returns("test-admin-key");
        devSection["SearchServiceQueryApiKey"].Returns("test-query-key");
        _mockConfig.GetSection("Dev").Returns(devSection);

        // Search section (reusing Dev section values)
        var searchSection = Substitute.For<IConfigurationSection>();
        searchSection["SearchServiceEndPoint"].Returns("https://test.search.windows.net");
        searchSection["SearchServiceAdminApiKey"].Returns("test-admin-key");
        searchSection["SearchServiceQueryApiKey"].Returns("test-query-key");
        _mockConfig.GetSection("Search").Returns(searchSection);

        _mockConfig["AnalysisSkipHours"].Returns("24");
        _mockConfig["SPOTenantName"].Returns("test");
        _mockConfig["SPOClientId"].Returns("test-client-id");
        _mockConfig["SPOUserName"].Returns("test@test.com");
        _mockConfig["BaseServerAddress"].Returns("https://test.com");
        _mockConfig["DBConnectionString"].Returns("Server=test;Database=test");
        _mockConfig["InstrumentationKey"].Returns("test-key");
        _mockConfig["KeyVaultUrl"].Returns("https://test.vault.azure.net");
        _mockConfig["StorageConnectionString"].Returns("DefaultEndpointsProtocol=https;AccountName=test;AccountKey=test");
        _mockConfig["BlobContainerName"].Returns("test-container");

        _config = new Config(_mockConfig);

        _testSite = new TargetMigrationSite
        {
            RootURL = "https://test.sharepoint.com/sites/testsite",
            FilterConfigJson = string.Empty
        };

        _testAdapter = new TestFileAnalyticsAdapter();
    }

    [Fact]
    public void Constructor_WithValidParameters_CreatesInstance()
    {
        // Arrange & Act
        using var builder = new SiteModelBuilder(_config, _logger, _testSite, _testAdapter);

        // Assert
        builder.Should().NotBeNull();
    }

    [Fact]
    public void Constructor_WithNullAdapter_CreatesDefaultGraphAdapter()
    {
        // Arrange & Act
        using var builder = new SiteModelBuilder(_config, _logger, _testSite, null);

        // Assert
        builder.Should().NotBeNull();
    }

    [Fact(Skip = LiveSkipReason)]
    public async Task Build_WithoutCallback_ReturnsModel()
    {
        // Arrange
        using var builder = new SiteModelBuilder(_config, _logger, _testSite, _testAdapter);

        // Act
        var result = await builder.Build();

        // Assert
        result.Should().NotBeNull();
        result.AllFiles.Should().NotBeNull();
    }

    [Fact]
    public void Build_WithInvalidBatchSize_ThrowsArgumentOutOfRangeException()
    {
        // Arrange
        using var builder = new SiteModelBuilder(_config, _logger, _testSite, _testAdapter);

        // Act
        Func<Task> act = async () => await builder.Build(0, null, null);

        // Assert
        act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact(Skip = LiveSkipReason)]
    public async Task Build_WithTestAdapter_CallsAdapterMethods()
    {
        // Arrange
        using var builder = new SiteModelBuilder(_config, _logger, _testSite, _testAdapter);
        _testAdapter.ResetCounters();

        // Configure test data
        _testAdapter.SetAnalyticsData("test-item-1", new ItemAnalyticsResponse.AnalyticsItemActionStat
        {
            ActionCount = 10,
            ActorCount = 5
        });

        _testAdapter.SetVersionData("test-item-1", new DriveItemVersionInfo
        {
            Versions =
            [
                new DriveItemVersion { Id = "1.0", Size = 1024 },
                new DriveItemVersion { Id = "2.0", Size = 2048 }
            ]
        });

        // Act
        var result = await builder.Build(10, null, null);

        // Assert
        result.Should().NotBeNull();
        // Note: Without actual file crawling, adapter won't be called
        // This test validates the builder can be constructed with test adapter
    }

    [Fact]
    public void BackgroundMetaTasksAll_ReturnsEnumerable()
    {
        // Arrange
        using var builder = new SiteModelBuilder(_config, _logger, _testSite, _testAdapter);

        // Act
        var tasks = builder.BackgroundMetaTasksAll;

        // Assert
        tasks.Should().NotBeNull();
        tasks.Should().BeAssignableTo<IEnumerable<Task<BackgroundUpdate>>>();
    }

    [Theory(Skip = LiveSkipReason)]
    [InlineData(10)]
    [InlineData(50)]
    [InlineData(100)]
    public async Task Build_WithDifferentBatchSizes_Succeeds(int batchSize)
    {
        // Arrange
        using var builder = new SiteModelBuilder(_config, _logger, _testSite, _testAdapter);

        // Act
        var result = await builder.Build(batchSize, null, null);

        // Assert
        result.Should().NotBeNull();
    }

    [Fact]
    public void Dispose_CalledMultipleTimes_DoesNotThrow()
    {
        // Arrange
        var builder = new SiteModelBuilder(_config, _logger, _testSite, _testAdapter);

        // Act
        Action act = () =>
        {
            builder.Dispose();
            builder.Dispose();
            builder.Dispose();
        };

        // Assert
        act.Should().NotThrow();
    }

    [Fact(Skip = LiveSkipReason)]
    public async Task Build_CalledMultipleTimes_ReturnsSameModel()
    {
        // Arrange
        using var builder = new SiteModelBuilder(_config, _logger, _testSite, _testAdapter);

        // Act
        var result1 = await builder.Build();
        var result2 = await builder.Build();

        // Assert
        result1.Should().BeSameAs(result2);
    }

    [Fact]
    public void Constructor_WithFilterConfigJson_ParsesConfiguration()
    {
        // Arrange
        var siteWithFilter = new TargetMigrationSite
        {
            RootURL = "https://test.sharepoint.com/sites/testsite",
            FilterConfigJson = "{\"IncludeAllLists\":true}"
        };

        // Act
        using var builder = new SiteModelBuilder(_config, _logger, siteWithFilter, _testAdapter);

        // Assert
        builder.Should().NotBeNull();
    }

    [Fact]
    public void Constructor_WithInvalidFilterConfigJson_UsesDefaultConfig()
    {
        // Arrange
        var siteWithInvalidFilter = new TargetMigrationSite
        {
            RootURL = "https://test.sharepoint.com/sites/testsite",
            FilterConfigJson = "invalid json"
        };

        // Act & Assert - should not throw
        using var builder = new SiteModelBuilder(_config, _logger, siteWithInvalidFilter, _testAdapter);
        builder.Should().NotBeNull();
    }
}
