using System.Collections.Immutable;
using System.Reflection.Metadata;

namespace ILens;

/// <summary>
/// Decodes a type's custom attributes straight from metadata, tolerating the one
/// failure that defeats ILSpy's high-level <c>IAttribute.FixedArguments</c>: a
/// constructor argument whose enum type lives in a referenced assembly that the
/// scanned module's type system can't resolve.
///
/// ILSpy's <c>CustomAttribute.DecodeValue</c> hands the blob to a type provider
/// whose <c>GetUnderlyingEnumType</c> throws <c>EnumUnderlyingTypeResolveException</c>
/// for such an enum, and the catch discards the <em>entire</em> argument list —
/// so <c>[HarmonyPatch(typeof(X), MethodType.Constructor, ...)]</c> decodes to
/// nothing (see TASK-23). This reader uses its own provider whose
/// <c>GetUnderlyingEnumType</c> simply assumes <c>Int32</c> — correct for
/// HarmonyLib's <c>MethodType</c> and effectively every attribute enum — so the
/// decode succeeds. Everything it reads (TypeRefs, the value blob, the
/// constructor signature) lives in the scanned module itself; the referenced
/// enum's defining assembly never has to be loaded.
/// </summary>
public static class LenientAttributeReader
{
    /// <summary>One decoded fixed argument. <see cref="ArgType"/> is the rendered
    /// parameter type (e.g. <c>System.Type</c>, <c>System.Type[]</c>,
    /// <c>HarmonyLib.MethodType</c>); <see cref="Value"/> is a normalized type
    /// name string for a <c>typeof</c>, an <c>IReadOnlyList&lt;string&gt;</c> of
    /// normalized type names for a type array, a boxed <c>int</c> for an enum, or
    /// the raw scalar otherwise.</summary>
    public sealed record LenientArg(string ArgType, object Value);

    public sealed record DecodedAttribute(
        string AttributeTypeName, IReadOnlyList<LenientArg> FixedArguments);

    /// <summary>
    /// Decode every custom attribute on the given type. Attributes whose value
    /// blob is malformed are skipped rather than aborting the whole list.
    /// </summary>
    public static IReadOnlyList<DecodedAttribute> ReadTypeAttributes(
        MetadataReader metadata, TypeDefinitionHandle typeHandle)
    {
        var provider = new StringTypeProvider();
        var results = new List<DecodedAttribute>();
        var typeDef = metadata.GetTypeDefinition(typeHandle);

        foreach (var attrHandle in typeDef.GetCustomAttributes())
        {
            var attr = metadata.GetCustomAttribute(attrHandle);
            var attrTypeName = GetAttributeTypeName(metadata, attr.Constructor);
            if (attrTypeName == null) continue;

            CustomAttributeValue<string> value;
            try
            {
                value = attr.DecodeValue(provider);
            }
            catch (System.BadImageFormatException)
            {
                continue;
            }

            var args = new List<LenientArg>(value.FixedArguments.Length);
            foreach (var a in value.FixedArguments)
                args.Add(new LenientArg(a.Type, NormalizeArgValue(a)));
            results.Add(new DecodedAttribute(attrTypeName, args));
        }

        return results;
    }

    /// <summary>
    /// Normalize a decoded argument value: a type array becomes a list of clean
    /// type-name strings, a <c>System.Type</c> value becomes one clean name, and
    /// every other value passes through unchanged.
    /// </summary>
    private static object NormalizeArgValue(CustomAttributeTypedArgument<string> a)
    {
        if (a.Value is ImmutableArray<CustomAttributeTypedArgument<string>> elements)
            return elements.Select(e => NormalizeTypeName(e.Value as string)).ToList();
        if (a.Type == "System.Type" && a.Value is string typeName)
            return NormalizeTypeName(typeName);
        return a.Value;
    }

    /// <summary>
    /// Turn a custom-attribute serialized type name into a readable form: strip
    /// the trailing assembly qualifier and rewrite a generic reflection name
    /// (<c>List`1[[Int32, …]]</c>) into angle-bracket form (<c>List&lt;Int32&gt;</c>),
    /// recursing into type arguments. Best-effort — an unparseable name returns
    /// with just the assembly qualifier stripped.
    /// </summary>
    public static string NormalizeTypeName(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;

        raw = StripAssemblyQualifier(raw.Trim());

        int tick = raw.IndexOf('`');
        if (tick < 0) return raw;
        // The generic type-argument list is a single bracketed group after the
        // arity: List`1[[Int32, asm]]. The OUTER brackets delimit the list; each
        // argument is itself bracketed as [AssemblyQualifiedName]. Take the outer
        // group, split its top-level args, then unwrap and recurse into each.
        int open = raw.IndexOf('[', tick);
        int close = raw.LastIndexOf(']');
        if (open < 0 || close <= open) return raw;

        var baseName = raw.Substring(0, tick);
        var inner = raw.Substring(open + 1, close - (open + 1));
        var args = SplitTopLevel(inner).Select(p =>
        {
            p = p.Trim();
            if (p.Length >= 2 && p[0] == '[' && p[^1] == ']')
                p = p.Substring(1, p.Length - 2);
            return NormalizeTypeName(p);
        });
        return baseName + "<" + string.Join(", ", args) + ">";
    }

    /// <summary>Drop a top-level <c>, Assembly, Version=…</c> suffix, leaving any
    /// bracketed generic arguments (which carry their own qualifiers) intact.</summary>
    private static string StripAssemblyQualifier(string s)
    {
        int depth = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '[') depth++;
            else if (c == ']') depth--;
            else if (c == ',' && depth == 0)
                return s.Substring(0, i).Trim();
        }
        return s;
    }

    /// <summary>Split a <c>[arg1],[arg2]</c> generic-argument list on its
    /// top-level <c>,</c> separators, ignoring commas nested in brackets.</summary>
    private static List<string> SplitTopLevel(string s)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '[') depth++;
            else if (c == ']') depth--;
            else if (c == ',' && depth == 0)
            {
                parts.Add(s.Substring(start, i - start));
                start = i + 1;
            }
        }
        parts.Add(s.Substring(start));
        return parts;
    }

    private static string GetAttributeTypeName(MetadataReader metadata, EntityHandle ctor)
    {
        return ctor.Kind switch
        {
            HandleKind.MemberReference =>
                TypeNameFromHandle(metadata,
                    metadata.GetMemberReference((MemberReferenceHandle)ctor).Parent),
            HandleKind.MethodDefinition =>
                TypeNameFromHandle(metadata,
                    metadata.GetMethodDefinition((MethodDefinitionHandle)ctor).GetDeclaringType()),
            _ => null,
        };
    }

    private static string TypeNameFromHandle(MetadataReader metadata, EntityHandle handle)
    {
        switch (handle.Kind)
        {
            case HandleKind.TypeReference:
                var tr = metadata.GetTypeReference((TypeReferenceHandle)handle);
                return Combine(metadata.GetString(tr.Namespace), metadata.GetString(tr.Name));
            case HandleKind.TypeDefinition:
                var td = metadata.GetTypeDefinition((TypeDefinitionHandle)handle);
                return Combine(metadata.GetString(td.Namespace), metadata.GetString(td.Name));
            default:
                return null;
        }
    }

    private static string Combine(string ns, string name) =>
        string.IsNullOrEmpty(ns) ? name : ns + "." + name;

    /// <summary>
    /// Type provider for <c>CustomAttribute.DecodeValue</c> that renders types as
    /// strings and — crucially — never throws on an unresolvable enum: it assumes
    /// an <c>Int32</c> backing for any enum, which is what ILSpy's strict provider
    /// refuses to do.
    /// </summary>
    private sealed class StringTypeProvider : ICustomAttributeTypeProvider<string>
    {
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
        {
            PrimitiveTypeCode.Boolean => "System.Boolean",
            PrimitiveTypeCode.Char    => "System.Char",
            PrimitiveTypeCode.SByte   => "System.SByte",
            PrimitiveTypeCode.Byte    => "System.Byte",
            PrimitiveTypeCode.Int16   => "System.Int16",
            PrimitiveTypeCode.UInt16  => "System.UInt16",
            PrimitiveTypeCode.Int32   => "System.Int32",
            PrimitiveTypeCode.UInt32  => "System.UInt32",
            PrimitiveTypeCode.Int64   => "System.Int64",
            PrimitiveTypeCode.UInt64  => "System.UInt64",
            PrimitiveTypeCode.Single  => "System.Single",
            PrimitiveTypeCode.Double  => "System.Double",
            PrimitiveTypeCode.String  => "System.String",
            PrimitiveTypeCode.Object  => "System.Object",
            _ => typeCode.ToString(),
        };

        public string GetSystemType() => "System.Type";

        public bool IsSystemType(string type) => type == "System.Type";

        public string GetSZArrayType(string elementType) => elementType + "[]";

        public string GetTypeFromDefinition(
            MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            var td = reader.GetTypeDefinition(handle);
            return Combine(reader.GetString(td.Namespace), reader.GetString(td.Name));
        }

        public string GetTypeFromReference(
            MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            var tr = reader.GetTypeReference(handle);
            return Combine(reader.GetString(tr.Namespace), reader.GetString(tr.Name));
        }

        public string GetTypeFromSerializedName(string name) => name;

        // The fix: assume Int32 rather than resolving (and throwing on) the enum.
        public PrimitiveTypeCode GetUnderlyingEnumType(string type) => PrimitiveTypeCode.Int32;
    }
}
