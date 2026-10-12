using ProtoCross.Types;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// Which enums are closed (spec 12.1): every proto2 enum, no proto3 enum, and an editions enum whose
/// <c>enum_type</c> feature says so, on the enum or else on its file.
/// </summary>
/// <remarks>
/// The runtime this compiler links strips <c>features</c> from the options it hands back, so an
/// answer read from there calls every editions enum open. That is the failure these exist to catch,
/// and why each place a feature can be stated has a row of its own.
/// </remarks>
public class EnumOpennessTests
{
    private static readonly Lazy<CompilationResult> Schemas = new(() => Compilation.Compile(
        TestPaths.WriteTempScript(
            """
            import proto "fixtures.proto";
            import proto "enum_openness_proto2.proto";
            import proto "enum_openness_editions.proto";
            import proto "enum_openness_closed_file.proto";
            """),
        [TestPaths.FixtureProtoDirectory]));

    [Theory]
    [InlineData("protocross.tests.TopLevelStatus", false)]
    [InlineData("protocross.tests.Outer.Nested", false)]
    [InlineData("protocross.tests.openness.Proto2Status", true)]
    [InlineData("protocross.tests.openness.editions.EditionsDefault", false)]
    [InlineData("protocross.tests.openness.editions.EditionsClosed", true)]
    [InlineData("protocross.tests.openness.editions.EditionsScope.Inner", true)]
    [InlineData("protocross.tests.openness.editions.EditionsScope.Deeper.Deepest", true)]
    [InlineData("protocross.tests.openness.editions.EditionsScope.Deeper.Unstated", false)]
    [InlineData("protocross.tests.openness.closedfile.FileClosed", true)]
    [InlineData("protocross.tests.openness.closedfile.FileClosedReopened", false)]
    [InlineData("protocross.tests.openness.closedfile.FileClosedScope.Nested", true)]
    public void AnEnumIsClosedWhereItsSchemaSaysSo(string fullName, bool closed)
    {
        var descriptor = Schemas.Value.Types.FindEnum(fullName);

        Assert.True(descriptor is not null, $"the fixture schemas should declare {fullName}");
        Assert.Equal(closed, EnumOpenness.IsClosed(descriptor!));
    }
}
