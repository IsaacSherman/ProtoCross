using System.Reflection;

namespace ProtoCross;

/// <summary>The version a ProtoCross assembly was built as, in the form every component reports it.</summary>
/// <remarks>
/// <para>
/// One home for the attribute read, because the command line's <c>--version</c>, the server's status
/// report and its handshake all state versions. Two spellings of the same lookup are a chance for one of
/// them to fall back differently, and for a reader to conclude two components are out of step when they
/// are not.
/// </para>
/// <para>
/// It is the informational version, so a build names the commit it came from as well as its number,
/// <c>0.8.0+&lt;commit&gt;</c>: the number says which release, and the commit says which build of it.
/// Every assembly takes the number from the extension's <c>package.json</c> (Directory.Build.props).
/// This lives in Core because the command line and the server share nothing else that could hold it.
/// </para>
/// </remarks>
public static class ProductVersion
{
    /// <summary>The version of the assembly <paramref name="type"/> is in.</summary>
    public static string Of(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        return type.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "0.0.0";
    }
}
