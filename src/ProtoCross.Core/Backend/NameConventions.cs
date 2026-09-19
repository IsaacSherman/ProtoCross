using System.Text;
using Google.Protobuf.Reflection;

namespace ProtoCross.Backend;

/// <summary>
/// Shared name mapping. Spec 21.2 requires each backend to document how ProtoCross names map to
/// target-language conventions; these helpers are that mapping, and they deliberately match what
/// protoc's own generators do so hand-written and generated code agree.
/// </summary>
public static class NameConventions
{
    /// <summary>
    /// Every name protoc's C++ generator will not use as it stands: the C++ keywords and alternative
    /// tokens, plus <c>NULL</c> and <c>assert</c>, which are macros. <c>kKeywordList</c> in the
    /// generator's <c>helpers.cc</c>, copied rather than rewritten from the standard.
    /// </summary>
    /// <remarks>
    /// The list exists to agree with a header protoc generated: a field whose name is on it has
    /// accessors with an underscore, and one whose name is not has none, whatever the standard says.
    /// The C++ backend escapes its own identifiers against the same list, because a name protoc will
    /// not use is no more usable as a method or a local. It used to keep a list of its own, and the
    /// two had drifted -- that one was missing <c>const_cast</c>, <c>char8_t</c> and the alternative
    /// tokens such as <c>not_eq</c>.
    /// </remarks>
    private static readonly HashSet<string> CppKeywords = new(StringComparer.Ordinal)
    {
        "NULL", "alignas", "alignof", "and", "and_eq", "asm", "assert", "auto", "bitand", "bitor",
        "bool", "break", "case", "catch", "char", "class", "compl", "const", "constexpr",
        "const_cast", "continue", "decltype", "default", "delete", "do", "double", "dynamic_cast",
        "else", "enum", "explicit", "export", "extern", "false", "float", "for", "friend", "goto",
        "if", "inline", "int", "long", "mutable", "namespace", "new", "noexcept", "not", "not_eq",
        "nullptr", "operator", "or", "or_eq", "private", "protected", "public", "register",
        "reinterpret_cast", "return", "short", "signed", "sizeof", "static", "static_assert",
        "static_cast", "struct", "switch", "template", "this", "thread_local", "throw", "true",
        "try", "typedef", "typeid", "typename", "union", "unsigned", "using", "virtual", "void",
        "volatile", "wchar_t", "while", "xor", "xor_eq", "char8_t", "char16_t", "char32_t",
        "concept", "consteval", "constinit", "co_await", "co_return", "co_yield", "requires",
    };

    /// <summary>
    /// The nullary members every generated C++ message declares, which an accessor of the same name
    /// could not overload. <c>MessageKnownNullaryMethodsSnakeCase</c> in protoc's <c>helpers.cc</c>.
    /// </summary>
    private static readonly HashSet<string> CppMessageNullaryMembers = new(StringComparer.Ordinal)
    {
        "unknown_fields", "mutable_unknown_fields", "descriptor", "default_instance",
    };

    /// <summary>
    /// Converts a protobuf <c>snake_case</c> name to <c>PascalCase</c>, matching the C# protobuf
    /// generator so <c>unit_price_cents</c> lines up with the generated <c>UnitPriceCents</c>.
    /// </summary>
    public static string ToPascalCase(string name)
    {
        var builder = new StringBuilder(name.Length);
        var capitalizeNext = true;

        foreach (var c in name)
        {
            if (c == '_')
            {
                capitalizeNext = true;
                continue;
            }

            builder.Append(capitalizeNext ? char.ToUpperInvariant(c) : c);
            capitalizeNext = false;
        }

        return builder.ToString();
    }

    /// <summary>
    /// The C# namespace protoc would use for a file: the explicit <c>csharp_namespace</c> option
    /// when present, otherwise the PascalCased protobuf package.
    /// </summary>
    public static string GetCSharpNamespace(FileDescriptor file)
    {
        var explicitNamespace = file.GetOptions()?.CsharpNamespace;
        if (!string.IsNullOrEmpty(explicitNamespace))
        {
            return explicitNamespace;
        }

        if (string.IsNullOrEmpty(file.Package))
        {
            return string.Empty;
        }

        return string.Join('.', file.Package.Split('.').Select(ToPascalCase));
    }

    /// <summary>
    /// The C++ namespace protoc would use: the protobuf package with dots replaced by <c>::</c>.
    /// </summary>
    public static string GetCppNamespace(FileDescriptor file)
        => string.IsNullOrEmpty(file.Package) ? string.Empty : file.Package.Replace(".", "::", StringComparison.Ordinal);

    /// <summary>The generated protobuf C++ header for a .proto file: <c>foo.proto</c> to <c>foo.pb.h</c>.</summary>
    public static string GetCppProtoHeader(FileDescriptor file)
    {
        var name = file.Name;
        return name.EndsWith(".proto", StringComparison.Ordinal)
            ? string.Concat(name.AsSpan(0, name.Length - ".proto".Length), ".pb.h")
            : name + ".pb.h";
    }

    /// <summary>
    /// The C++ nested-type qualifier for a message, for example <c>Outer_Inner</c> for a message
    /// nested inside <c>Outer</c>.
    /// </summary>
    public static string GetCppTypeName(MessageDescriptor message)
    {
        var parts = new List<string>();
        for (var current = message; current is not null; current = current.ContainingType)
        {
            parts.Insert(0, current.Name);
        }

        return string.Join('_', parts);
    }

    /// <summary>The C# type name for a message, including the outer-class nesting protoc applies.</summary>
    public static string GetCSharpTypeName(MessageDescriptor message)
        => QualifyCSharp(message.File, string.Join(".Types.", EnclosingNames(message.ContainingType, message.Name)));

    /// <summary>
    /// The C# type name for an enum. protoc puts nested enums inside the containing message's
    /// <c>Types</c> class, so <c>Outer.E</c> becomes <c>Outer.Types.E</c>.
    /// </summary>
    public static string GetCSharpTypeName(EnumDescriptor enumType)
        => QualifyCSharp(enumType.File, string.Join(".Types.", EnclosingNames(enumType.ContainingType, enumType.Name)));

    /// <summary>
    /// The C++ type name for an enum. protoc flattens nested types into the namespace with
    /// underscores, so <c>Outer.E</c> becomes <c>Outer_E</c>.
    /// </summary>
    public static string GetCppTypeName(EnumDescriptor enumType)
        => string.Join('_', EnclosingNames(enumType.ContainingType, enumType.Name));

    /// <summary>
    /// The C# name of an enum value. protoc strips the enum's own name from the front of the value
    /// and PascalCases what is left, so <c>TOP_LEVEL_STATUS_OK</c> under <c>TopLevelStatus</c>
    /// becomes <c>Ok</c>.
    /// </summary>
    /// <remarks>
    /// Reproduced rather than approximated. The rule has edge cases -- a value that does not carry
    /// the prefix keeps its whole name, and stripping that would leave a leading digit gets an
    /// underscore -- and getting one wrong emits a name that does not exist, which is a compile
    /// error in the consumer's build rather than anything this compiler would notice.
    /// </remarks>
    public static string GetCSharpValueName(EnumValueDescriptor value)
    {
        var name = ShoutyToPascalCase(TryRemovePrefix(value.EnumDescriptor.Name, value.Name));

        // 'LEVEL_2' under 'Level' strips to '2', which is not an identifier.
        return name.Length > 0 && char.IsAsciiDigit(name[0]) ? "_" + name : name;
    }

    /// <summary>
    /// The C++ name of an enum value, unqualified. protoc emits a nested enum's values at namespace
    /// scope prefixed with the flattened enum type name, and a top-level enum's values bare, so
    /// <c>Outer.Nested.NESTED_SOME</c> becomes <c>Outer_Nested_NESTED_SOME</c> while
    /// <c>TopLevelStatus.TOP_LEVEL_STATUS_OK</c> stays <c>TOP_LEVEL_STATUS_OK</c>.
    /// </summary>
    public static string GetCppValueName(EnumValueDescriptor value)
        => value.EnumDescriptor.ContainingType is null
            ? value.Name
            : GetCppTypeName(value.EnumDescriptor) + "_" + value.Name;

    /// <summary>
    /// The name protoc's C++ generator spells every accessor of a field from: the getter is
    /// <c>name()</c>, and the rest are prefixes on the same name -- <c>has_name()</c>,
    /// <c>set_name()</c>, <c>mutable_name()</c>, <c>add_name()</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A port of <c>FieldName</c> in protoc's <c>compiler/cpp/helpers.cc</c>. The name is lowercased,
    /// then given a trailing underscore if the result is on <see cref="CppKeywords"/> or is one of
    /// <see cref="CppMessageNullaryMembers"/>. A field called <c>class</c> is therefore read with
    /// <c>class_()</c> and written with <c>set_class_()</c>, not <c>set_class()</c>: protoc prefixes the
    /// escaped name, so a prefix that would have made the bare one legal does not bring it back.
    /// </para>
    /// <para>
    /// The order is protoc's, and it shows. <c>Friend</c> lowercases onto a keyword and becomes
    /// <c>friend_</c>; <c>NULL</c>, on the list only in capitals, lowercases off it and stays
    /// <c>null</c>. The one exception is a message with <c>no_standard_descriptor_accessor</c> set,
    /// which gives up its generated <c>descriptor()</c> and so leaves a field of that name alone.
    /// </para>
    /// <para>
    /// Reproduced rather than approximated, as <see cref="GetCSharpValueName"/> is and for the same
    /// reason: a name that is nearly right is a compile error in the consumer's build, where nothing
    /// connects it back to this compiler. The rule and both lists are the same in the sources of
    /// protoc 31.1 and 33.4, and were checked against the header 33.4 generates.
    /// </para>
    /// </remarks>
    public static string GetCppFieldName(FieldDescriptor field)
    {
        if (field.Name == "descriptor" && field.ContainingType.GetOptions()?.NoStandardDescriptorAccessor == true)
        {
            return field.Name;
        }

        var name = field.Name.ToLowerInvariant();
        return CppKeywords.Contains(name) || CppMessageNullaryMembers.Contains(name) ? name + "_" : name;
    }

    /// <summary>
    /// <paramref name="name"/> as a C++ identifier: itself, or with an underscore appended when it is
    /// a name protoc's C++ generator would escape. protoc's own <c>ResolveKeyword</c>.
    /// </summary>
    public static string EscapeCppKeyword(string name) => CppKeywords.Contains(name) ? name + "_" : name;

    /// <summary>
    /// Removes <paramref name="prefix"/> from the front of <paramref name="value"/>, ignoring case
    /// and underscores in both, and returns <paramref name="value"/> unchanged when it does not
    /// match. A port of the same helper in protoc's C# generator.
    /// </summary>
    private static string TryRemovePrefix(string prefix, string value)
    {
        var target = new StringBuilder(prefix.Length);
        foreach (var c in prefix)
        {
            if (c != '_')
            {
                target.Append(char.ToLowerInvariant(c));
            }
        }

        var valueIndex = 0;
        for (var prefixIndex = 0; prefixIndex < target.Length && valueIndex < value.Length; prefixIndex++, valueIndex++)
        {
            while (value[valueIndex] == '_')
            {
                valueIndex++;
                if (valueIndex == value.Length)
                {
                    // The value is nothing but underscores past this point.
                    return value;
                }
            }

            if (char.ToLowerInvariant(value[valueIndex]) != target[prefixIndex])
            {
                return value;
            }
        }

        while (valueIndex < value.Length && value[valueIndex] == '_')
        {
            valueIndex++;
        }

        // Consuming the whole value would leave nothing to name it with.
        return valueIndex < value.Length ? value[valueIndex..] : value;
    }

    /// <summary>
    /// Converts a <c>SHOUTY_CASE</c> name to <c>PascalCase</c>: the first alphanumeric of each run
    /// is upper-cased, the rest lower-cased, and the separators dropped. A port of the same helper
    /// in protoc's C# generator, and distinct from <see cref="ToPascalCase"/>, which preserves the
    /// case of characters it does not capitalize and so would turn <c>LEVEL_HIGH</c> into
    /// <c>LEVELHIGH</c>.
    /// </summary>
    private static string ShoutyToPascalCase(string input)
    {
        var builder = new StringBuilder(input.Length);
        var inRun = false;

        foreach (var c in input)
        {
            if (!char.IsAsciiLetterOrDigit(c))
            {
                inRun = false;
                continue;
            }

            builder.Append(inRun ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c));
            inRun = true;
        }

        return builder.ToString();
    }

    /// <summary>The chain of enclosing message names, outermost first, ending with <paramref name="leaf"/>.</summary>
    private static List<string> EnclosingNames(MessageDescriptor? containingType, string leaf)
    {
        var parts = new List<string>();
        for (var current = containingType; current is not null; current = current.ContainingType)
        {
            parts.Insert(0, current.Name);
        }

        parts.Add(leaf);
        return parts;
    }

    /// <summary>Prefixes a namespace, tolerating files that declare no protobuf package.</summary>
    private static string QualifyCSharp(FileDescriptor file, string name)
    {
        var ns = GetCSharpNamespace(file);
        return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
    }
}
