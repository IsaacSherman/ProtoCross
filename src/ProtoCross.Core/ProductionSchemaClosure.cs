using Google.Protobuf.Reflection;
using ProtoCross.Binding;
using ProtoCross.Diagnostics;

namespace ProtoCross;

/// <summary>
/// The production schema closure (spec 25.3.1): the schemas production sources import, and every
/// schema reachable from those imports.
/// </summary>
/// <remarks>
/// <para>
/// What production behavior may name. A compilation loads one closure, of every source's imports,
/// because tests are bound against all of it; production behavior is bound against this part of it,
/// so that a schema only a test source brings -- a fixture, a mocking framework's messages -- cannot
/// make a production name valid that the production build, which never loads that schema, refuses.
/// </para>
/// <para>
/// Reachable rather than imported, and by any production source rather than by the one naming the
/// type: sources share their imports (spec 5.2), and a schema one imports brings the schemas it
/// imports with it. The question is whether the production build would have loaded the schema, and
/// that is the answer protoc gives it.
/// </para>
/// </remarks>
internal sealed class ProductionSchemaClosure
{
    private readonly DescriptorBundle _schema;

    /// <summary>Each schema in the closure, by the name protoc knows it by, and the first production import that reaches it.</summary>
    private readonly Dictionary<string, ImportResolution> _reachedBy;

    private ProductionSchemaClosure(DescriptorBundle schema, Dictionary<string, ImportResolution> reachedBy)
    {
        _schema = schema;
        _reachedBy = reachedBy;
    }

    /// <summary>The schemas in the closure, in the order the compilation loaded them.</summary>
    public IReadOnlyList<FileDescriptor> Schemas => [.. _schema.Descriptors.Where(file => _reachedBy.ContainsKey(file.Name))];

    /// <param name="schema">Everything the compilation loaded: the closure of every source's imports.</param>
    /// <param name="productionImports">The imports production sources wrote, as each resolved.</param>
    public static ProductionSchemaClosure Of(DescriptorBundle schema, IReadOnlyList<ImportResolution> productionImports)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(productionImports);

        var reachedBy = new Dictionary<string, ImportResolution>(StringComparer.Ordinal);

        foreach (var import in productionImports.Where(import => import.IsResolved))
        {
            var pending = new Stack<FileDescriptor>(schema.Descriptors.Where(file => Names(import, file, schema)));

            while (pending.TryPop(out var file))
            {
                if (!reachedBy.TryAdd(file.Name, import))
                {
                    continue;
                }

                foreach (var dependency in file.Dependencies)
                {
                    pending.Push(dependency);
                }
            }
        }

        return new ProductionSchemaClosure(schema, reachedBy);
    }

    /// <summary>
    /// Refuses every schema in the closure that the production build would not load from the file
    /// this compilation did (<c>PC0090</c>).
    /// </summary>
    /// <param name="productionRoots">
    /// The directories a production build hands protoc, in its order: the ones this compilation
    /// searched, without those only test sources bring.
    /// </param>
    /// <remarks>
    /// <para>
    /// One protoc run loads the whole compilation, over every directory, so a schema a production
    /// schema imports is found wherever the first of them holds it -- a test source's directory
    /// included. The production build searches without those directories, and there that schema is
    /// either missing, and the build fails, or found somewhere else, and production behavior is bound
    /// against another file. Either way adding the test sources changed what production behavior
    /// was bound against.
    /// </para>
    /// <para>
    /// Asked with the rule the compilation's own record of each file was made by
    /// (<see cref="SchemaLookup"/>), so the two answers differ only by the directories between them.
    /// A schema protoc supplied from its own descriptors has no file on either side, and is the same
    /// in both builds.
    /// </para>
    /// </remarks>
    public void ReportSchemasTheProductionBuildLoadsDifferently(IReadOnlyList<string> productionRoots, DiagnosticBag diagnostics)
    {
        ArgumentNullException.ThrowIfNull(productionRoots);
        ArgumentNullException.ThrowIfNull(diagnostics);

        foreach (var file in Schemas)
        {
            var loaded = _schema.PathFor(file.Name);
            var production = SchemaLookup.Find(file.Name, productionRoots);

            if (loaded is null
                || (production is not null && PathIdentity.AreSame(production, loaded)))
            {
                continue;
            }

            var import = _reachedBy[file.Name];
            diagnostics.Report(
                DiagnosticCodes.ProductionSchemaNeedsATestDirectory,
                production is null
                    ? $"'{file.Name}', which '{import.Path}' brings into the production compilation, was found "
                        + $"only at '{loaded}', through a directory a test source brings, so the production "
                        + "build cannot load it."
                    : $"'{file.Name}', which '{import.Path}' brings into the production compilation, was loaded "
                        + $"from '{loaded}', through a directory a test source brings, where the production "
                        + $"build loads '{production}'.",
                import.Span,
                "Make the schema reachable without the test sources: pass its directory as an include "
                    + "path, or keep it beside a production source.");
        }
    }

    /// <summary>Whether <paramref name="import"/> is what brought <paramref name="file"/> into the compilation.</summary>
    /// <remarks>
    /// Asked two ways, because neither answers every case. protoc does not always know a schema by
    /// the path its import wrote -- one written as a full path is known by its path below the root
    /// that holds it -- so the file the import resolved to is compared with the file the schema was
    /// loaded from. And the two files can differ while the schema is the one imported: a schema protoc
    /// supplied from its own descriptors has no file, and one a test source's directory shadows was
    /// loaded from that copy, which is exactly what has to be found here to be reported. So the name
    /// answers too, and it is never wrong when it matches: one compilation cannot load two schemas
    /// under one name.
    /// </remarks>
    private static bool Names(ImportResolution import, FileDescriptor file, DescriptorBundle schema)
        => string.Equals(import.Path, file.Name, StringComparison.Ordinal)
            || (schema.PathFor(file.Name) is { } path && PathIdentity.AreSame(path, import.ResolvedPath!));
}
