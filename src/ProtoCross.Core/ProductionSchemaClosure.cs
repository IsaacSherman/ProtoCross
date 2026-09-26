using Google.Protobuf.Reflection;
using ProtoCross.Binding;

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
internal static class ProductionSchemaClosure
{
    /// <param name="schema">Everything the compilation loaded: the closure of every source's imports.</param>
    /// <param name="productionImports">The imports production sources wrote, as each resolved.</param>
    /// <returns>The part of <paramref name="schema"/> in the closure, in the order it was loaded.</returns>
    /// <remarks>
    /// A schema is found by the name protoc knows it by, which is the path the import that handed it
    /// to protoc wrote. protoc is handed each file once, under the first import of it, and production
    /// sources come before test sources, so a file any production import resolved to was handed over
    /// under a production import's path. protoc refuses a path it cannot use as written -- one
    /// spelled with a backslash, say -- so a loaded schema is never known by another spelling.
    /// </remarks>
    public static IReadOnlyList<FileDescriptor> Of(DescriptorBundle schema, IReadOnlyList<ImportResolution> productionImports)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(productionImports);

        var imported = productionImports
            .Where(import => import.IsResolved)
            .Select(import => import.Path)
            .ToHashSet(StringComparer.Ordinal);

        var reached = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<FileDescriptor>(schema.Descriptors.Where(file => imported.Contains(file.Name)));

        while (pending.TryPop(out var file))
        {
            if (!reached.Add(file.Name))
            {
                continue;
            }

            foreach (var dependency in file.Dependencies)
            {
                pending.Push(dependency);
            }
        }

        return [.. schema.Descriptors.Where(file => reached.Contains(file.Name))];
    }
}
