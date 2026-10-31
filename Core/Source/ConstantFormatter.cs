using System.Runtime.CompilerServices;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp.OutputVisitor;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.Semantics;
using ICSharpCode.Decompiler.TypeSystem;
using ICSharpCode.Decompiler.TypeSystem.Implementation;

namespace ILens;

/// <summary>
/// Writes a value the metadata stores, a constant's or the default a parameter takes, as the
/// decompiled code writes it, through the decompiler's own
/// <see cref="TypeSystemAstBuilder.ConvertConstantValue(IType, object)"/>: <c>true</c> and
/// <c>false</c>; a number in the invariant culture, with the suffix C# requires, such as
/// <c>1.5m</c> or <c>0.5f</c>; a string or a character in quotes with its escapes; <c>null</c>;
/// a special value such as <c>double.NaN</c> or <c>int.MaxValue</c>, by its name except in a
/// constant that would read as itself (<see cref="NamesSpecialValues"/>); and an enum's value by its
/// member's name, a combination of flags as <c>Mode.A | Mode.B</c>, and a value no member has,
/// or one of an enum whose library ILens did not find, as <c>(Mode)7</c>. A type in the value
/// is written as <see cref="ReferenceFormatter.FormatTypeRef(IType, bool)"/> writes it, short
/// or in full. A null default of a value type is written <c>default</c>, which C# reads by the
/// parameter's type before it, where the decompiled code writes <c>default(T)</c>.
/// </summary>
public static class ConstantFormatter
{
    private static readonly CSharpFormattingOptions Formatting = new DecompilerSettings().CSharpFormattingOptions;

    // ILens's own type system loads no other assembly, so an enum another assembly defines is an
    // unknown type there, with no member to name a value by. Each host registers how a value of
    // its module reads such an enum in the decompiler's type system, which holds the assemblies
    // the reference search found; the entry goes with the module when the host is closed.
    private static readonly ConditionalWeakTable<IModule, Func<FullTypeName, ITypeDefinition>> ExternalTypes = new();

    /// <summary>
    /// Lets a value of <paramref name="module"/> name an enum another assembly defines, as
    /// <paramref name="findExternalType"/> reads it.
    /// </summary>
    public static void ReadEnumsThrough(IModule module, Func<FullTypeName, ITypeDefinition> findExternalType) =>
        ExternalTypes.AddOrUpdate(module, findExternalType);

    /// <summary>
    /// A constant's value. An enum's own member is written as the number it stands for, since its
    /// name would only repeat the member's: <c>public const Mode Fast = 2</c>.
    /// </summary>
    public static string ValueOf(IField field, bool full = false)
    {
        var type = field.DeclaringTypeDefinition is { Kind: TypeKind.Enum } enumType
            ? enumType.EnumUnderlyingType
            : field.Type;
        return Format(type, field.GetConstantValue(), field.ParentModule, full, NamesSpecialValues(field));
    }

    /// <summary>
    /// Whether a constant's value may be written by the name of a special value, as the decompiler
    /// decides it for a field (<c>CSharpDecompiler.DoDecompile(IField, ...)</c>): not for a field of
    /// the type that declares it, nor for <c>PI</c> and <c>E</c> of <c>System.Math</c> and
    /// <c>System.MathF</c>, so that a constant is written by its value and not as itself, as in
    /// <c>public const int MaxValue = 2147483647</c> and <c>public const double PI = 3.141592653589793</c>.
    /// </summary>
    private static bool NamesSpecialValues(IField field) =>
        !(field.DeclaringType.ReflectionName == field.Type.ReflectionName
          || (field.Name is "PI" or "E" && field.DeclaringType.ReflectionName is "System.Math" or "System.MathF"));

    /// <summary>
    /// The default <paramref name="parameter"/> takes when a call leaves its argument out, or
    /// <c>?</c> where its metadata cannot be read, which the decompiler would otherwise read as
    /// no value at all.
    /// </summary>
    public static string DefaultOf(IParameter parameter, bool full = false)
    {
        object value;
        try
        {
            value = parameter.GetConstantValue(throwOnInvalidMetadata: true);
        }
        catch (BadImageFormatException)
        {
            return "?";
        }
        return Format(parameter.Type, value, parameter.Owner?.ParentModule, full, specialValues: true);
    }

    private static string Format(IType type, object value, IModule module, bool full, bool specialValues)
    {
        // A parameter passed by in or ref readonly carries its default like any other; its type
        // is the type it refers to, inside the modifier the compiler may add.
        while (type is ByReferenceType or ModifiedType)
            type = ((TypeWithElementType)type).ElementType;

        if (value is null)
            return type.IsReferenceType == true || NullableType.IsNullable(type)
                || type.Kind is TypeKind.Pointer or TypeKind.FunctionPointer
                ? "null"
                : "default";

        var builder = new TypeSystemAstBuilder
        {
            AlwaysUseShortTypeNames = true,
            AddResolveResultAnnotations = true,
            UseSpecialConstants = specialValues,
        };
        var expression = builder.ConvertConstantValue(Readable(type, value, module) ?? type, value);
        // A type written by its keyword, as in int.MaxValue, reads alike in both forms and stays.
        foreach (var named in expression.DescendantsAndSelf.OfType<AstType>()
                     .Where(node => node.Parent is not AstType && node is not PrimitiveType).ToList())
        {
            if (named.Annotation<TypeResolveResult>() is { } resolved)
                named.ReplaceWith(new SimpleType(ReferenceFormatter.FormatTypeRef(resolved.Type, full)));
        }
        expression.AcceptVisitor(new InsertParenthesesVisitor { InsertParenthesesForReadability = true });
        return expression.ToString(Formatting);
    }

    /// <summary>
    /// The type of a value of <paramref name="type"/> as the decompiler's type system reads it,
    /// where ILens's own type system holds too little of it to write the value as the decompiled
    /// code does: an enum another assembly defines, which ILens's own type system knows by name
    /// alone, and a type of the core library, which it holds without members, so that a special
    /// value reads <c>double.NaN</c> or <c>int.MaxValue</c>. An integer stored for a type known by
    /// name alone is a value of an enum, since C# stores no other default or constant of a type
    /// that is not one of its own. Null where ILens's own type system holds the type, where the
    /// module registered no reading, or where no assembly the reference search found defines it.
    /// </summary>
    private static ITypeDefinition Readable(IType type, object value, IModule module)
    {
        var underlying = NullableType.GetUnderlyingType(type);
        var unknown = underlying.Kind == TypeKind.Unknown;
        var standIn = unknown
            ? IsIntegral(value)
            : underlying.GetDefinition() is { ParentModule: not MetadataModule };
        if (!standIn || module is null || !ExternalTypes.TryGetValue(module, out var find)
            || ReferenceFormatter.FullTypeNameOf(underlying) is not { } name)
            return null;
        var found = find(name);
        return !unknown || found is { Kind: TypeKind.Enum } ? found : null;
    }

    private static bool IsIntegral(object value) =>
        value is sbyte or byte or short or ushort or int or uint or long or ulong;
}
