using AwesomeAssertions;
using Models.ColdStorage;
using Xunit;

namespace Migration.Engine.Tests.Lifecycle;

/// <summary>
/// Version-history preservation end to end (issue #66): manifest schema/back-compat,
/// oldest-first replay ordering, major/minor detection and the "one archive unit"
/// blob layout that post-restore cleanup relies on (issue #64).
/// </summary>
public class VersionHistoryPreservationTests
{
    // ---- manifest schema + back-compat --------------------------------------

    [Fact]
    public void LegacyManifest_WithoutSchemaVersion_IsNotMistakenForCurrent()
    {
        // Written before issue #66: no schemaVersion, no hashes, no captureComplete.
        const string legacyJson = """
        {"versions":[{"versionId":"1.0","blobPath":"h/x/a.docx.versions/1.0","size":10,"lastModifiedUtc":"2023-01-01T00:00:00Z"}]}
        """;

        var parsed = VersionManifest.TryParse(legacyJson);

        parsed.Should().NotBeNull();
        // The property initialiser must NOT let an old manifest claim to be current —
        // otherwise we'd trust validation metadata that was never written.
        parsed!.SchemaVersion.Should().Be(VersionManifest.LegacySchemaVersion);
        parsed.IsLegacy.Should().BeTrue();
        parsed.CaptureComplete.Should().BeFalse();
        parsed.Count.Should().Be(1);
    }

    [Fact]
    public void CurrentManifest_RoundTrips_AllValidationMetadata()
    {
        var manifest = new VersionManifest
        {
            SchemaVersion = VersionManifest.CurrentSchemaVersion,
            BaseBlobPath = "h/x/a.docx",
            CapturedAtUtc = new DateTime(2024, 5, 1, 12, 0, 0, DateTimeKind.Utc),
            CaptureComplete = true,
            Versions =
            {
                new ArchivedVersion
                {
                    VersionId = "2.1",
                    VersionLabel = "2.1",
                    IsMajor = false,
                    BlobPath = "h/x/a.docx.versions/2.1",
                    Size = 42,
                    LastModifiedUtc = new DateTime(2024, 4, 1, 0, 0, 0, DateTimeKind.Utc),
                    CheckInComment = "tweaked the chart",
                    ContentMd5Base64 = "1B2M2Y8AsgTpgAmY7PhCfg==",
                },
            },
        };

        var parsed = VersionManifest.TryParse(manifest.ToJson());

        parsed.Should().NotBeNull();
        parsed!.IsLegacy.Should().BeFalse();
        parsed.CaptureComplete.Should().BeTrue();
        parsed.BaseBlobPath.Should().Be("h/x/a.docx");
        var v = parsed.Versions.Single();
        v.VersionLabel.Should().Be("2.1");
        v.IsMajor.Should().BeFalse();
        v.CheckInComment.Should().Be("tweaked the chart");
        v.ContentMd5Base64.Should().Be("1B2M2Y8AsgTpgAmY7PhCfg==");
    }

    // ---- replay ordering ----------------------------------------------------

    [Fact]
    public void SortOldestFirst_OrdersNumerically_NotLexically()
    {
        // A plain string sort puts "10.0" before "2.0", which would replay history
        // out of order and leave the wrong content as the latest version.
        var manifest = new VersionManifest
        {
            Versions =
            {
                Version("10.0", new DateTime(2024, 1, 5, 0, 0, 0, DateTimeKind.Utc)),
                Version("2.0", new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc)),
                Version("1.0", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            },
        };

        manifest.SortOldestFirst();

        manifest.Versions.Select(v => v.VersionLabel).Should().ContainInOrder("1.0", "2.0", "10.0");
    }

    [Fact]
    public void SortOldestFirst_OrdersMinorVersionsWithinAMajor()
    {
        var manifest = new VersionManifest
        {
            Versions =
            {
                Version("2.10", new DateTime(2024, 1, 4, 0, 0, 0, DateTimeKind.Utc)),
                Version("2.2", new DateTime(2024, 1, 3, 0, 0, 0, DateTimeKind.Utc)),
                Version("1.0", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            },
        };

        manifest.SortOldestFirst();

        manifest.Versions.Select(v => v.VersionLabel).Should().ContainInOrder("1.0", "2.2", "2.10");
    }

    [Fact]
    public void SortOldestFirst_FallsBackToTimestamp_WhenLabelsAreNotParsable()
    {
        // Version ids fall back to the version URL when SharePoint gives no label.
        var older = new ArchivedVersion { VersionId = "_vti_history/1", LastModifiedUtc = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        var newer = new ArchivedVersion { VersionId = "_vti_history/2", LastModifiedUtc = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc) };
        var manifest = new VersionManifest { Versions = { newer, older } };

        manifest.SortOldestFirst();

        manifest.Versions[0].Should().BeSameAs(older);
    }

    // ---- major / minor ------------------------------------------------------

    [Theory]
    [InlineData("1.0", true)]
    [InlineData("12.0", true)]
    [InlineData("1.3", false)]
    [InlineData("0.1", false)]
    // An unparsable label is treated as major: a library with only major versioning
    // labels everything "N.0", so defaulting to major is the safe reading.
    [InlineData("_vti_history/512", true)]
    [InlineData(null, true)]
    public void IsMajorLabel_DetectsPublishedVersions(string? label, bool expected)
        => VersionManifest.IsMajorLabel(label).Should().Be(expected);

    // ---- archive unit (issue #64) -------------------------------------------

    [Fact]
    public void VersionFolderPrefix_CoversEveryVersionBlob_ForCleanup()
    {
        const string baseKey = "contoso.sharepoint.com/sites/x/Shared Documents/a.docx";
        var prefix = VersionBlobLayout.VersionFolderPrefix(baseKey);

        // Post-restore cleanup enumerates by this prefix, so every version blob must
        // start with it or the sidecar would be orphaned forever.
        VersionBlobLayout.ForVersion(baseKey, "1.0").Should().StartWith(prefix);
        VersionBlobLayout.ForVersion(baseKey, "27.3").Should().StartWith(prefix);
        prefix.Should().Be($"{baseKey}.versions/");
    }

    [Fact]
    public void VersionArtifacts_AreRecognised_SoBlobDrivenRestoreSkipsThem()
    {
        const string baseKey = "h/sites/x/Shared Documents/a.docx";

        VersionBlobLayout.IsVersionArtifact(VersionBlobLayout.ForVersion(baseKey, "1.0")).Should().BeTrue();
        VersionBlobLayout.IsVersionArtifact(VersionBlobLayout.ManifestKey(baseKey)).Should().BeTrue();
        // The real file must NOT be mistaken for a sidecar, or it would never restore.
        VersionBlobLayout.IsVersionArtifact(baseKey).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void VersionFolderPrefix_RejectsAnEmptyBaseKey(string? baseKey)
    {
        // Guard: an empty prefix would enumerate — and delete — the entire container.
        var act = () => VersionBlobLayout.VersionFolderPrefix(baseKey!);
        act.Should().Throw<ArgumentException>();
    }

    private static ArchivedVersion Version(string label, DateTime modifiedUtc) => new()
    {
        VersionId = label,
        VersionLabel = label,
        IsMajor = VersionManifest.IsMajorLabel(label),
        BlobPath = $"h/x/a.docx.versions/{label}",
        LastModifiedUtc = modifiedUtc,
    };
}
