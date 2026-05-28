namespace Models.ColdStorage;

/// <summary>
/// Distinguishes a migrate request (SharePoint -> cold storage) from a restore
/// request (cold storage -> SharePoint) on the wire.
/// </summary>
public enum MigrationOperationKind
{
    Migrate = 0,
    Restore = 1,
}

/// <summary>
/// What the restore worker should do if the destination SharePoint location
/// already contains a non-placeholder file with the same name.
/// </summary>
public enum ConflictBehavior
{
    Fail = 0,
    Overwrite = 1,
    Rename = 2,
}

/// <summary>
/// Item granularity selected by the SPFx component.
/// </summary>
public enum ColdStorageItemKind
{
    File = 0,
    Folder = 1,
}
