using System.Xml.Linq;
using AwesomeAssertions;
using Migration.Engine.Migration;
using Xunit;

namespace Migration.Engine.Tests.Lifecycle;

/// <summary>
/// Guards the wiring behind the library status badge (issue #32). The badge only
/// renders when the <b>list</b> column carries the SPFx field customizer's
/// component id — a site column on its own is never added to a library or a view,
/// which is exactly why the original implementation showed nothing.
/// </summary>
public class ColdStorageStatusFieldTests
{
    /// <summary>
    /// Must match the id in
    /// src/SPFx/spfx-cold-storage/src/extensions/coldStorageStatusField/ColdStorageStatusFieldCustomizer.manifest.json
    /// and the ClientSideComponentId in src/SPFx/spfx-cold-storage/sharepoint/assets/elements.xml.template.
    /// </summary>
    private const string ExpectedCustomizerId = "bcc81765-0e17-4bd7-a1a5-68a72cb5a016";

    [Fact]
    public void CustomizerId_MatchesTheSpfxManifest()
    {
        SharePointPlaceholderWriter.StatusFieldCustomizerId.Should().Be(ExpectedCustomizerId);
        Guid.TryParse(SharePointPlaceholderWriter.StatusFieldCustomizerId, out _).Should().BeTrue();
    }

    [Fact]
    public void StatusFieldXml_IsWellFormedAndBindsTheFieldCustomizer()
    {
        var field = XElement.Parse(SharePointPlaceholderWriter.BuildStatusFieldXml());

        field.Name.LocalName.Should().Be("Field");
        field.Attribute("Type")!.Value.Should().Be("Text");
        field.Attribute("Name")!.Value.Should().Be(SharePointPlaceholderWriter.FieldColdStorageStatus);
        field.Attribute("StaticName")!.Value.Should().Be(SharePointPlaceholderWriter.FieldColdStorageStatus);

        // Without this attribute SharePoint renders raw text and the badge never appears.
        field.Attribute("ClientSideComponentId")!.Value.Should().Be(ExpectedCustomizerId);
    }

    [Fact]
    public void StatusFieldName_MatchesTheSiteColumnProvisionedBySpfx()
    {
        SharePointPlaceholderWriter.FieldColdStorageStatus.Should().Be("ColdStorageStatus");
    }
}
