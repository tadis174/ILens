using System.Reflection.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using ICSharpCode.Decompiler.TypeSystem.Implementation;

namespace ILens;

/// <summary>
/// Which member implements which member of an interface, read in a type system that holds the
/// assembly's references, such as the decompiler's. ILens's own knows an interface of another
/// assembly by its name alone, one of the core library without its members, and no base class
/// another assembly defines. <see cref="Of"/> answers for a member and
/// <see cref="ImplementersOf"/> for a member of an interface. Both read the same bindings, so
/// each is the other's converse.
/// </summary>
/// <remarks>
/// <para>
/// A class that lists an interface binds each member of the interface, from the class itself
/// upward through its base classes, at the first class that has an implementation by a row or,
/// failing that, by name. A base class counts whether or not it has the interface:
/// <c>class Lister : PlainBase, IPart { }</c> binds <c>IPart.Get</c> to <c>PlainBase.Get</c>.
/// </para>
/// <para>
/// By a row: a method or accessor whose MethodImpl row names the member, whatever it is called.
/// C# names such a method after the interface, <c>IShape.Area</c>, and ILSpy's
/// <c>IsExplicitInterfaceImplementation</c> is true only for a name with a '.'. A compiler also
/// writes the row without that name: the C# compiler for the <c>MoveNext</c> of an iterator's
/// class, the VB compiler for a member that implements under a name of its own. A property or
/// an event is implemented through its accessors, by the property or event that owns the
/// implementing accessors. Where no property owns them, the methods stand for it: the F#
/// compiler writes an interface's property so, and the C# compiler adds such a method where a
/// class binds an interface's property to a property of another assembly's base class that is
/// not virtual.
/// </para>
/// <para>
/// By name: the public member of the member's kind, name, parameters, return type and
/// staticness. Return types are compared by reflection name, so a member returning
/// <c>dynamic</c> is not found to implement one returning <c>object</c>.
/// </para>
/// <para>
/// A class that has the interface only through a base class keeps the binding of the nearest
/// class above it that lists the interface. Where the bound member is virtual, the most
/// derived override on the way down to the class runs in its place and implements the member
/// as well; a member that only hides the bound one does not.
/// </para>
/// <para>
/// A member can be bound only by classes below its own, as that of a base class without the
/// interface is. It implements the interface member for objects of those classes alone, so its
/// <see cref="Found"/> names them. A class that lists an interface again below a class that
/// has it can bind a member differently than that class did, to a member that hides the bound
/// one; the C# compiler then adds a method to the class that implements by a row and passes
/// the call on, so that binding is read from the row.
/// </para>
/// </remarks>
public static class InterfaceImplementations
{
    /// <summary>
    /// How far up a chain of base classes or of overrides the search goes before it takes the
    /// chain for a cycle in malformed metadata.
    /// </summary>
    private const int MaxDepth = 64;

    /// <summary>
    /// One side of a binding: for <see cref="ImplementersOf"/> a member that implements, for
    /// <see cref="Of"/> an interface member that is implemented.
    /// </summary>
    /// <param name="BoundBy">
    /// The classes that list the interface and bind the two, when the implementing member's own
    /// class does not; none when it does.
    /// </param>
    public sealed record Found(IMember Member, IReadOnlyList<ITypeDefinition> BoundBy);

    /// <summary>
    /// The interface members <paramref name="member"/> implements, each as the class that binds
    /// it sees it: a member of a generic interface comes with the type arguments that class
    /// gives.
    /// </summary>
    public static IReadOnlyList<Found> Of(IMember member)
    {
        var results = new Results();
        foreach (var implemented in RowsOf(member))
            results.Add(implemented, binder: null);

        // Only a class or a struct binds interface members to its own. An interface's member
        // with the name of a base interface's member hides that one, and implements it only by
        // a row.
        if (member.DeclaringTypeDefinition is not { Kind: TypeKind.Class or TypeKind.Struct } declaring)
            return results.ToList();

        // In the view of the member's own class: the member is what the class binds an
        // interface member to, or overrides that member. A member the class binds by a row may
        // have any name, so the rows of what the member overrides are candidates beside the
        // interface members of its name and parameters.
        var bindings = new Bindings();
        var levels = Chain(declaring).Levels;
        var overridden = OverriddenChain(member);
        var candidates = overridden.Skip(1).SelectMany(RowsOf)
            .Concat(InheritanceHelper.GetBaseMembers(member, includeImplementedInterfaces: true)
                .Where(candidate => candidate.DeclaringTypeDefinition is { Kind: TypeKind.Interface }));
        foreach (var candidate in candidates)
        {
            var interfaceType = candidate.DeclaringType;
            if (levels.FirstOrDefault(level => Lists(level, interfaceType)) is not { } lister)
                continue;
            if (bindings.Bound(lister, interfaceType, candidate).Members
                .Any(bound => overridden.Any(own => Same(own, bound))))
                results.Add(candidate, binder: null);
        }

        // In the view of a class below that lists an interface itself: such a class binds by
        // name to the first public member upward, which can be this one.
        if (member.Accessibility == Accessibility.Public && declaring is { Kind: TypeKind.Class, IsSealed: false })
        {
            foreach (var type in declaring.ParentModule.TypeDefinitions)
            {
                if (type.Kind != TypeKind.Class || !type.DirectBaseTypes.Any(direct => direct.Kind == TypeKind.Interface))
                    continue;
                if (type.Equals(declaring) || !Chain(type).Levels.Any(level => declaring.Equals(level.GetDefinition())))
                    continue;

                foreach (var interfaceType in Listed(type))
                {
                    foreach (var candidate in NamedLike(interfaceType, member))
                    {
                        if (bindings.Bound(type, interfaceType, candidate).Members.Any(bound => Same(bound, member)))
                            results.Add(candidate, binder: type);
                    }
                }
            }
        }
        return results.ToList();
    }

    /// <summary>
    /// True if what <see cref="Of"/> answers for <paramref name="member"/> can depend on a base
    /// type of its class, so that one that was not read leaves the answer open. A class binds
    /// by name only to a public member, and a member that overrides implements what the member
    /// it overrides is bound to. Any other member implements through its own rows alone, which
    /// name the interface member themselves; a row that names a member of a type that was not
    /// read leaves the answer open as well, since <see cref="RowsOf"/> takes such a member for
    /// an interface's only under the name C# gives an explicit implementation.
    /// </summary>
    /// <remarks>
    /// A static member counts among those that implement through their rows alone, whatever
    /// its accessibility: each of the 3,287 static members that implement in the core library
    /// of .NET 10.0.12 has a row of its own (counted 2026-10-13).
    /// </remarks>
    public static bool DependsOnBaseTypes(IMember member) =>
        member.ExplicitlyImplementedInterfaceMembers.Any(implemented => implemented.DeclaringTypeDefinition is null)
        || (member.DeclaringTypeDefinition is { Kind: TypeKind.Class or TypeKind.Struct }
            && member is not IMethod { IsConstructor: true }
            && ((member.Accessibility == Accessibility.Public && !member.IsStatic) || Overriding.Of(member)));

    /// <summary>
    /// The members that implement <paramref name="interfaceMember"/>, a method, an accessor, a
    /// property or an event of an interface, for a class of the interface's assembly: what each
    /// such class binds the member to, and the override that runs in its place. A bound member
    /// can belong to a class of another assembly. <c>Unread</c> holds the base classes that
    /// could not be read above a class for which no binding was found below them.
    /// </summary>
    public static (IReadOnlyList<Found> Members, IReadOnlyList<UnreadBase> Unread) ImplementersOf(IMember interfaceMember)
    {
        var asked = interfaceMember.MemberDefinition;
        var interfaceDefinition = asked.DeclaringTypeDefinition;
        var bindings = new Bindings();
        var results = new Results();
        var unread = new Dictionary<string, UnreadBase>(StringComparer.Ordinal);
        foreach (var type in interfaceDefinition.ParentModule.TypeDefinitions)
        {
            if (type.Equals(interfaceDefinition)
                || !type.GetAllBaseTypeDefinitions().Any(baseType => interfaceDefinition.Equals(baseType)))
                continue;

            // A derived interface implements a member of its base interface by a row alone. With
            // the same row it makes the member abstract again, which implements nothing.
            if (type.Kind == TypeKind.Interface)
            {
                foreach (var implementation in bindings.ByRow(type, interfaceType: null, asked))
                {
                    if (!implementation.IsAbstract)
                        results.Add(implementation.MemberDefinition, binder: null);
                }
                continue;
            }
            if (type.Kind is not (TypeKind.Class or TypeKind.Struct))
                continue;

            var levels = Chain(type).Levels;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            // Upward, so that for each form of the interface the nearest class that lists it
            // comes first.
            foreach (var lister in levels)
            {
                foreach (var interfaceType in Listed(lister))
                {
                    if (!interfaceDefinition.Equals(interfaceType.GetDefinition()) || !seen.Add(interfaceType.ReflectionName))
                        continue;
                    if (SeenBy(interfaceType, asked) is not { } member)
                        continue;

                    var binding = bindings.Bound(lister, interfaceType, member);
                    if (binding.Members.Count == 0 && binding.Unread is { } missing)
                        unread.TryAdd(missing.TypeName, missing);
                    foreach (var bound in binding.Members)
                    {
                        // The member is bound in its own class's view when that class is the
                        // one looked at and nothing below overrides it; any other class it is
                        // bound for names the class that lists the interface.
                        var runs = Runs(levels, bound);
                        if (runs is not null)
                            results.Add(runs.MemberDefinition, binder: null);
                        results.Add(bound.MemberDefinition,
                            binder: runs is null && type.Equals(bound.DeclaringTypeDefinition) ? null : lister.GetDefinition());
                    }
                }
            }
        }
        return (results.ToList(), unread.Values.ToList());
    }

    /// <summary>
    /// The interface members <paramref name="member"/> implements by its rows. A row can also
    /// name a class's member, for an override that returns a more specific type (see
    /// <see cref="Overriding"/>). An interface that was not loaded is taken for one only under
    /// the name C# gives an explicit implementation. A member with which an interface makes a
    /// member of its base interface abstract again has such a row and implements nothing.
    /// </summary>
    private static IEnumerable<IMember> RowsOf(IMember member) =>
        member is { IsAbstract: true, DeclaringTypeDefinition.Kind: TypeKind.Interface }
            ? []
            : member.ExplicitlyImplementedInterfaceMembers.Where(implemented =>
                implemented.DeclaringTypeDefinition is { Kind: TypeKind.Interface }
                || (implemented.DeclaringTypeDefinition is null && member.IsExplicitInterfaceImplementation));

    /// <summary>
    /// <paramref name="start"/> and its base classes, each as <paramref name="start"/> sees it,
    /// up to and including <c>System.Object</c>, whose members a class can bind as well. A base
    /// class the type system cannot read ends the chain and is returned as <c>Unread</c>;
    /// <c>System.Object</c> of the minimal core library, which has no members, ends it silently.
    /// </summary>
    private static (IReadOnlyList<IType> Levels, UnreadBase Unread) Chain(IType start)
    {
        var levels = new List<IType> { start };
        var current = start;
        while (levels.Count < MaxDepth && TypeWalker.BaseClassOf(current) is { } baseType)
        {
            if (baseType.GetDefinition()?.ParentModule is not MetadataModule)
                return (levels, baseType.FullName == "System.Object" ? null : UnreadBase.Of(current, baseType));
            levels.Add(baseType);
            current = baseType;
        }
        return (levels, null);
    }

    /// <summary>
    /// The interfaces <paramref name="type"/> lists, each as <paramref name="type"/> sees it:
    /// those its own rows name, and each base interface of those that its base class does not
    /// have. The runtime binds the members of such an interface anew, from the class upward.
    /// For an interface the class merely inherits from its base class, and for a base
    /// interface that its base class has already, the class keeps the base class's bindings.
    /// </summary>
    /// <remarks>
    /// The C# compiler writes a row for every base interface of an interface a class names, so
    /// in its assemblies a class that names an interface lists that interface's base
    /// interfaces as well. The VB compiler leaves the row out when the base class has the base
    /// interface: such a class keeps the base class's bindings, also where it declares a public
    /// member of an interface member's name.
    /// </remarks>
    private static IEnumerable<IType> Listed(IType type)
    {
        HashSet<string> rows = null;
        HashSet<string> ofBaseClass = null;
        foreach (var named in type.DirectBaseTypes)
        {
            if (named.Kind != TypeKind.Interface)
                continue;
            foreach (var listed in named.GetAllBaseTypes())
            {
                if (listed.Kind != TypeKind.Interface)
                    continue;
                var name = listed.ReflectionName;
                if (name != named.ReflectionName)
                {
                    rows ??= InterfaceNames(type.DirectBaseTypes);
                    if (!rows.Contains(name))
                    {
                        ofBaseClass ??= TypeWalker.BaseClassOf(type) is { } baseClass
                            ? InterfaceNames(baseClass.GetAllBaseTypes())
                            : new HashSet<string>();
                        if (ofBaseClass.Contains(name))
                            continue;
                    }
                }
                yield return listed;
            }
        }
    }

    private static HashSet<string> InterfaceNames(IEnumerable<IType> types) =>
        types.Where(type => type.Kind == TypeKind.Interface)
            .Select(type => type.ReflectionName)
            .ToHashSet(StringComparer.Ordinal);

    private static bool Lists(IType type, IType interfaceType) =>
        Listed(type).Any(listed => listed.ReflectionName == interfaceType.ReflectionName);

    /// <summary><paramref name="asked"/>, a member of an interface, as <paramref name="interfaceType"/> has it.</summary>
    private static IMember SeenBy(IType interfaceType, IMember asked) => asked is IMethod { IsAccessor: true }
        ? interfaceType.GetAccessors(accessor => Same(accessor, asked), GetMemberOptions.IgnoreInheritedMembers).FirstOrDefault()
        : interfaceType.GetMembers(member => Same(member, asked), GetMemberOptions.IgnoreInheritedMembers).FirstOrDefault();

    /// <summary>
    /// The members <paramref name="type"/> declares of the kind and name of <paramref name="like"/>.
    /// </summary>
    private static IEnumerable<IMember> NamedLike(IType type, IMember like) => like is IMethod { IsAccessor: true }
        ? type.GetAccessors(accessor => accessor.Name == like.Name, GetMemberOptions.IgnoreInheritedMembers)
        : type.GetMembers(member => member.Name == like.Name && member.SymbolKind == like.SymbolKind,
            GetMemberOptions.IgnoreInheritedMembers);

    /// <summary>
    /// The override of <paramref name="bound"/> that runs for an object of the class
    /// <paramref name="levels"/> start at: the first one upward from that class, below the
    /// class that declares <paramref name="bound"/>. Null when nothing overrides it there.
    /// </summary>
    private static IMember Runs(IReadOnlyList<IType> levels, IMember bound)
    {
        if (!(bound.IsVirtual || bound.IsAbstract || Overriding.Of(bound)))
            return null;
        foreach (var level in levels)
        {
            if (bound.DeclaringTypeDefinition.Equals(level.GetDefinition()))
                return null;
            if (NamedLike(level, bound).FirstOrDefault(candidate => Reaches(candidate, bound)) is { } runs)
                return runs;
        }
        return null;
    }

    /// <summary><paramref name="member"/> and the members it overrides, nearest first.</summary>
    private static IReadOnlyList<IMember> OverriddenChain(IMember member)
    {
        var chain = new List<IMember> { member };
        for (var current = Overriding.Overridden(member); current is not null && chain.Count < MaxDepth;
             current = Overriding.Overridden(current))
            chain.Add(current);
        return chain;
    }

    private static bool Reaches(IMember candidate, IMember target) =>
        OverriddenChain(candidate).Skip(1).Any(overridden => Same(overridden, target));

    /// <summary>
    /// True for one member seen twice, whatever type arguments each view gives it. By module
    /// and row, since a view with type arguments is another object than the definition.
    /// </summary>
    private static bool Same(IMember left, IMember right) => Row(left) == Row(right);

    private static (IModule Module, EntityHandle Token) Row(IMember member) =>
        (member.MemberDefinition.ParentModule, member.MemberDefinition.MetadataToken);

    /// <summary>
    /// True if a class may bind <paramref name="interfaceMember"/> to its member
    /// <paramref name="member"/> by name.
    /// </summary>
    private static bool Fits(IMember member, IMember interfaceMember) =>
        member.Accessibility == Accessibility.Public
        && member.IsStatic == interfaceMember.IsStatic
        && SignatureComparer.Ordinal.Equals(member, interfaceMember)
        && member.ReturnType.ReflectionName == interfaceMember.ReturnType.ReflectionName;

    /// <summary>What a class that lists an interface binds one member of it to.</summary>
    /// <param name="Members">
    /// The member, none when no class up the chain has one, and several only where methods
    /// without a property stand for a property's accessors.
    /// </param>
    /// <param name="Unread">The base class the search ended at without a member, if it could not be read.</param>
    private sealed record Binding(IReadOnlyList<IMember> Members, UnreadBase Unread);

    /// <summary>
    /// The bindings of one question, each worked out once: a question about a member of a much
    /// implemented interface asks the same class for every class below it.
    /// </summary>
    private sealed class Bindings
    {
        private readonly Dictionary<(IModule, string, string, EntityHandle), Binding> _bound = new();
        private readonly Dictionary<(IModule, string), IReadOnlyList<(IMethod Method, IMember Implemented)>> _rows = new();

        /// <summary>
        /// What <paramref name="lister"/>, a class that lists <paramref name="interfaceType"/>,
        /// binds <paramref name="interfaceMember"/> to.
        /// </summary>
        public Binding Bound(IType lister, IType interfaceType, IMember interfaceMember)
        {
            var key = (lister.GetDefinition()?.ParentModule, lister.ReflectionName, interfaceType.ReflectionName,
                interfaceMember.MemberDefinition.MetadataToken);
            if (_bound.TryGetValue(key, out var known))
                return known;

            var (levels, unread) = Chain(lister);
            foreach (var level in levels)
            {
                var byRow = ByRow(level, interfaceType, interfaceMember);
                if (byRow.Count > 0)
                    return _bound[key] = new Binding(byRow, null);
                if (ByName(level, interfaceMember) is { } named)
                    return _bound[key] = new Binding([named], null);
            }
            return _bound[key] = new Binding([], unread);
        }

        /// <summary>
        /// The members <paramref name="level"/> declares that implement
        /// <paramref name="interfaceMember"/> by a row, for <paramref name="interfaceType"/> or,
        /// when that is null, for the interface with any type arguments.
        /// </summary>
        public IReadOnlyList<IMember> ByRow(IType level, IType interfaceType, IMember interfaceMember)
        {
            var rows = RowsIn(level);
            if (rows.Count == 0)
                return [];

            IMember[] named = interfaceMember switch
            {
                IProperty property => [property.Getter, property.Setter],
                IEvent @event => [@event.AddAccessor, @event.RemoveAccessor, @event.InvokeAccessor],
                _ => [interfaceMember],
            };
            return rows
                .Where(row => named.Any(target => target is not null && Same(row.Implemented, target))
                    && (interfaceType is null || row.Implemented.DeclaringType.ReflectionName == interfaceType.ReflectionName))
                // A property or an event is asked about: the one that owns the accessor, and
                // the method itself where none does.
                .Select(row => interfaceMember is IMethod ? row.Method : (IMember)row.Method.AccessorOwner ?? row.Method)
                .DistinctBy(Row)
                .ToList();
        }

        /// <summary>
        /// The methods and accessors <paramref name="level"/> declares whose rows name a member
        /// of an interface, each with that member as <paramref name="level"/> sees it.
        /// </summary>
        private IReadOnlyList<(IMethod Method, IMember Implemented)> RowsIn(IType level)
        {
            var definition = level.GetDefinition();
            var key = (definition?.ParentModule, level.ReflectionName);
            if (_rows.TryGetValue(key, out var rows))
                return rows;

            // Most classes have no such row, and asking each of their methods for its rows
            // reads the class's rows once for every method.
            if (definition is not { ParentModule: MetadataModule module, MetadataToken: { IsNil: false } token }
                || module.MetadataFile.Metadata.GetTypeDefinition((TypeDefinitionHandle)token).GetMethodImplementations().Count == 0)
                return _rows[key] = [];

            return _rows[key] = level.GetMethods(options: GetMemberOptions.IgnoreInheritedMembers)
                .Concat(level.GetAccessors(options: GetMemberOptions.IgnoreInheritedMembers))
                .SelectMany(method => method.ExplicitlyImplementedInterfaceMembers.Select(implemented => (method, implemented)))
                .Where(row => row.implemented.DeclaringTypeDefinition is { Kind: TypeKind.Interface })
                .ToList();
        }

        /// <summary>
        /// The member <paramref name="level"/> declares that <paramref name="interfaceMember"/>
        /// is bound to by name, or null. An accessor is bound through its property or event.
        /// </summary>
        private static IMember ByName(IType level, IMember interfaceMember)
        {
            if (interfaceMember is IMethod { IsAccessor: true, AccessorOwner: { } owner } accessor)
            {
                return (ByName(level, owner), owner) switch
                {
                    (IProperty bound, IProperty asked) => Is(asked.Getter) ? bound.Getter : bound.Setter,
                    (IEvent bound, IEvent asked) => Is(asked.AddAccessor) ? bound.AddAccessor
                        : Is(asked.RemoveAccessor) ? bound.RemoveAccessor
                        : bound.InvokeAccessor,
                    _ => null,
                };

                bool Is(IMethod other) => other is not null && Same(accessor, other);
            }
            return NamedLike(level, interfaceMember).FirstOrDefault(candidate => Fits(candidate, interfaceMember));
        }
    }

    /// <summary>
    /// What a question found, each member once: without classes when its own class binds it,
    /// and otherwise with every class that does.
    /// </summary>
    private sealed class Results
    {
        private sealed class Entry(IMember member)
        {
            public IMember Member { get; } = member;
            public bool OwnClass { get; set; }
            public Dictionary<string, ITypeDefinition> Binders { get; } = new(StringComparer.Ordinal);
        }

        private readonly Dictionary<(IModule, EntityHandle, string), Entry> _entries = new();

        /// <param name="binder">The class that binds the member, or null for the member's own class.</param>
        public void Add(IMember member, ITypeDefinition binder)
        {
            // A member of an interface that was not loaded has no row to know it by, so two of
            // them are told apart by how they print.
            var definition = member.MemberDefinition;
            var key = (definition.ParentModule, definition.MetadataToken,
                definition.MetadataToken.IsNil ? ReferenceFormatter.FormatSymbol(member) : member.DeclaringType.ReflectionName);
            if (!_entries.TryGetValue(key, out var entry))
                _entries[key] = entry = new Entry(member);
            if (binder is null)
                entry.OwnClass = true;
            else
                entry.Binders.TryAdd(binder.ReflectionName, binder);
        }

        public IReadOnlyList<Found> ToList() => _entries.Values
            .Select(entry => new Found(entry.Member, entry.OwnClass ? [] : entry.Binders.Values.ToList()))
            .ToList();
    }
}
