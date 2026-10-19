using System.Reflection;
using ProtoCross.Ir;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>Every IR type is named in spec 22.2, beside what it keeps.</summary>
/// <remarks>
/// 22.2 is the IR's contract, and it spent four issues behind the code before #107 caught it up, because nothing
/// said a node had been added. A type the table in 22.2 does not name fails here, which is the step that was
/// missing: whoever adds a node is told to say what it keeps. <see cref="IrContractTests"/> asserts what 22.2
/// promises of every node; this asserts that it speaks of every one.
/// </remarks>
public class IrContractCoverageTests
{
    /// <summary>Every IR type a module can hold: each concrete record of the IR's namespace.</summary>
    public static TheoryData<string> IrTypes()
    {
        var types = typeof(IrNode).Assembly.GetTypes()
            .Where(type => type.Namespace == typeof(IrNode).Namespace
                && type is { IsClass: true, IsAbstract: false, IsPublic: true }
                && type.Name.StartsWith("Ir", StringComparison.Ordinal)
                && type.GetMethod("<Clone>$", BindingFlags.Public | BindingFlags.Instance) is not null)
            .Select(type => type.Name)
            .Order(StringComparer.Ordinal);

        var data = new TheoryData<string>();
        foreach (var name in types)
        {
            data.Add(name);
        }

        return data;
    }

    private static readonly Lazy<string> Contract = new(() =>
    {
        var section = File.ReadAllText(Path.Combine(TestPaths.RepositoryRoot, "ProtoCross_Spec", "§22-IR and Compiler Architecture.md"));
        var start = section.IndexOf("### 22.2", StringComparison.Ordinal);
        var end = section.IndexOf("### 22.3", start, StringComparison.Ordinal);
        return section[start..end];
    });

    [Fact]
    public void TheIrHasTypesToName() => Assert.True(IrTypes().Count > 50, "no IR records were found to check");

    [Theory]
    [MemberData(nameof(IrTypes))]
    public void Spec22_2NamesTheType(string type)
        => Assert.True(
            Contract.Value.Contains($"`{type}`", StringComparison.Ordinal),
            $"{type} is not named in spec 22.2. Add it to the table there, beside what it keeps.");
}
