using Models;
using Models.ColdStorage;
using Xunit;

namespace Migration.Engine.Tests.Lifecycle;

public class ColdStorageBusEnvelopeTests
{
    private static ColdStorageBusEnvelope ValidMigrate() => new()
    {
        JobId = Guid.NewGuid(),
        ItemId = Guid.NewGuid(),
        Operation = MigrationOperationKind.Migrate,
        ContainerName = "archive",
        RequestedByUpn = "alice@contoso.com",
        File = new BaseSharePointFileInfo
        {
            SiteUrl = "https://contoso.sharepoint.com/sites/x",
            WebUrl = "https://contoso.sharepoint.com/sites/x",
            ServerRelativeFilePath = "/sites/x/Shared Documents/y.docx",
            FileSize = 1024,
            LastModified = DateTime.UtcNow,
        },
    };

    private static ColdStorageBusEnvelope ValidRestore() => new()
    {
        JobId = Guid.NewGuid(),
        ItemId = Guid.NewGuid(),
        Operation = MigrationOperationKind.Restore,
        ContainerName = "archive",
        RequestedByUpn = "alice@contoso.com",
        ConflictBehavior = ConflictBehavior.Fail,
        RestoreTarget = new PlaceholderRestoreTarget
        {
            SiteUrl = "https://contoso.sharepoint.com/sites/x",
            WebUrl = "https://contoso.sharepoint.com/sites/x",
            PlaceholderServerRelativeUrl = "/sites/x/Shared Documents/y.docx.url",
            OriginalServerRelativeUrl = "/sites/x/Shared Documents/y.docx",
        },
    };

    [Fact]
    public void IsValid_True_ForCompleteMigrateEnvelope()
        => Assert.True(ValidMigrate().IsValid);

    [Fact]
    public void IsValid_True_ForCompleteRestoreEnvelope()
        => Assert.True(ValidRestore().IsValid);

    [Fact]
    public void IsValid_False_WhenMigrateMissingFile()
    {
        var envelope = ValidMigrate();
        envelope.File = null;
        Assert.False(envelope.IsValid);
    }

    [Fact]
    public void IsValid_False_WhenRestoreMissingTarget()
    {
        var envelope = ValidRestore();
        envelope.RestoreTarget = null;
        Assert.False(envelope.IsValid);
    }

    [Fact]
    public void IsValid_False_WhenJobIdEmpty()
    {
        var envelope = ValidMigrate();
        envelope.JobId = Guid.Empty;
        Assert.False(envelope.IsValid);
    }

    [Fact]
    public void IsValid_False_WhenContainerMissing()
    {
        var envelope = ValidMigrate();
        envelope.ContainerName = string.Empty;
        Assert.False(envelope.IsValid);
    }
}

