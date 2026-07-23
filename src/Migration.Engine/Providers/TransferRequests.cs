namespace Migration.Engine.Providers;

/// <summary>
/// Provider-neutral input to <see cref="MigratePipeline"/>: everything needed to archive one item
/// from a source store to a cold store, independent of who those providers are. The message
/// processor builds this from the bus envelope; tests build it directly.
/// </summary>
public sealed record MigrateRequest
{
    public required Guid JobId { get; init; }
    public required Guid ItemId { get; init; }

    /// <summary>Where the live item lives in the source store.</summary>
    public required SourceItemRef Source { get; init; }

    /// <summary>Where its archive lives (or will live) in the cold store.</summary>
    public required ColdStorageKey Cold { get; init; }

    /// <summary>The source's last-modified time, used for conflict-by-date and placeholder metadata.</summary>
    public required DateTime SourceLastModifiedUtc { get; init; }

    /// <summary>The source's created time, for placeholder metadata (optional).</summary>
    public DateTime? SourceCreatedUtc { get; init; }

    /// <summary>Size hint from enumeration, used only by the eligibility gate (authoritative size comes from the read).</summary>
    public long SourceSizeHint { get; init; }

    public string RequestedByUpn { get; init; } = string.Empty;

    /// <summary>
    /// When true, copy the captured original authorship onto visible "Original *" columns on
    /// the placeholder's library. Default false: leave the placeholder as just the pointer
    /// file. The metadata is preserved on the cold object either way.
    /// </summary>
    public bool CopyMetadataColumns { get; init; }

    /// <summary>Optional provider hints passed through to the eligibility evaluator (e.g. Graph drive/item id).</summary>
    public string? DriveId { get; init; }
    public string? GraphItemId { get; init; }
}

/// <summary>
/// Provider-neutral input to <see cref="RestorePipeline"/>: restore one archived item back to its
/// source, driven by the placeholder pointer left behind at archival time.
/// </summary>
public sealed record RestoreRequest
{
    public required Guid JobId { get; init; }
    public required Guid ItemId { get; init; }

    /// <summary>The placeholder pointer to read (which names the cold object + original location).</summary>
    public required SourceItemRef Pointer { get; init; }

    /// <summary>Optional explicit destination; when null, the pointer's recorded original location is used.</summary>
    public SourceItemRef? Destination { get; init; }

    public Models.ColdStorage.ConflictBehavior ConflictBehavior { get; init; } = Models.ColdStorage.ConflictBehavior.Fail;

    /// <summary>When true, delete the cold object after a verified restore (mirrors the archive's delete-safety).</summary>
    public bool DeleteColdAfterRestore { get; init; }
}
