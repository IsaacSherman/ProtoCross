using System.Text.RegularExpressions;
using Google.Protobuf.Reflection;
using ProtoCross.Backend;
using ProtoCross.Backend.Cpp;
using ProtoCross.Backend.CSharp;
using ProtoCross.Binding;
using ProtoCross.Diagnostics;
using ProtoCross.Tests.Harness;
using Xunit;

namespace ProtoCross.Tests;

public partial class NameMappingTests
{
    // --- C# namespaces ---
    //
    // protoc's C# generator names a file's namespace with the case rule it names a property with, run
    // once over the whole package with its periods kept, unless the file sets csharp_namespace. A
    // package belongs to a file and the conformance corpus has one schema, so the rule is checked here
    // against the C# protoc writes, with one schema for each case it treats differently.

    /// <summary>
    /// What each schema declares ahead of its body: a package, and the option that overrides it.
    /// </summary>
    private static readonly string[] AwkwardPackageHeads =
    [
        "package acme.v1beta1;",
        "package acme.v1;",
        "package x9y.z;",
        "package snake_case.pkg_name;",
        "package a__b.c_;",
        "package mixedCase.ALLCAPS.lower;",
        "package _1x.acme;",
        "package __1x;",
        "package acme._1x;",
        "package acme.v1beta1;\noption csharp_namespace = \"Explicit.name_space\";",
        "package acme.v1beta1;\noption csharp_namespace = \"\";",
        "",
    ];

    /// <summary>
    /// Every namespace <see cref="NameConventions.GetCSharpNamespace"/> names is the one protoc
    /// declared for that file, for a schema of each case its rule treats differently: a letter after a
    /// digit, capitals, underscores doubled and trailing, an underscore before a digit at the start of
    /// the package and at the start of a later component, an explicit <c>csharp_namespace</c> and an
    /// empty one, and no package at all.
    /// </summary>
    /// <remarks>
    /// <c>acme._1x</c> is the case that tells protoc's rule from one applied a component at a time.
    /// protoc keeps the underscore in front of a digit only at the start of the whole package, so the
    /// namespace it declares is <c>Acme.1X</c>, which is not a C# identifier. The generated source
    /// does not compile, and the name ProtoCross gives it still has to be that one.
    /// </remarks>
    [Fact]
    public void EveryCSharpNamespaceIsTheOneProtocDeclaresForThatFile()
    {
        // protoc names a file's C# after the schema PascalCased, which Ns{index} already is.
        var schemas = AwkwardPackageHeads
            .Select((head, index) => ($"Ns{index}.proto", $"syntax = \"proto3\";\n{head}\n"))
            .ToList();
        var (files, directory) = GenerateWithPinnedProtoc(schemas, "csharp_out");

        foreach (var (file, head) in files.Zip(AwkwardPackageHeads))
        {
            var generated = File.ReadAllText(Path.Combine(directory, Path.ChangeExtension(file.Name, ".cs")));
            var declared = DeclaredNamespace(generated);
            var named = NameConventions.GetCSharpNamespace(file);
            Assert.True(
                named == declared,
                $"protoc declares the namespace '{declared}' for '{head}', and ProtoCross names it '{named}'");
        }
    }

    /// <summary>The namespace generated C# declares its classes in, or empty when it declares none.</summary>
    private static string DeclaredNamespace(string source)
    {
        var declaration = Regex.Match(source, @"^namespace (\S+) \{\r?$", RegexOptions.Multiline);
        return declaration.Success ? declaration.Groups[1].Value : string.Empty;
    }
}
