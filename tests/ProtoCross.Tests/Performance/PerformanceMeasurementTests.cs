using Xunit;

namespace ProtoCross.Tests.Performance;

/// <summary>Properties of the measuring apparatus itself, rather than of what it measures.</summary>
/// <remarks>
/// These run unconditionally. A benchmark's guards are worth less than the benchmark if they only
/// hold on the runs somebody remembered to ask for.
/// </remarks>
public class PerformanceMeasurementTests
{
    /// <summary>
    /// The optimizer check reports the configuration this assembly was actually built in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>#if DEBUG</c> is decided by the compiler that produced this test, and
    /// <see cref="Sampler.Optimized"/> is decided at run time by reading an attribute off the
    /// assembly being measured. They are two independent answers to one question, which is what makes
    /// asserting they agree worth anything: a detection that silently returned <c>true</c> always --
    /// the failure that matters, because it lets Debug numbers through the guard wearing a Release
    /// label -- fails here on every ordinary `dotnet test` run.
    /// </para>
    /// <para>
    /// It is also the reason the check reads the attribute rather than using <c>#if DEBUG</c>
    /// directly: the question is about the assembly under measurement, and a conditional in the test
    /// project can only answer about the test project.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheOptimizerCheckAgreesWithTheBuildConfiguration()
    {
#if DEBUG
        Assert.False(
            Sampler.Optimized,
            "this is a Debug build, so the guard that keeps Debug numbers out of the report must say so");
        Assert.Contains("Debug", Sampler.Configuration, StringComparison.Ordinal);
#else
        Assert.True(
            Sampler.Optimized,
            "this is a Release build, so the benchmark must be willing to run");
        Assert.Equal("Release", Sampler.Configuration);
#endif
    }

    /// <summary>The report says which configuration produced it, whichever one that was.</summary>
    /// <remarks>
    /// A table of milliseconds with no build configuration on it is a table somebody will paste into
    /// a document, and the numbers in this repository's own documentation were pasted from exactly
    /// such a table.
    /// </remarks>
    [Fact]
    public void EveryReportStatesTheConfigurationItWasTakenOn()
    {
        Assert.Contains(Sampler.Configuration, PerformanceReport.Heading(), StringComparison.Ordinal);
    }
}
