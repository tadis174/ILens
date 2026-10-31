using System.Reflection;
using System.Reflection.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using ICSharpCode.Decompiler.TypeSystem.Implementation;

namespace ILens;

/// <summary>
/// How a parameter is passed: by value, or by reference with the keyword its declaration has.
/// Whether a caller may pass its arguments one by one, as params, is <see cref="IsParams"/>.
/// </summary>
/// <param name="ByReference">False for a parameter passed by value.</param>
/// <param name="Keyword">
/// <c>ref</c>, <c>out</c>, <c>in</c> or <c>ref readonly</c>, and <c>None</c> for a parameter
/// passed by value and for one passed by reference whose keyword is not known.
/// </param>
/// <param name="ReadOnly">
/// True where the method may not write through the reference: an <c>in</c> or
/// <c>ref readonly</c> parameter, and one whose keyword is not known but whose signature
/// carries the marker of the two.
/// </param>
/// <remarks>
/// <para>Ask <see cref="Of"/> for this, never ILSpy's <c>IParameter.ReferenceKind</c>, which is
/// wrong in two ways. It answers by value unless the parameter's type is a reference itself,
/// and the compiler writes the <c>in</c> or <c>ref readonly</c> parameter of a virtual,
/// abstract or interface method, and of a delegate, as a reference inside a required modifier,
/// <c>modreq(InAttribute)</c>. ILens's own type system keeps such modifiers, as ILSpyX builds
/// it, so there the property says by value for exactly those parameters. And it tells
/// <c>ref readonly</c> from <c>ref</c> only under an option the decompiler's type system does
/// not have. The parameter's own metadata row says the keyword whatever the options.</para>
/// <para>The modifiers stay in the type system because they are part of a signature. Built
/// without them, it made two overloads of a C++/CLI library that differ only in a modifier
/// print alike, and <c>list_members</c> listed one of them: 20 of 264 answers on one such
/// library lost a member.</para>
/// <para>A method whose declaration ILens did not read, one of an assembly it could not load
/// or one the loaded assembly does not declare, has no row. It is known only from the signature
/// a call names, which says that a parameter is passed by reference and, through the marker,
/// that it is read-only, but not with which keyword.</para>
/// </remarks>
public readonly record struct ParameterPassing(bool ByReference, ReferenceKind Keyword, bool ReadOnly)
{
    /// <summary>True for a parameter passed by reference whose keyword is not known.</summary>
    public bool KeywordIsUnknown => ByReference && Keyword == ReferenceKind.None;

    public static ParameterPassing Of(IParameter parameter)
    {
        var type = parameter.Type;
        var marked = false;
        while (type is ModifiedType modified)
        {
            marked |= (modified.Modifier.Namespace, modified.Modifier.Name)
                is ("System.Runtime.InteropServices", "InAttribute");
            type = modified.ElementType;
        }
        if (type is not ByReferenceType)
            return default;

        // A parameter ILSpy made up for a method it knows no declaration of has no row, and
        // ILSpy's own answer for it is that the keyword is not known.
        var keyword = DeclaredKeyword(parameter) ?? parameter.ReferenceKind;
        return new(true, keyword, marked || keyword is ReferenceKind.In or ReferenceKind.RefReadOnly);
    }

    /// <summary>
    /// True where a caller may pass the parameter's arguments one by one: an array ILSpy reads as
    /// params, and a collection the compiler marks with <c>ParamCollectionAttribute</c>, a params
    /// collection of C# 13 such as <c>params ReadOnlySpan&lt;T&gt; items</c>. A declaration asks
    /// this, not ILSpy's <c>IParameter.IsParams</c>, which reads <c>ParamArrayAttribute</c> on an
    /// array alone in ICSharpCode.Decompiler 9.1, a version with no setting for C# 13.
    /// </summary>
    public static bool IsParams(IParameter parameter) =>
        parameter.IsParams
        || (RowOf(parameter) is (var metadata, { } row) && Carries(metadata, row, "ParamCollectionAttribute"));

    /// <summary>
    /// The keyword of a parameter passed by reference as its method's definition declares it,
    /// or null for a parameter of a method no definition was read for.
    /// </summary>
    private static ReferenceKind? DeclaredKeyword(IParameter parameter)
    {
        if (RowOf(parameter) is not (var metadata, var found))
            return null;
        // A compiler may leave out the row of a parameter that has nothing to say in one.
        if (found is not { } row)
            return ReferenceKind.Ref;

        var direction = row.Attributes & (ParameterAttributes.In | ParameterAttributes.Out);
        if (direction == ParameterAttributes.Out)
            return ReferenceKind.Out;
        if (Carries(metadata, row, "IsReadOnlyAttribute"))
            return ReferenceKind.In;
        return direction == ParameterAttributes.In && Carries(metadata, row, "RequiresLocationAttribute")
            ? ReferenceKind.RefReadOnly
            : ReferenceKind.Ref;
    }

    /// <summary>
    /// The metadata row of a parameter as its method's definition declares it, with the metadata
    /// that holds it. Null for a parameter of a method no definition was read for; the row is null
    /// where the definition has none for the parameter.
    /// </summary>
    private static (MetadataReader Metadata, Parameter? Row)? RowOf(IParameter parameter)
    {
        if (parameter.Owner is not { } owner)
            return null;
        var index = -1;
        for (var i = 0; i < owner.Parameters.Count && index < 0; i++)
        {
            if (ReferenceEquals(owner.Parameters[i], parameter))
                index = i;
        }

        // An indexer has no parameter rows of its own. Its accessors have, in the same order.
        var declaring = owner.MemberDefinition is IProperty property
            ? property.Getter ?? property.Setter
            : owner.MemberDefinition;
        if (index < 0 || declaring is not { ParentModule: MetadataModule module }
            || declaring.MetadataToken.IsNil || declaring.MetadataToken.Kind != HandleKind.MethodDefinition)
            return null;

        var metadata = module.MetadataFile.Metadata;
        foreach (var handle in metadata.GetMethodDefinition((MethodDefinitionHandle)declaring.MetadataToken).GetParameters())
        {
            var row = metadata.GetParameter(handle);
            if (row.SequenceNumber == index + 1)
                return (metadata, row);
        }
        return (metadata, null);
    }

    /// <summary>
    /// True if the parameter carries the attribute <paramref name="name"/> of
    /// <c>System.Runtime.CompilerServices</c>, which the compiler adds to the assembly itself
    /// where the framework it compiles for has none.
    /// </summary>
    private static bool Carries(MetadataReader metadata, Parameter row, string name)
    {
        foreach (var handle in row.GetCustomAttributes())
        {
            var constructor = metadata.GetCustomAttribute(handle).Constructor;
            var (typeNamespace, typeName) = constructor.Kind switch
            {
                HandleKind.MethodDefinition
                    when metadata.GetTypeDefinition(metadata.GetMethodDefinition((MethodDefinitionHandle)constructor)
                        .GetDeclaringType()) is var definition
                    => (definition.Namespace, definition.Name),
                HandleKind.MemberReference
                    when metadata.GetMemberReference((MemberReferenceHandle)constructor).Parent is
                        { Kind: HandleKind.TypeReference } parent
                        && metadata.GetTypeReference((TypeReferenceHandle)parent) is var reference
                    => (reference.Namespace, reference.Name),
                _ => (default(StringHandle), default(StringHandle)),
            };
            if (!typeName.IsNil && metadata.StringComparer.Equals(typeName, name)
                && metadata.StringComparer.Equals(typeNamespace, "System.Runtime.CompilerServices"))
                return true;
        }
        return false;
    }
}
