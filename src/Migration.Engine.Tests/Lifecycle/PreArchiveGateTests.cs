using Models.ColdStorage;
using Xunit;

namespace Migration.Engine.Tests.Lifecycle;

/// <summary>
/// Pure grace-period decision for pre-archive notices (issue #17).
/// </summary>
public class PreArchiveGateTests
{
    private static readonly DateTime Now = new(2026, 6, 30, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Disabled_AlwaysProceeds()
    {
        Assert.Equal(PreArchiveDecision.Proceed, PreArchiveGate.Decide(null, Now, 0));
        Assert.Equal(PreArchiveDecision.Proceed, PreArchiveGate.Decide(Now.AddHours(5), Now, 0));
        Assert.Equal(PreArchiveDecision.Proceed, PreArchiveGate.Decide(null, Now, -1));
    }

    [Fact]
    public void NoNoticeYet_SendsNotice()
        => Assert.Equal(PreArchiveDecision.SendNotice, PreArchiveGate.Decide(null, Now, 24));

    [Fact]
    public void WithinGraceWindow_Waits()
        => Assert.Equal(PreArchiveDecision.Waiting, PreArchiveGate.Decide(Now.AddHours(1), Now, 24));

    [Fact]
    public void GraceElapsed_Proceeds()
    {
        Assert.Equal(PreArchiveDecision.Proceed, PreArchiveGate.Decide(Now, Now, 24));
        Assert.Equal(PreArchiveDecision.Proceed, PreArchiveGate.Decide(Now.AddHours(-1), Now, 24));
    }
}
