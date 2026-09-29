using ProtoCross.Backend;
using Xunit;

namespace ProtoCross.Tests;

/// <summary>
/// Which names can name the namespace a project's behavior is declared in (spec 24): a protobuf
/// package whose every component begins with a letter.
/// </summary>
public class ProjectNamespaceTests
{
    /// <summary>Names every target spells: one component or several, any case, digits and underscores after the first letter.</summary>
    internal static IReadOnlyList<string> NamespaceNames { get; } =
    [
        "billing",
        "acme.billing",
        "Acme.Billing",
        "acme.v1beta1",
        "snake_case.pkg",
        "x",
        "acme.new",
    ];

    /// <summary>
    /// Names at least one target cannot spell, or that are no package at all: an empty component, a
    /// leading digit or underscore at the start or further in, punctuation, a space, and a letter
    /// outside ASCII.
    /// </summary>
    private static IReadOnlyList<string> NotNamespaceNames { get; } =
    [
        "",
        ".",
        "acme.",
        ".acme",
        "acme..billing",
        "1acme",
        "acme.1x",
        "_acme",
        "acme._x",
        "billing-service",
        "acme billing",
        "acme/billing",
        "caf\u00e9",
    ];

    public static TheoryData<string> Namespaces => [.. NamespaceNames];

    public static TheoryData<string> NotNamespaces => [.. NotNamespaceNames];

    [Theory]
    [MemberData(nameof(Namespaces))]
    public void IdentifiersSeparatedByPeriodsNameANamespace(string name)
    {
        Assert.True(ProjectNamespace.TryParse(name, out var parsed), $"'{name}' should name a namespace");
        Assert.Equal(name, parsed.Package);
    }

    [Theory]
    [MemberData(nameof(NotNamespaces))]
    public void ANameATargetCannotSpellNamesNoNamespace(string name)
    {
        Assert.False(ProjectNamespace.TryParse(name, out var parsed), $"'{name}' should name no namespace");
        Assert.Null(parsed);
    }

    /// <summary>
    /// Constructing one from a name <see cref="ProjectNamespace.TryParse"/> refuses is a mistake in
    /// the caller, so there is no namespace that could not have been parsed.
    /// </summary>
    [Theory]
    [MemberData(nameof(NotNamespaces))]
    public void ANamespaceCannotBeBuiltFromANameThatIsNotOne(string name)
        => Assert.Throws<ArgumentException>(() => new ProjectNamespace(name));
}
