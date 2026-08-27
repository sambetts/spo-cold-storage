using Entities.DBEntities.ColdStorage;
using Models;
using Models.ColdStorage;
using Xunit;

namespace Migration.Engine.Tests.Lifecycle;

/// <summary>
/// The worker acts app-only with Sites.FullControl.All, so before executing ANY envelope it
/// re-asserts that every SharePoint path in it belongs to the site its job was authorized against.
/// This is the backstop that also covers messages already sitting on the bus and rows written
/// before the API-side scope checks existed. It must fail closed.
/// </summary>
public class EnvelopeScopeGuardTests
{
    private const string Site = "https://contoso.sharepoint.com/sites/A";

    private static MigrationJob Job(string? siteUrl, MigrationOperationKind op = MigrationOperationKind.Restore)
        => new() { JobId = Guid.NewGuid(), Operation = op, SiteUrl = siteUrl ?? string.Empty };

    private static ColdStorageBusEnvelope Restore(
        string siteUrl = Site,
        string? webUrl = null,
        string destination = "/sites/A/Shared Documents/a.docx",
        string? placeholder = "/sites/A/Shared Documents/a.docx.url",
        string? blobPath = "contoso.sharepoint.com/sites/A/Shared Documents/a.docx")
        => new()
        {
            JobId = Guid.NewGuid(),
            ItemId = Guid.NewGuid(),
            Operation = MigrationOperationKind.Restore,
            ContainerName = "cold",
            RestoreTarget = new PlaceholderRestoreTarget
            {
                SiteUrl = siteUrl,
                WebUrl = webUrl ?? siteUrl,
                OriginalServerRelativeUrl = destination,
                PlaceholderServerRelativeUrl = placeholder ?? string.Empty,
                BlobPath = blobPath,
            },
        };

    private static ColdStorageBusEnvelope Migrate(
        string siteUrl = Site,
        string path = "/sites/A/Shared Documents/a.docx")
        => new()
        {
            JobId = Guid.NewGuid(),
            ItemId = Guid.NewGuid(),
            Operation = MigrationOperationKind.Migrate,
            ContainerName = "cold",
            File = new BaseSharePointFileInfo
            {
                SiteUrl = siteUrl,
                WebUrl = siteUrl,
                ServerRelativeFilePath = path,
                LastModified = DateTime.UtcNow,
                FileSize = 10,
            },
        };

    [Fact]
    public void InScope_restore_is_allowed()
    {
        Assert.True(ColdStorageMessageProcessor.IsEnvelopeInJobScope(Restore(), Job(Site), out _));
    }

    [Fact]
    public void InScope_migrate_is_allowed()
    {
        Assert.True(ColdStorageMessageProcessor.IsEnvelopeInJobScope(
            Migrate(), Job(Site, MigrationOperationKind.Migrate), out _));
    }

    [Fact]
    public void Sub_web_is_allowed()
    {
        var env = Restore(webUrl: "https://contoso.sharepoint.com/sites/A/team");
        Assert.True(ColdStorageMessageProcessor.IsEnvelopeInJobScope(env, Job(Site), out _));
    }

    [Fact]
    public void Missing_job_is_refused()
    {
        Assert.False(ColdStorageMessageProcessor.IsEnvelopeInJobScope(Restore(), null, out _));
    }

    [Fact]
    public void Job_without_a_site_is_refused_fail_closed()
    {
        Assert.False(ColdStorageMessageProcessor.IsEnvelopeInJobScope(Restore(), Job(""), out _));
    }

    [Fact]
    public void Restore_targeting_another_site_is_refused()
    {
        var env = Restore(siteUrl: "https://contoso.sharepoint.com/sites/B");
        Assert.False(ColdStorageMessageProcessor.IsEnvelopeInJobScope(env, Job(Site), out _));
    }

    [Fact]
    public void Restore_destination_outside_the_site_is_refused()
    {
        var env = Restore(destination: "/sites/B/Shared Documents/a.docx");
        Assert.False(ColdStorageMessageProcessor.IsEnvelopeInJobScope(env, Job(Site), out _));
    }

    /// <summary>
    /// The exact gap review round 2 found: an in-scope destination paired with another site's
    /// placeholder, which is read and deleted on SharePoint.
    /// </summary>
    [Fact]
    public void Restore_with_an_out_of_scope_placeholder_is_refused()
    {
        var env = Restore(placeholder: "/sites/B/Shared Documents/secret.docx.url");
        Assert.False(ColdStorageMessageProcessor.IsEnvelopeInJobScope(env, Job(Site), out _));
    }

    /// <summary>
    /// The other half of that gap: an in-scope destination pointed at another site's archive, which
    /// would disclose site B's content into site A.
    /// </summary>
    [Fact]
    public void Restore_with_an_out_of_scope_blob_is_refused()
    {
        var env = Restore(blobPath: "contoso.sharepoint.com/sites/B/Shared Documents/secret.docx");
        Assert.False(ColdStorageMessageProcessor.IsEnvelopeInJobScope(env, Job(Site), out _));
    }

    [Fact]
    public void Restore_with_an_out_of_scope_web_is_refused()
    {
        var env = Restore(webUrl: "https://contoso.sharepoint.com/sites/B");
        Assert.False(ColdStorageMessageProcessor.IsEnvelopeInJobScope(env, Job(Site), out _));
    }

    [Fact]
    public void Migrate_path_outside_the_site_is_refused()
    {
        var env = Migrate(path: "/sites/B/Shared Documents/a.docx");
        Assert.False(ColdStorageMessageProcessor.IsEnvelopeInJobScope(
            env, Job(Site, MigrationOperationKind.Migrate), out _));
    }

    [Fact]
    public void Migrate_on_another_host_is_refused()
    {
        var env = Migrate(siteUrl: "https://evil.example/sites/A");
        Assert.False(ColdStorageMessageProcessor.IsEnvelopeInJobScope(
            env, Job(Site, MigrationOperationKind.Migrate), out _));
    }

    [Fact]
    public void Envelope_with_no_payload_is_refused()
    {
        var env = new ColdStorageBusEnvelope
        {
            JobId = Guid.NewGuid(),
            ItemId = Guid.NewGuid(),
            Operation = MigrationOperationKind.Restore,
            ContainerName = "cold",
        };
        Assert.False(ColdStorageMessageProcessor.IsEnvelopeInJobScope(env, Job(Site), out _));
    }
}
