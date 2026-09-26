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
    /// The list exists to agree with a header protoc generated: a field, a type, an enum value or a
    /// package component whose name is on it is spelled with an underscore, and one whose name is not
    /// is spelled as it stands, whatever the standard says. The C++ backend escapes its own
    /// identifiers against the same list, because a name protoc will not use is no more usable as a
    /// method or a local. It used to keep a list of its own, and the two had drifted -- that one was
    /// missing <c>const_cast</c>, <c>char8_t</c> and the alternative tokens such as <c>not_eq</c>.
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
    /// The members of a generated C++ message that a class of the same name would collide with.
    /// <c>MessageKnownMethodsCamelCase</c> in protoc's <c>helpers.cc</c>.
    /// </summary>
    /// <remarks>
    /// A different list from <see cref="CppMessageNullaryMembers"/>, because protoc checks a
    /// different one: this is the list it holds a type name against, and that one a field name.
    /// </remarks>
    private static readonly HashSet<string> CppMessageKnownMethods = new(StringComparer.Ordinal)
    {
        "GetDescriptor", "GetReflection", "default_instance", "Swap", "UnsafeArenaSwap", "New",
        "CopyFrom", "MergeFrom", "IsInitialized", "GetMetadata", "Clear",
    };

    /// <summary>
    /// Converts a protobuf <c>snake_case</c> name to <c>PascalCase</c>, matching the C# protobuf
    /// generator so <c>unit_price_cents</c> lines up with the generated <c>UnitPriceCents</c>.
    /// The members protoc's C# generator declares or overrides on every message, which a property of
    /// the same name would collide with. <c>reserved_member_names</c> in the generator's
    /// <c>GetPropertyName</c>.
    /// </summary>
    /// <remarks>
    /// Deliberately not every inherited member. protoc leaves out <c>GetType</c> and
    /// <c>MemberwiseClone</c>, because a property hiding either is a warning rather than an error and
    /// renaming one now would break code already written against it. So a field <c>get_type</c> is
    /// <c>GetType</c>, and this list has to stay protoc's rather than grow into a complete one.
    /// </remarks>
    private static readonly HashSet<string> CSharpMessageMembers = new(StringComparer.Ordinal)
    {
        "Types", "Descriptor", "Equals", "ToString", "GetHashCode", "WriteTo", "Clone",
        "CalculateSize", "MergeFrom", "OnConstruction", "Parser",
    };

    /// <summary>
    /// Converts a <c>snake_case</c> name to <c>PascalCase</c> by capitalizing the first letter and every
    /// letter after an underscore, so <c>line_total_cents</c> becomes <c>LineTotalCents</c>. The C#
    /// backend names methods and extension classes with it.
    /// </summary>
    /// <remarks>
    /// Close to protoc's C# rule, and not it: <see cref="UnderscoresToPascalCase"/> also capitalizes a
    /// letter after a digit and keeps an underscore before a leading digit. A field's property and a
    /// file's namespace have to be exactly the ones protoc declared, so they are named by
    /// <see cref="GetCSharpPropertyName"/> and <see cref="GetCSharpNamespace"/> instead. A method name
    /// only has to agree with itself, and moving this rule would rename methods that callers already
    /// compile against.
    /// </remarks>
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
    /// What follows a source's name in the names of the tests generated from it:
    /// <c>pricing.tests.g.cs</c>, <c>pricing.tests.cc</c>.
    /// </summary>
    /// <remarks>
    /// Here rather than in each backend, because a test source's own files are generated into the
    /// test output beside every source's tests, and <c>PC2006</c> has to know what those are called
    /// before any backend runs (spec 5.3).
    /// </remarks>
    public const string TestsSuffix = ".tests";

    /// <summary>The name of the arithmetic runtime every C# source's behavior shares.</summary>
    public const string CSharpRuntimeName = "ProtoCrossArithmetic";

    /// <summary>The name of the support file C# tests that <c>expect fail</c> share.</summary>
    public const string CSharpTestRuntimeName = "ProtoCrossTestSupport";

    /// <summary>The name of the arithmetic runtime every C++ source's behavior shares.</summary>
    public const string CppRuntimeName = "protocross_runtime";

    /// <summary>
    /// The names a backend generates a file under whatever the sources are called, which no source
    /// may take (spec 5.3).
    /// </summary>
    /// <remarks>
    /// Each is generated beside sources' own files -- the runtimes beside the behavior, and beside a
    /// test source's behavior in the test output; the test support beside the tests -- so a source of
    /// one of these names would be generated over it. They are compared by <see cref="OutputKey"/>
    /// like every other generated name, and refused whichever backend is asked for, because which
    /// backends run is decided after the compilation is.
    /// </remarks>
    public static IReadOnlyList<string> FixedNames { get; } = [CSharpRuntimeName, CSharpTestRuntimeName, CppRuntimeName];

    /// <summary>
    /// What a source's name comes to once everything any backend derives from it has folded it: its
    /// letters and digits, upper-cased, with a <c>T</c> in front when the first is not a letter.
    /// Two sources whose names give one key cannot be generated side by side.
    /// </summary>
    /// <param name="sourceStem">The source's file name without its extension.</param>
    /// <remarks>
    /// <para>
    /// Every generated name is derived from the source's, and each derivation throws something away.
    /// The file names keep case, which a file system may not. A C++ include guard upper-cases and
    /// turns punctuation into underscores, so <c>a-b</c> and <c>a_b</c> meet there. A C# test class
    /// drops punctuation and capitalizes what follows, so <c>a_b</c> and <c>aB</c> meet there, and puts
    /// a <c>T</c> in front of a name that does not start with a letter, so <c>1a</c> and <c>t1a</c>
    /// meet there too. A CMake target turns punctuation into underscores and keeps case.
    /// </para>
    /// <para>
    /// This key throws away all of that at once, so it is coarser than each of them: two names any
    /// of them fold together fold together here. It also folds some that none of them does, such as
    /// <c>ab</c> and <c>a_b</c>, and that is the price of one rule instead of four that each have to
    /// be remembered when a fifth derived name is added.
    /// </para>
    /// </remarks>
    public static string OutputKey(string sourceStem)
    {
        ArgumentNullException.ThrowIfNull(sourceStem);

        var key = new StringBuilder(sourceStem.Length + 1);
        foreach (var c in sourceStem)
        {
            if (char.IsLetterOrDigit(c))
            {
                key.Append(char.ToUpperInvariant(c));
            }
        }

        return key.Length > 0 && char.IsLetter(key[0]) ? key.ToString() : "T" + key;
    }

    /// <summary>
    /// The C# namespace protoc declares a file's classes in: the file's <c>csharp_namespace</c> option
    /// when it sets one, and otherwise its protobuf package PascalCased, so <c>acme.v1beta1</c> is
    /// <c>Acme.V1Beta1</c>. Empty for the global namespace.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A port of <c>GetFileNamespace</c> in protoc's <c>compiler/csharp/names.cc</c>. The package is
    /// converted by <see cref="UnderscoresToPascalCase"/> in one pass with its periods kept, not a
    /// component at a time, and the difference shows in the underscore protoc keeps in front of a
    /// leading digit: it keeps one at the start of the package and nowhere else. So <c>_1x.acme</c>
    /// is <c>_1X.Acme</c>, and <c>acme._1x</c> is <c>Acme.1X</c>. That is not a C# identifier, and
    /// protoc's own output for the package does not compile, but it is the namespace protoc declares,
    /// and no other spelling names a class that exists. <see cref="ToPascalCase"/>, the simpler rule
    /// that names methods, is not close enough either: it leaves a letter after a digit alone, and
    /// <c>Acme.V1beta1</c> is not protoc's namespace.
    /// </para>
    /// <para>
    /// The option wins whenever the file sets it, even to nothing, which is how a schema with a
    /// package puts its classes in the global namespace. So the question is whether it was set, not
    /// whether it says anything.
    /// </para>
    /// <para>
    /// Reproduced rather than approximated, as <see cref="GetCSharpPropertyName"/> is and for the same
    /// reason. The rule is the same in the sources of protoc 31.1 and 33.4, and both declare the same
    /// namespace for every case described here.
    /// </para>
    /// </remarks>
    public static string GetCSharpNamespace(FileDescriptor file)
        => file.GetOptions() is { HasCsharpNamespace: true } options
            ? options.CsharpNamespace
            : UnderscoresToPascalCase(file.Package, preservePeriod: true);

    /// <summary>
    /// The C++ namespace protoc would use: the protobuf package with dots replaced by <c>::</c>, and
    /// each component escaped on its own, so <c>acme.new.default</c> is <c>acme::new_::default_</c>.
    /// </summary>
    /// <remarks>
    /// A port of <c>Namespace</c> in protoc's <c>compiler/cpp/helpers.cc</c>, which passes every
    /// component through <see cref="EscapeCppKeyword"/>. Only the keyword list applies: a package is
    /// not a class, so a component named <c>New</c> or <c>Swap</c> is left alone.
    /// </remarks>
    public static string GetCppNamespace(FileDescriptor file)
        => string.Join("::", file.Package.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(EscapeCppKeyword));

    /// <summary>The generated protobuf C++ header for a .proto file: <c>foo.proto</c> to <c>foo.pb.h</c>.</summary>
    public static string GetCppProtoHeader(FileDescriptor file)
    {
        var name = file.Name;
        return name.EndsWith(".proto", StringComparison.Ordinal)
            ? string.Concat(name.AsSpan(0, name.Length - ".proto".Length), ".pb.h")
            : name + ".pb.h";
    }

    /// <summary>
    /// The C++ class protoc generates for a message, unqualified: the message's name joined to its
    /// enclosing messages' with underscores, for example <c>Outer_Inner</c> for a message nested
    /// inside <c>Outer</c>, and escaped as <see cref="EscapeCppTypeName"/> describes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A port of <c>ClassName</c> in protoc's <c>compiler/cpp/helpers.cc</c>, and the order of its
    /// two steps is protoc's. The enclosing part is this method's own answer for the parent, escape
    /// included, and the escape is then applied to the whole flattened name. So <c>New</c> is
    /// <c>New_</c>, a message nested in it is <c>New__Inner</c>, with the parent's underscore still in
    /// it, and <c>Plain.Swap</c> is <c>Plain_Swap</c>, because the flattened name collides with
    /// nothing even though its last part would.
    /// </para>
    /// <para>
    /// Reproduced rather than approximated, as <see cref="GetCppFieldName"/> is and for the same
    /// reason. The rule and its lists are the same in the sources of protoc 31.1 and 33.4, and were
    /// checked against the headers both generate.
    /// </para>
    /// </remarks>
    public static string GetCppTypeName(MessageDescriptor message)
        => EscapeCppTypeName(message.ContainingType is { } parent
            ? GetCppTypeName(parent) + "_" + message.Name
            : message.Name);

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
    /// <remarks>
    /// A port of protoc's <c>ClassName</c> for an enum, which is not the rule for a message. A
    /// top-level enum is escaped as a class would be, so <c>Swap</c> is <c>Swap_</c>. A nested one is
    /// named after its parent's class, escape included, so <c>New.Kind</c> is <c>New__Kind</c>, and
    /// then left as it is. That holds even when the flattened name lands on the keyword list, as
    /// <c>wchar.t</c> does: protoc emits an enum called <c>wchar_t</c>, whose header does not
    /// compile, and no spelling here could make it.
    /// </remarks>
    public static string GetCppTypeName(EnumDescriptor enumType)
        => enumType.ContainingType is { } parent
            ? GetCppTypeName(parent) + "_" + enumType.Name
            : EscapeCppTypeName(enumType.Name);

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
    /// <remarks>
    /// The value's own name is escaped first, as protoc's <c>EnumValueName</c> does, and the prefix
    /// goes on afterwards. So a top-level value <c>new</c> is <c>new_</c>, and a nested
    /// <c>New.Kind.class</c> is <c>New__Kind_class_</c>, keeping an underscore the prefixed name would
    /// not have needed -- the same way <c>set_class_()</c> keeps the field's.
    /// </remarks>
    public static string GetCppValueName(EnumValueDescriptor value)
    {
        var name = EscapeCppKeyword(value.Name);
        return value.EnumDescriptor.ContainingType is null
            ? name
            : GetCppTypeName(value.EnumDescriptor) + "_" + name;
    }

    /// <summary>
    /// The property protoc's C# generator declares for a field of a message, which is also the name
    /// its presence test prefixes: <c>unit_price_cents</c> is read with <c>UnitPriceCents</c> and
    /// tested with <c>HasUnitPriceCents</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A port of <c>GetPropertyName</c> in protoc's <c>compiler/csharp/csharp_helpers.cc</c>. The name
    /// is PascalCased by <see cref="UnderscoresToPascalCase"/>, then given a trailing underscore if the
    /// result is the name of the message that declares the field, which a member cannot share, or is
    /// on <see cref="CSharpMessageMembers"/>. A field <c>descriptor</c> is therefore
    /// <c>Descriptor_</c>, tested with <c>HasDescriptor_</c>, and a field <c>probe</c> in a message
    /// <c>Probe</c> is <c>Probe_</c>. Both checks see the PascalCased result, so <c>Descriptor</c> is
    /// escaped as well, and <c>CLASHING</c> in a message <c>Clashing</c> is not.
    /// </para>
    /// <para>
    /// A group is named after its type instead of its field, because protoc made the field name by
    /// lowercasing the type: <c>group MyGroup</c> declares a field <c>mygroup</c> and a property
    /// <c>MyGroup</c>. An editions field with delimited encoding is named the same way exactly when it
    /// has the shape a group would have had, which <see cref="IsGroupLike"/> decides.
    /// </para>
    /// <para>
    /// Reproduced rather than approximated, as <see cref="GetCSharpValueName"/> and
    /// <see cref="GetCppFieldName"/> are and for the same reason: a name that is nearly right is a
    /// compile error in the consumer's build, where nothing connects it back to this compiler. The
    /// rule, the member list and the case conversion are the same in the sources of protoc 31.1 and
    /// 33.4, and both generate identical properties for every case described here.
    /// </para>
    /// <para>
    /// It is not for an extension, which protoc declares as a static member of an <c>Extensions</c>
    /// class rather than as a property, and checks against the message it extends rather than against
    /// <see cref="FieldDescriptor.ContainingType"/>, the scope that declares it.
    /// </para>
    /// </remarks>
    public static string GetCSharpPropertyName(FieldDescriptor field)
    {
        var name = UnderscoresToPascalCase(IsGroupLike(field) ? field.MessageType.Name : field.Name);
        return name == field.ContainingType.Name || CSharpMessageMembers.Contains(name) ? name + "_" : name;
    }

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
    /// <para>
    /// It is a version's rule, not protobuf's forever. protoc 21.x escaped keywords only, from a list
    /// without <c>assert</c>, <c>char16_t</c>, <c>char32_t</c> or any keyword C++20 added, and no
    /// generated members. For most of the names the two disagree on, that older header does not
    /// compile on its own, since the field collides with a keyword, a member or a macro. The
    /// exception is the C++20 keywords, such as <c>char8_t</c> and <c>constinit</c>, under C++17,
    /// where they are not keywords, so a header from protoc 21.x or earlier needs a C++20 build, as
    /// the generated scaffold already uses, to pair with this spelling.
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
    /// <paramref name="name"/> as a C++ class at namespace scope: itself, or with an underscore
    /// appended when it is on <see cref="CppKeywords"/> or is one of
    /// <see cref="CppMessageKnownMethods"/>. protoc's <c>ResolveKnownNameCollisions</c> for a type.
    /// </summary>
    private static string EscapeCppTypeName(string name)
        => CppKeywords.Contains(name) || CppMessageKnownMethods.Contains(name) ? name + "_" : name;

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

    /// <summary>
    /// Converts a <c>snake_case</c> name to <c>PascalCase</c> the way protoc's C# generator does: a
    /// letter at the start, after a separator or after a digit is capitalized, every other letter and
    /// digit is kept as it is, and anything else is dropped, except a period when
    /// <paramref name="preservePeriod"/> asks for it to be kept. A port of <c>UnderscoresToCamelCase</c>
    /// in the generator's <c>names.cc</c>, called as its <c>UnderscoresToPascalCase</c> calls it for a
    /// field and as its <c>GetFileNamespace</c> does, with periods kept, for a package.
    /// </summary>
    /// <remarks>
    /// It differs from <see cref="ToPascalCase"/> in the two places that decide whether a property
    /// exists. A letter after a digit is capitalized, so <c>field1a</c> is <c>Field1A</c>, and an
    /// underscore standing before a leading digit is kept, so <c>_1st</c> is <c>_1St</c> rather than
    /// <c>1St</c>, which is not an identifier. That underscore is decided once, for the whole name, so
    /// a package keeps it only in front of its first component. protoc's rule has one more clause, an
    /// underscore for a name ending in <c>#</c>, which is left out because no field name or package can
    /// contain one.
    /// </remarks>
    private static string UnderscoresToPascalCase(string name, bool preservePeriod = false)
    {
        var builder = new StringBuilder(name.Length + 1);
        var capitalizeNext = true;

        foreach (var c in name)
        {
            if (char.IsAsciiLetter(c))
            {
                builder.Append(capitalizeNext ? char.ToUpperInvariant(c) : c);
                capitalizeNext = false;
            }
            else if (char.IsAsciiDigit(c))
            {
                builder.Append(c);
                capitalizeNext = true;
            }
            else
            {
                if (c == '.' && preservePeriod)
                {
                    builder.Append(c);
                }

                capitalizeNext = true;
            }
        }

        if (builder.Length > 0 && char.IsAsciiDigit(builder[0]) && name.StartsWith('_'))
        {
            builder.Insert(0, '_');
        }

        return builder.ToString();
    }

    /// <summary>
    /// Whether a field is a group, or an editions field that could have been written as one, which
    /// protoc names after its type rather than its field. A port of <c>IsGroupLike</c> in protoc's
    /// <c>descriptor.cc</c>.
    /// </summary>
    /// <remarks>
    /// Delimited encoding is not enough on its own: editions allow it on any message field, whatever
    /// the field is called and wherever its type is declared, and protoc names such a field after the
    /// field. It also needs the shape a group always had, a field named for its type lowercased and a
    /// type declared in the same file and scope as the field. The runtime reports delimited encoding as
    /// <see cref="FieldType.Group"/>, as protoc's own descriptors do. protoc compares the scope
    /// separately for an extension; <see cref="FieldDescriptor.ContainingType"/> is already the
    /// declaring scope for one, so the single comparison covers both.
    /// </remarks>
    private static bool IsGroupLike(FieldDescriptor field)
        => field.FieldType == FieldType.Group
            && field.Name == field.MessageType.Name.ToLowerInvariant()
            && field.MessageType.File == field.File
            && field.MessageType.ContainingType == field.ContainingType;

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
