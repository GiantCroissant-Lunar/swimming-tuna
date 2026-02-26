using SwarmAssistant.Runtime.Execution;

namespace SwarmAssistant.Runtime.Tests.Execution;

public sealed class BuildVerifierTests
{
    [Theory]
    [InlineData("Passed: 42, Failed: 0", 42, 0)]
    [InlineData("Failed: 3, Passed: 10", 10, 3)]
    [InlineData("Passed:  100 , Failed:  5", 100, 5)]
    [InlineData("Test Run Successful.\nPassed: 471", 471, 0)]
    [InlineData("Failed! - Failed: 2, Passed: 100, Skipped: 0", 100, 2)]
    [InlineData("Passed: 50\nSome output\nPassed: 200, Failed: 1", 200, 1)]
    [InlineData("Failed: 2, Passed: 30\nFailed: 5, Passed: 100", 100, 5)]
    [InlineData("no test results here", 0, 0)]
    [InlineData("", 0, 0)]
    public void ParseTestResults_ExtractsCorrectCounts(string output, int expectedPassed, int expectedFailed)
    {
        var (passed, failed) = BuildVerifier.ParseTestResults(output);

        Assert.Equal(expectedPassed, passed);
        Assert.Equal(expectedFailed, failed);
    }

    [Fact]
    public void BuildVerifyResult_SuccessRecord()
    {
        var result = new BuildVerifyResult(true, 42, 0, "all good", null);

        Assert.True(result.Success);
        Assert.Equal(42, result.TestsPassed);
        Assert.Equal(0, result.TestsFailed);
        Assert.Null(result.Error);
    }

    [Fact]
    public void BuildVerifyResult_FailureRecord()
    {
        var result = new BuildVerifyResult(false, 10, 3, "output", "3 test(s) failed");

        Assert.False(result.Success);
        Assert.Equal(10, result.TestsPassed);
        Assert.Equal(3, result.TestsFailed);
        Assert.Equal("3 test(s) failed", result.Error);
    }
}
