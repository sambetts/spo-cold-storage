using AwesomeAssertions;
using Models.ColdStorage;
using Xunit;

namespace Migration.Engine.Tests.Lifecycle;

/// <summary>
/// Unique-permission preservation across archive + restore (issue #67). The snapshot is
/// taken before the source is deleted and is the only record of the original ACL, so its
/// round-trip and its "inherited means leave it alone" semantics are what matter.
/// </summary>
public class ArchivedPermissionsTests
{
    [Fact]
    public void RoundTrips_Assignments()
    {
        var snapshot = new ArchivedPermissions
        {
            HadUniqueRoleAssignments = true,
            CapturedAtUtc = new DateTime(2024, 5, 1, 9, 0, 0, DateTimeKind.Utc),
            Assignments =
            {
                new ArchivedRoleAssignment
                {
                    LoginName = "i:0#.f|membership|ada@contoso.com",
                    Title = "Ada Lovelace",
                    Roles = { "Contribute" },
                },
                new ArchivedRoleAssignment
                {
                    LoginName = "c:0t.c|tenant|9f1a",
                    Title = "Finance Owners",
                    Roles = { "Full Control", "Design" },
                },
            },
        };

        var parsed = ArchivedPermissions.TryParse(snapshot.ToJson());

        parsed.Should().NotBeNull();
        parsed!.HadUniqueRoleAssignments.Should().BeTrue();
        parsed.Count.Should().Be(2);
        parsed.Assignments[0].LoginName.Should().Be("i:0#.f|membership|ada@contoso.com");
        parsed.Assignments[1].Roles.Should().BeEquivalentTo(["Full Control", "Design"]);
    }

    [Fact]
    public void InheritedItem_IsRepresentedAsNoUniqueAssignments()
    {
        // An inheriting item must NOT be "restored" by breaking inheritance — the
        // destination folder already yields the correct access.
        var snapshot = new ArchivedPermissions { HadUniqueRoleAssignments = false };

        var parsed = ArchivedPermissions.TryParse(snapshot.ToJson());

        parsed.Should().NotBeNull();
        parsed!.HadUniqueRoleAssignments.Should().BeFalse();
        parsed.Count.Should().Be(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData(null)]
    public void TryParse_ReturnsNull_OnBadInput(string? json)
    {
        // A null result means "no snapshot" — restore then leaves the file inheriting,
        // which is the safe default rather than throwing mid-restore.
        ArchivedPermissions.TryParse(json).Should().BeNull();
    }

    [Fact]
    public void SchemaVersion_IsStamped_ForFutureMigrations()
    {
        var parsed = ArchivedPermissions.TryParse(new ArchivedPermissions().ToJson());
        parsed!.SchemaVersion.Should().Be(ArchivedPermissions.CurrentSchemaVersion);
    }
}
