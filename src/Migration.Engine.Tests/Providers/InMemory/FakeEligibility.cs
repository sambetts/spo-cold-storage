using Migration.Engine.Migration;

namespace Migration.Engine.Tests.Providers.InMemory;

/// <summary>Configurable <see cref="IArchiveEligibilityEvaluator"/> for tests. Eligible by default.</summary>
public sealed class FakeEligibility : IArchiveEligibilityEvaluator
{
    private readonly ArchiveEligibilityResult _result;

    public FakeEligibility(bool eligible = true, string? skipReason = null)
        => _result = new ArchiveEligibilityResult(eligible, skipReason);

    public static FakeEligibility Ineligible(string reason) => new(false, reason);

    public Task<ArchiveEligibilityResult> EvaluateAsync(ArchiveCandidate candidate, CancellationToken cancellationToken = default)
        => Task.FromResult(_result);
}
