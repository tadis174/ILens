using System.Reflection.Metadata;
using ICSharpCode.Decompiler.TypeSystem;

namespace ILens;

/// <summary>
/// Finds the attributes that set a field or a property by name, as <c>Text</c> in
/// <c>[Label(Text = "x")]</c>. An attribute's bytes hold such an argument as the member's name
/// and whether it is a field or a property. No metadata row names the member, so neither the
/// method bodies nor the reference rows show that use.
/// </summary>
/// <remarks>
/// <para>
/// The bytes do not say which class declares the member. That is found as C# finds it, from the
/// attribute's class upward (<see cref="TypeWalker.FindSetByName"/>), in the decompiler's type
/// system, which holds the assemblies the reference search found.
/// </para>
/// <para>
/// The scan is asked either about a member the inspected assembly defines, known by its row, or
/// about the members of a class another assembly defines, known by name. For such a class the
/// walk ends at the class itself, so a name is taken as the caller named it on that class,
/// whether the class declares it or inherits it. An attribute class whose walk meets a class
/// that was not read (<see cref="UnreadLibrary"/>), before it reaches the class asked about or
/// a class that declares the name, is left undecided
/// (<see cref="NotRead"/>). For a member the inspected assembly defines that cannot hide a
/// match: a class of another assembly derives from one of this assembly only where the two
/// assemblies reference each other.
/// </para>
/// <para>
/// Not thread-safe: the caller holds the host's lock.
/// </para>
/// </remarks>
public sealed class NamedArgumentScan
{
    private enum Reach { Sets, DoesNot, NotRead }

    private readonly MetadataModule _module;
    private readonly MetadataReader _metadata;
    private readonly MetadataModule _references;
    private readonly AttributeArgumentReader _reader;
    private readonly Func<IType, bool> _isTheClass;
    private readonly Func<IMember, bool> _isTheMember;

    // An assembly's attributes share few classes and few names.
    private readonly Dictionary<(EntityHandle Class, string Name), (Reach Reach, UnreadBase Unread)> _reach = new();

    // A question passes over the attributes several times, and many of them share a
    // constructor and a value.
    private readonly Dictionary<(EntityHandle Constructor, BlobHandle Value), AttributeArgumentReader.NamedArguments> _named = new();

    private NamedArgumentScan(MetadataModule module, MetadataModule references,
        Func<IType, bool> isTheClass, Func<IMember, bool> isTheMember)
    {
        _module = module;
        _metadata = module.MetadataFile.Metadata;
        _references = references;
        _reader = new AttributeArgumentReader(module);
        _isTheClass = isTheClass;
        _isTheMember = isTheMember;
    }

    /// <summary>
    /// A scan for <paramref name="member"/>, a field or property that <paramref name="module"/>
    /// defines.
    /// </summary>
    /// <param name="references">
    /// The inspected assembly's module in the decompiler's type system.
    /// </param>
    public static NamedArgumentScan ForMember(MetadataModule module, MetadataModule references, IMember member)
    {
        var definition = member.MemberDefinition.MetadataToken;
        return new(module, references, _ => false,
            declared => declared.MetadataToken == definition && declared.ParentModule == references);
    }

    /// <summary>
    /// A scan for the members of a class <paramref name="module"/> does not define, which
    /// <paramref name="isTheClass"/> knows by name.
    /// </summary>
    /// <param name="references">
    /// The inspected assembly's module in the decompiler's type system.
    /// </param>
    public static NamedArgumentScan ForClass(MetadataModule module, MetadataModule references, Func<IType, bool> isTheClass) =>
        new(module, references, isTheClass, _ => false);

    /// <summary>How a named argument names <paramref name="member"/>.</summary>
    public static AttributeArgumentReader.NamedArgument KeyOf(IMember member) => new(member is IField, member.Name);

    /// <summary>
    /// A test that passes the attributes that set <paramref name="member"/> of what the scan is
    /// asked about, for <see cref="AttributeCarriers"/>. An attribute that only may set it, see
    /// <see cref="UndecidedOn"/>, does not pass.
    /// </summary>
    public Func<CustomAttribute, bool> Sets(AttributeArgumentReader.NamedArgument member) => attribute =>
        NamedArgumentsOf(attribute).Set.Contains(member)
        && ReachOf(attribute, member.Name).Reach == Reach.Sets;

    /// <summary>
    /// What carries the attributes whose bytes did not decide whether they set
    /// <paramref name="member"/>, since they can be read with more than one width for the
    /// values of an enum (see <see cref="AttributeArgumentReader"/>).
    /// </summary>
    public IReadOnlyList<AttributeCarriers.Undecided> UndecidedOn(AttributeArgumentReader.NamedArgument member) =>
        AttributeCarriers.UndecidedOf(_module, WithNamedArguments()
            .Where(row => row.Named.MaySet.Contains(member) && ReachOf(row.Attribute, member.Name).Reach == Reach.Sets)
            .Select(row => new AttributeArgumentReader.UndecidedAttribute(
                row.Attribute.Parent, row.Attribute.Constructor, row.Attribute.Value, row.Named.Enum))
            .ToList());

    /// <summary>
    /// Every field and property of what the scan is asked about that an attribute sets, or may
    /// set, by name.
    /// </summary>
    public IReadOnlyList<AttributeArgumentReader.NamedArgument> Members() =>
        WithNamedArguments()
            .SelectMany(row => row.Named.Set.Concat(row.Named.MaySet)
                .Where(member => ReachOf(row.Attribute, member.Name).Reach == Reach.Sets))
            .Distinct()
            .ToList();

    /// <summary>
    /// The attribute classes that set a member named <paramref name="name"/> and whose walk met
    /// a class that could not be read, each as an answer prints a type, with that class.
    /// Whether such a class inherits the name from the class asked about is not known.
    /// </summary>
    public IReadOnlyList<(string Class, UnreadBase Unread)> NotRead(string name) =>
        WithNamedArguments()
            .Where(row => row.Named.Set.Concat(row.Named.MaySet).Any(member => member.Name == name))
            .Select(row => (Row: row, Reached: ReachOf(row.Attribute, name)))
            .Where(row => row.Reached.Reach == Reach.NotRead)
            .Select(row => (DisplayOf(ClassOf(row.Row.Attribute)), row.Reached.Unread))
            .Distinct()
            .ToList();

    private IEnumerable<(CustomAttribute Attribute, AttributeArgumentReader.NamedArguments Named)> WithNamedArguments()
    {
        foreach (var handle in _metadata.CustomAttributes)
        {
            var attribute = _metadata.GetCustomAttribute(handle);
            var named = NamedArgumentsOf(attribute);
            if (named.Set.Count > 0 || named.MaySet.Count > 0)
                yield return (attribute, named);
        }
    }

    private AttributeArgumentReader.NamedArguments NamedArgumentsOf(CustomAttribute attribute)
    {
        var key = (attribute.Constructor, attribute.Value);
        if (_named.TryGetValue(key, out var named))
            return named;
        try
        {
            named = _reader.NamedArgumentsOf(attribute);
        }
        // An attribute the reader cannot decode sets nothing it can name.
        catch (Exception e) when (e is BadImageFormatException or ArgumentException)
        {
            named = AttributeArgumentReader.NamedArguments.None;
        }
        return _named[key] = named;
    }

    private (Reach Reach, UnreadBase Unread) ReachOf(CustomAttribute attribute, string name)
    {
        var attributeClass = ClassOf(attribute);
        if (attributeClass.IsNil)
            return (Reach.DoesNot, null);
        if (!_reach.TryGetValue((attributeClass, name), out var known))
            _reach[(attributeClass, name)] = known = Walk(attributeClass, name);
        return known;
    }

    private (Reach Reach, UnreadBase Unread) Walk(EntityHandle attributeClass, string name)
    {
        (IMember Declared, bool Stopped, IType Unread, IType BelowUnread) found;
        try
        {
            found = TypeWalker.FindSetByName(_references.ResolveType(attributeClass, default), name, _isTheClass);
        }
        // A class the metadata reader cannot decode, or whose type arguments ILSpy's type
        // system rejects as inconsistent, sets nothing that can be named. Every question
        // about a member of another assembly's type walks every attribute's class, so one
        // such row would otherwise end them all.
        catch (Exception e) when (e is BadImageFormatException or ArgumentException)
        {
            return (Reach.DoesNot, null);
        }
        if (found.Stopped)
            return (Reach.Sets, null);
        if (found.Declared is not null)
            return (_isTheMember(found.Declared) ? Reach.Sets : Reach.DoesNot, null);
        if (found.Unread is null)
            return (Reach.DoesNot, null);
        // The attribute's class itself is named by a reference row of this assembly, a class
        // above it by one of the assembly that defines the class below it.
        return (Reach.NotRead, found.BelowUnread is not null
            ? UnreadBase.Of(found.BelowUnread, found.Unread)
            : new UnreadBase(ReferenceFormatter.FormatTypeName(found.Unread),
                ReferenceFormatter.FullTypeNameOf(found.Unread) is { } fullName
                    ? UnreadLibrary.Of(_references.Compilation, _module.MetadataFile, fullName)
                    : []));
    }

    /// <summary>
    /// The class an attribute is of, as the row of its constructor names it: a definition, a
    /// reference, or for a class with type parameters a construction of it.
    /// </summary>
    private EntityHandle ClassOf(CustomAttribute attribute)
    {
        EntityHandle declaring = attribute.Constructor.Kind switch
        {
            HandleKind.MethodDefinition =>
                _metadata.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType(),
            HandleKind.MemberReference =>
                _metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent,
            _ => default,
        };
        return declaring.Kind is HandleKind.TypeDefinition or HandleKind.TypeReference or HandleKind.TypeSpecification
            ? declaring
            : default;
    }

    private string DisplayOf(EntityHandle attributeClass)
    {
        var type = _module.ResolveType(attributeClass, default);
        return ReferenceFormatter.FormatTypeName((type as ParameterizedType)?.GenericType ?? type);
    }
}
