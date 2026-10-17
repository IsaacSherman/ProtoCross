using System.Diagnostics.CodeAnalysis;

namespace ProtoCross.Toolchain;

/// <summary>What looking for a tool found: the tool, or why there is none and what would provide one.</summary>
/// <remarks>
/// A reason rather than a null, because a missing tool is the one failure its user fixes by reading what
/// they were told. A null left every caller to say that for itself, and each said it a little differently.
/// </remarks>
public sealed record ToolLookup<T>
    where T : class
{
    private ToolLookup(T? tool, string? missing)
    {
        Tool = tool;
        Missing = missing;
    }

    /// <summary>The tool, or null when none was found.</summary>
    public T? Tool { get; }

    /// <summary>Why no tool was found, and what would provide one; null when one was.</summary>
    public string? Missing { get; }

    /// <summary>Whether a tool was found.</summary>
    [MemberNotNullWhen(true, nameof(Tool))]
    [MemberNotNullWhen(false, nameof(Missing))]
    public bool Found => Tool is not null;

    /// <summary>A lookup that found <paramref name="tool"/>.</summary>
    public static ToolLookup<T> Of(T tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return new(tool, null);
    }

    /// <summary>A lookup that found nothing, for the reason <paramref name="missing"/> gives.</summary>
    public static ToolLookup<T> None(string missing)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(missing);
        return new(null, missing);
    }
}
