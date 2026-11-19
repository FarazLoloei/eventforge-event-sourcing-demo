using Xunit;

namespace EventForge.UnitTests;

public sealed class CiFailureProbeTests
{
    [Fact]
    public void Pipeline_rejects_failing_tests()
    {
        Assert.Fail("EVF-5 controlled CI failure probe");
    }
}
