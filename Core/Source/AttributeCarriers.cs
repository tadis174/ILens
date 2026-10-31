using System.Reflection.Metadata;
using ICSharpCode.Decompiler.TypeSystem;

namespace ILens;

/// <summary>
/// What carries a custom attribute, as an analyze answer names it: the type or member the
/// attribute is written on, the method for an attribute on one of its parameters or on its
/// return value, the property or event for one on an accessor, the type or method that declares
/// a type parameter for one on that parameter or on one of its constraints, and the assembly or
/// the module as its <see cref="GlobalAttributeTarget"/>. ILSpyX's
/// <c>AnalyzerHelpers.GetParentEntity</c> names them the same way, except that it has one symbol
/// for the assembly and the module.
/// </summary>
public static class AttributeCarriers
{
    /// <summary>
    /// The carriers of the attributes in <paramref name="module"/> that pass
    /// <paramref name="test"/>. The result is not distinct: a carrier of several such attributes
    /// can come back more than once. An attribute the test cannot decode counts as one that does
    /// not pass it.
    /// </summary>
    public static IEnumerable<ISymbol> Of(MetadataModule module, Func<CustomAttribute, bool> test)
    {
        var parameters = new HashSet<ParameterHandle>();
        foreach (var (carrier, parameter) in Carried(module, test))
        {
            if (carrier is null)
                parameters.Add(parameter);
            else
                yield return carrier;
        }
        if (parameters.Count == 0)
            yield break;
        var metadata = module.MetadataFile.Metadata;
        foreach (var handle in metadata.MethodDefinitions)
        {
            if (metadata.GetMethodDefinition(handle).GetParameters().Any(parameters.Contains))
                yield return SymbolOf(module.GetDefinition(handle));
        }
    }

    /// <summary>
    /// How many attributes in <paramref name="module"/> pass <paramref name="test"/> and sit
    /// where <see cref="Of"/> names a carrier. Each attribute counts, so a carrier of two such
    /// attributes counts twice.
    /// </summary>
    public static int Count(MetadataModule module, Func<CustomAttribute, bool> test) =>
        Carried(module, test).Count();

    /// <summary>
    /// For each attribute in <paramref name="module"/> that passes <paramref name="test"/>, what
    /// carries it, or for an attribute on a parameter or a return value that parameter and no
    /// carrier, since finding its method takes a pass over every method. An attribute on
    /// anything else, such as the row that says a class implements an interface, where only
    /// the compiler puts one, gives nothing.
    /// </summary>
    private static IEnumerable<(ISymbol Carrier, ParameterHandle Parameter)> Carried(
        MetadataModule module, Func<CustomAttribute, bool> test)
    {
        var metadata = module.MetadataFile.Metadata;
        foreach (var handle in metadata.CustomAttributes)
        {
            var attribute = metadata.GetCustomAttribute(handle);
            if (!Passes(test, attribute))
                continue;
            var parent = attribute.Parent;
            switch (parent.Kind)
            {
                case HandleKind.Parameter:
                    yield return (null, (ParameterHandle)parent);
                    break;
                case HandleKind.MethodDefinition:
                    yield return (SymbolOf(module.GetDefinition((MethodDefinitionHandle)parent)), default);
                    break;
                case HandleKind.TypeDefinition:
                case HandleKind.FieldDefinition:
                case HandleKind.PropertyDefinition:
                case HandleKind.EventDefinition:
                    yield return (module.ResolveEntity(parent), default);
                    break;
                case HandleKind.ModuleDefinition:
                case HandleKind.AssemblyDefinition:
                    yield return (GlobalAttributeTarget.Of(metadata, parent), default);
                    break;
                case HandleKind.GenericParameter:
                    yield return (module.ResolveEntity(metadata.GetGenericParameter((GenericParameterHandle)parent).Parent), default);
                    break;
                case HandleKind.GenericParameterConstraint:
                    var constraint = metadata.GetGenericParameterConstraint((GenericParameterConstraintHandle)parent);
                    yield return (module.ResolveEntity(metadata.GetGenericParameter(constraint.Parameter).Parent), default);
                    break;
            }
        }
    }

    /// <summary>
    /// A test that passes the attributes applied through <paramref name="constructor"/>, a
    /// constructor <paramref name="module"/> defines. An attribute names its constructor
    /// outright: by the row that defines it, or for an attribute class with type parameters by
    /// a reference row, which names the constructor of one construction of the class.
    /// </summary>
    public static Func<CustomAttribute, bool> AppliedThrough(MetadataModule module, IMethod constructor)
    {
        var definition = constructor.MetadataToken;
        var references = new Dictionary<EntityHandle, bool>();
        return attribute =>
        {
            var named = attribute.Constructor;
            if (named.Kind == HandleKind.MethodDefinition)
                return named == definition;
            if (!references.TryGetValue(named, out var isTheConstructor))
                references[named] = isTheConstructor =
                    module.ResolveMethod(named, default)?.MemberDefinition is { } resolved
                    && resolved.MetadataToken == definition
                    && resolved.ParentModule?.MetadataFile == module.MetadataFile;
            return isTheConstructor;
        };
    }

    private static bool Passes(Func<CustomAttribute, bool> test, CustomAttribute attribute)
    {
        try
        {
            return test(attribute);
        }
        catch (Exception e) when (e is BadImageFormatException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// What carries the attributes of one class whose bytes did not decide whether they name a
    /// type, or set a member by name, since they can be read with more than one width for the
    /// values of <paramref name="Enum"/>. Both names are as an answer prints a type.
    /// </summary>
    public sealed record Undecided(string Attribute, string Enum, IReadOnlyList<ISymbol> Carriers);

    /// <summary>
    /// The carriers of the attributes <paramref name="arguments"/> has left undecided, by
    /// attribute class and enum.
    /// </summary>
    public static IReadOnlyList<Undecided> UndecidedOf(MetadataModule module, AttributeArgumentReader arguments) =>
        UndecidedOf(module, arguments.Undecided);

    /// <summary>The carriers of <paramref name="undecided"/>, by attribute class and enum.</summary>
    public static IReadOnlyList<Undecided> UndecidedOf(
        MetadataModule module, IReadOnlyList<AttributeArgumentReader.UndecidedAttribute> undecided)
    {
        if (undecided.Count == 0)
            return [];
        return undecided
            .GroupBy(attribute => (Attribute: ClassOf(attribute.Constructor), attribute.Enum))
            .Select(group =>
            {
                var rows = group.Select(attribute => (attribute.Parent, attribute.Constructor, attribute.Value)).ToHashSet();
                return new Undecided(group.Key.Attribute, group.Key.Enum,
                    Of(module, attribute => rows.Contains((attribute.Parent, attribute.Constructor, attribute.Value))).ToList());
            })
            .ToList();

        string ClassOf(EntityHandle constructor) =>
            module.ResolveMethod(constructor, default)?.DeclaringType is { } attributeType
                ? ReferenceFormatter.FormatTypeName(attributeType)
                : "an attribute";
    }

    /// <summary>A method as the analyzers report it: an accessor as its property or event.</summary>
    private static ISymbol SymbolOf(IMethod method) => (ISymbol)method.AccessorOwner ?? method;
}
