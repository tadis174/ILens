using System.Collections.Immutable;
using System.Reflection.Metadata;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.Disassembler;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using ICSharpCode.Decompiler.TypeSystem.Implementation;

namespace ILens;

/// <summary>
/// Answers analyze questions about a type the inspected assembly uses but does not define,
/// from the assembly's own metadata: its references to the type and to the type's members,
/// the signatures and method bodies that name them, and its custom attributes. For a type the
/// assembly defines it answers who uses the type (<see cref="UsersOf"/>).
/// </summary>
/// <remarks>
/// <para>
/// The ILSpyX analyzers cannot answer for a type from another assembly. They decide whether a
/// reference names the analyzed type by resolving it to that type's definition, which ILens
/// never loads for such a type, so every comparison fails. This scan compares names instead,
/// and otherwise looks where the ILSpyX analyzer for the same kind looks, so a question gets
/// the same kind of answer whether or not the assembly defines the type.
/// </para>
/// <para>
/// Who uses a type the assembly defines is answered here as well, since the ILSpyX analyzer
/// for it reads every attribute of the assembly first, through the metadata library's decoder
/// and without catching that decoder's errors. The decoder fails on an attribute class with
/// type parameters whose constructor takes a later type parameter after a type argument that
/// is a class, a struct, an enum or a constructed type, so one such attribute would end the
/// answer for every type of its assembly. <see cref="AttributeArgumentReader"/> reads such an
/// attribute.
/// </para>
/// <para>
/// Not thread-safe: the caller holds the host's lock, which serializes it with the decompiler
/// and the analyzers.
/// </para>
/// </remarks>
public sealed class TypeUsageScanner
{
    private readonly MetadataFile _file;
    private readonly MetadataReader _metadata;
    private readonly MetadataModule _module;
    private readonly SymbolResolver.ExternalTypeName _target;

    /// <summary>The assembly's reference rows that name the type.</summary>
    private readonly HashSet<TypeReferenceHandle> _references;

    /// <summary>
    /// The type codes signatures write the type as, when it is a core type such as
    /// <c>System.String</c>, which a signature names without any reference row.
    /// </summary>
    private readonly HashSet<PrimitiveTypeCode> _primitives;

    /// <summary>The row that defines the type, or nil for a type the assembly does not define.</summary>
    private readonly TypeDefinitionHandle _definition;

    private readonly SignatureNames _signatures;
    private readonly AttributeArgumentReader _arguments;

    /// <summary>
    /// The inspected assembly's module in the decompiler's type system, which holds the
    /// assemblies the reference search found. Null for a type the assembly defines.
    /// </summary>
    private readonly MetadataModule _inReferences;
    private NamedArgumentScan _setByName;

    /// <summary>
    /// The bodies whose decompiled code shows the code of a body: the body itself, except for a
    /// method that only passes a call on, whose callers stand for it (<c>AssemblyHost.Proxies</c>).
    /// </summary>
    private readonly Func<MethodDefinitionHandle, IEnumerable<MethodDefinitionHandle>> _bodiesShowing;

    public TypeUsageScanner(MetadataFile file, MetadataModule module, SymbolResolver.ExternalTypeName target,
        MetadataModule inReferences, Func<MethodDefinitionHandle, IEnumerable<MethodDefinitionHandle>> bodiesShowing)
    {
        _file = file;
        _metadata = file.Metadata;
        _module = module;
        _target = target;
        _inReferences = inReferences;
        _bodiesShowing = bodiesShowing;
        _references = _metadata.TypeReferences
            .Where(handle => SymbolResolver.TryReadReference(_metadata, handle, out var name, out _)
                && target.Matches(name))
            .ToHashSet();
        _primitives = PrimitivesNamed(target);
        _signatures = new SignatureNames(_references, _primitives, _definition);
        _arguments = new AttributeArgumentReader(module,
            handle => handle.Kind == HandleKind.TypeReference && _references.Contains((TypeReferenceHandle)handle),
            type => Contains(type, topLevelOnly: false));
    }

    /// <summary>
    /// For a type the assembly defines, matched by the row that defines it. No reference row
    /// names such a type, so of the questions only <see cref="UsedBy"/> finds anything;
    /// <see cref="UsersOf"/> is the way in.
    /// </summary>
    private TypeUsageScanner(MetadataFile file, MetadataModule module, ITypeDefinition definition,
        Func<MethodDefinitionHandle, IEnumerable<MethodDefinitionHandle>> bodiesShowing)
    {
        _file = file;
        _metadata = file.Metadata;
        _module = module;
        _bodiesShowing = bodiesShowing;
        var name = definition.FullTypeName;
        _target = new SymbolResolver.ExternalTypeName(
            ReferenceFormatter.FormatTypeName(definition), candidate => candidate == name, [name], Origin: null);
        _references = [];
        _primitives = PrimitivesNamed(_target);
        _definition = (TypeDefinitionHandle)definition.MetadataToken;
        _signatures = new SignatureNames(_references, _primitives, _definition);
        _arguments = new AttributeArgumentReader(module, handle => handle == definition.MetadataToken,
            type =>
            {
                var finder = new DefinitionFinder(definition);
                type.AcceptVisitor(finder);
                return finder.Found;
            });
    }

    /// <summary>
    /// The type codes a signature writes a type of that name as: a core type such as
    /// <c>System.String</c> is named there without any reference or definition row.
    /// </summary>
    private static HashSet<PrimitiveTypeCode> PrimitivesNamed(SymbolResolver.ExternalTypeName target) =>
        Enum.GetValues<PrimitiveTypeCode>()
            .Where(code => target.Matches(new TopLevelTypeName("System", code.ToString())))
            .ToHashSet();

    /// <summary>
    /// Every entity that uses <paramref name="type"/>, a type <paramref name="module"/>
    /// defines, in the places <see cref="UsedBy"/> looks, and beside them what carries an
    /// attribute whose bytes did not decide whether it names the type. A use of a field or
    /// method the type declares is a use of the type. A member or nested type the type itself
    /// declares is left out of both, as it says nothing about who else uses the type. So is
    /// a class the compiler generated inside the type, with everything in it: the code of the
    /// type's lambdas, iterators and async methods lies there, and reported under the member
    /// it was written in, it would name the type's own member as a user.
    /// </summary>
    public static (IReadOnlyList<ISymbol> Users, IReadOnlyList<AttributeCarriers.Undecided> Undecided) UsersOf(
        MetadataFile file, MetadataModule module, ITypeDefinition type,
        Func<MethodDefinitionHandle, IEnumerable<MethodDefinitionHandle>> bodiesShowing)
    {
        var scan = new TypeUsageScanner(file, module, type, bodiesShowing);
        var users = scan.UsedBy().Where(NotItsOwn).ToList();
        var undecided = scan.UndecidedUses
            .Select(attributes => attributes with { Carriers = attributes.Carriers.Where(NotItsOwn).ToList() })
            .Where(attributes => attributes.Carriers.Count > 0)
            .ToList();
        return (users, undecided);

        // Walks outward from the user through the classes the compiler generated. One someone
        // wrote ends the walk: a nested class of the type is another class, and its members
        // are users.
        bool NotItsOwn(ISymbol user)
        {
            for (var declaring = (user as IEntity)?.DeclaringTypeDefinition; declaring is not null;
                 declaring = declaring.DeclaringTypeDefinition)
            {
                if (type.Equals(declaring))
                    return false;
                if (!CompilerGeneratedFilter.IsCompilerGenerated(declaring))
                    return true;
            }
            return true;
        }
    }

    /// <summary>True if the assembly has a reference row naming the type.</summary>
    public bool IsReferenced => _references.Count > 0;

    /// <summary>
    /// What carries the attributes the last <see cref="UsedBy"/> could not decide on: each may
    /// name the type.
    /// </summary>
    public IReadOnlyList<AttributeCarriers.Undecided> UndecidedUses { get; private set; } = [];

    // --- Type-level kinds ------------------------------------------------------

    /// <summary>
    /// Every entity that uses the type: an entity carrying an attribute of the type or naming
    /// it in an argument, a type deriving from it or implementing or constraining with it, a
    /// method naming it in its signature, constraints or body, a field, property or event of the
    /// type, and an enum whose underlying type it is. The places <c>TypeUsedByAnalyzer</c> looks.
    /// What carries an attribute whose bytes did not decide whether it names the type is left in
    /// <see cref="UndecidedUses"/>.
    /// </summary>
    public IReadOnlyList<ISymbol> UsedBy()
    {
        _arguments.Undecided.Clear();
        var results = AttributeCarriers.Of(_module, attribute =>
            Names(AttributeTypeOf(attribute)) || _arguments.NameTheType(attribute)).ToList();
        UndecidedUses = AttributeCarriers.UndecidedOf(_module, _arguments);

        foreach (var handle in _metadata.TypeDefinitions)
        {
            var type = _metadata.GetTypeDefinition(handle);
            if (Names(type.BaseType)
                || type.GetInterfaceImplementations().Any(i => Names(_metadata.GetInterfaceImplementation(i).Interface))
                || NamedInConstraints(type.GetGenericParameters()))
                results.Add(_module.GetDefinition(handle));
        }
        foreach (var handle in _metadata.MethodDefinitions)
        {
            var method = _metadata.GetMethodDefinition(handle);
            if (Safely(() => AnyIn(method.DecodeSignature(_signatures, null)))
                || NamedInConstraints(method.GetGenericParameters())
                || NamedInBody(method))
                results.AddRange(_bodiesShowing(handle).Select(body => SymbolOf(_module.GetDefinition(body))));
        }
        foreach (var handle in _metadata.FieldDefinitions)
        {
            if (!Safely(() => _metadata.GetFieldDefinition(handle).DecodeSignature(_signatures, null)))
                continue;
            // The field an enum keeps its value in names the enum's underlying type, which the
            // enum's declaration writes after its name, so the enum is the user.
            var field = _module.GetDefinition(handle);
            results.Add(CompilerGeneratedFilter.IsEnumValueField(field) ? field.DeclaringTypeDefinition : field);
        }
        foreach (var handle in _metadata.PropertyDefinitions)
        {
            if (Safely(() => AnyIn(_metadata.GetPropertyDefinition(handle).DecodeSignature(_signatures, null))))
                results.Add(_module.GetDefinition(handle));
        }
        foreach (var handle in _metadata.EventDefinitions)
        {
            if (Names(_metadata.GetEventDefinition(handle).Type))
                results.Add(_module.GetDefinition(handle));
        }
        return results;
    }

    /// <summary>
    /// Every method that creates an instance of the type: <c>newobj</c> on one of its
    /// constructors, <c>initobj</c> on the type, and a direct <c>call</c> to one of its
    /// constructors, which initializes a struct. As <c>TypeInstantiatedByAnalyzer</c> does
    /// for a struct; for a class, the only such call is a subclass constructor calling its
    /// base constructor, which creates nothing and is left out.
    /// </summary>
    public IReadOnlyList<ISymbol> InstantiatedBy()
    {
        var results = new List<ISymbol>();
        foreach (var handle in _metadata.MethodDefinitions)
        {
            var method = _metadata.GetMethodDefinition(handle);
            var chainsToBase = _metadata.StringComparer.Equals(method.Name, ".ctor")
                && IsTheType(_metadata.GetTypeDefinition(method.GetDeclaringType()).BaseType);
            var creates = AnyInBody(method,
                opCode => opCode is ILOpCode.Newobj or ILOpCode.Initobj || (opCode == ILOpCode.Call && !chainsToBase),
                (opCode, token) => opCode == ILOpCode.Initobj ? IsTheType(token) : IsConstructorOfTheType(token));
            if (creates)
                results.Add(SymbolOf(_module.GetDefinition(handle)));
        }
        return results;
    }

    /// <summary>
    /// Every visible field, property, event and method whose signature exposes the type: its
    /// own type, return type or a parameter is the type, an array of it or a reference to it.
    /// A type argument does not count, and a private member exposes nothing unless it is an
    /// explicit interface implementation, as in <c>TypeExposedByAnalyzer</c>.
    /// </summary>
    public IReadOnlyList<ISymbol> ExposedBy()
    {
        var results = new List<ISymbol>();
        foreach (var type in _module.TypeDefinitions)
        {
            results.AddRange(type.Fields.Where(f => f.Accessibility != Accessibility.Private && Exposes(f.ReturnType)));
            results.AddRange(type.Properties.Where(p => Visible(p) && (Exposes(p.ReturnType) || p.Parameters.Any(q => Exposes(q.Type)))));
            results.AddRange(type.Events.Where(e => Visible(e) && Exposes(e.ReturnType)));
            results.AddRange(type.DeclaredMethods().Where(m => Visible(m) && (Exposes(m.ReturnType) || m.Parameters.Any(q => Exposes(q.Type)))));
        }
        return results;

        static bool Visible(IMember member) =>
            member.Accessibility != Accessibility.Private || member.IsExplicitInterfaceImplementation;
        bool Exposes(IType type) => Contains(type, topLevelOnly: true);
    }

    /// <summary>
    /// Every one of <paramref name="extensionMethods"/>, the rows of the assembly's extension
    /// methods (<see cref="SymbolResolver.ExtensionMethods"/>), whose receiver is the type
    /// itself, or a construction of it for a generic type, passed by value or by reference, or a
    /// type parameter whose constraints name it (<see cref="TypeWalker.ListedUnder"/>). An
    /// extension method on one of the type's base types is not listed, as for a type the
    /// assembly defines (<see cref="AssemblyHost.FindExtensionMethods"/>).
    /// </summary>
    public IReadOnlyList<ISymbol> ExtensionMethods(IEnumerable<MethodDefinitionHandle> extensionMethods) =>
        extensionMethods
            .Select(handle => _module.GetDefinition(handle))
            .Where(method => TypeWalker.ListedUnder(method).Any(IsTheType))
            .ToList<ISymbol>();

    /// <summary>
    /// Every entity the attribute is applied to. An attribute the compiler stores as a flag
    /// rather than an attribute row, such as <c>[Serializable]</c>, is read from the flags,
    /// as <c>AttributeAppliedToAnalyzer</c> does.
    /// </summary>
    public IReadOnlyList<ISymbol> AppliedTo()
    {
        var builtin = Enum.GetValues<KnownAttribute>().FirstOrDefault(known =>
            known != KnownAttribute.None && !known.IsCustomAttribute()
            && _target.Matches(KnownAttributes.GetTypeName(known)));
        return builtin == KnownAttribute.None
            ? AttributeCarriers.Of(_module, attribute => IsTheType(AttributeTypeOf(attribute))).ToList()
            : AppliedAsFlag(builtin).ToList();
    }

    /// <summary>
    /// Every type implementing the interface: naming it among its interfaces, or implementing
    /// it through one of the assembly's own base types or interfaces. A type implementing it
    /// through a base class from another assembly is not found: the assembly's metadata does
    /// not show that class's interfaces. Throws when the assembly derives a class from the type,
    /// since that shows it is not an interface.
    /// </summary>
    public IReadOnlyList<ISymbol> ImplementedBy()
    {
        foreach (var handle in _metadata.TypeDefinitions)
        {
            if (IsTheType(_metadata.GetTypeDefinition(handle).BaseType))
                throw new ArgumentException(
                    $"Analysis kind 'implemented_by' on a type requires an interface; {_target.Display} " +
                    $"is a class, since {ReferenceFormatter.FormatTypeName(_module.GetDefinition(handle))} derives from it.");
        }

        var known = new Dictionary<TypeDefinitionHandle, bool>();
        return _metadata.TypeDefinitions
            .Where(handle => Implements(handle, known))
            .Select(handle => (ISymbol)_module.GetDefinition(handle))
            .ToList();
    }

    private bool Implements(TypeDefinitionHandle handle, Dictionary<TypeDefinitionHandle, bool> known)
    {
        if (known.TryGetValue(handle, out var result))
            return result;
        // Provisionally false, so a cycle in malformed metadata ends here.
        known[handle] = false;
        var type = _metadata.GetTypeDefinition(handle);
        result = type.GetInterfaceImplementations()
                .Select(i => _metadata.GetInterfaceImplementation(i).Interface)
                .Any(i => IsTheType(i) || (DefinitionOf(i) is { } local && Implements(local, known)))
            || (DefinitionOf(type.BaseType) is { } baseType && Implements(baseType, known));
        known[handle] = result;
        return result;
    }

    // --- Member-level kinds ----------------------------------------------------

    /// <summary>
    /// One member of the type as the assembly's references show it: the rows with one name
    /// and one signature. A member of a generic type is one member whatever its type
    /// arguments, and its parameter types write the type's own type parameters as
    /// <c>!0</c>, <c>!1</c>, since their names are in the other assembly.
    /// </summary>
    /// <param name="Called">
    /// For a method, what the decompiler's type system makes of its first row: the method's
    /// declaration where the reference search found its library, and otherwise a stand-in
    /// that knows the row's signature alone. Null for a field and for a row that cannot be
    /// read.
    /// </param>
    /// <param name="TypeParameterCount">
    /// For a generic method, the count of its own type parameters, which its signature gives;
    /// 0 for any other member.
    /// </param>
    /// <param name="ReturnType">
    /// For a method, the return type its signature gives; null for a field and for a row that
    /// cannot be read.
    /// </param>
    public sealed record ReferencedMember(
        string Name, bool IsField, IReadOnlyList<IType> ParameterTypes, IReadOnlyList<MemberReferenceHandle> References,
        IMethod Called, int TypeParameterCount = 0, IType ReturnType = null);

    /// <summary>
    /// The members of the type the assembly references, by name and signature. A reference
    /// row the metadata reader cannot decode is skipped, as everywhere else in the scan.
    /// </summary>
    public IReadOnlyList<ReferencedMember> ReferencedMembers()
    {
        var groups = new Dictionary<(string Name, string Signature), (bool IsField, List<MemberReferenceHandle> Rows)>();
        foreach (var handle in _metadata.MemberReferences)
        {
            try
            {
                var reference = _metadata.GetMemberReference(handle);
                if (!IsTheType(reference.Parent))
                    continue;
                var key = (_metadata.GetString(reference.Name),
                    Convert.ToBase64String(_metadata.GetBlobBytes(reference.Signature)));
                if (!groups.TryGetValue(key, out var group))
                    groups[key] = group = (reference.GetKind() == MemberReferenceKind.Field, []);
                group.Rows.Add(handle);
            }
            catch (BadImageFormatException)
            {
            }
        }

        var types = new SignatureTypes(_module);
        return groups.Select(group =>
        {
            IReadOnlyList<IType> parameters = [];
            IMethod called = null;
            IType returnType = null;
            var typeParameterCount = 0;
            if (!group.Value.IsField)
            {
                try
                {
                    var signature = _metadata.GetMemberReference(group.Value.Rows[0]).DecodeMethodSignature(types, null);
                    parameters = signature.ParameterTypes;
                    returnType = signature.ReturnType;
                    typeParameterCount = signature.GenericParameterCount;
                    called = _inReferences?.ResolveMethod(group.Value.Rows[0], default);
                }
                catch (Exception e) when (e is BadImageFormatException or ArgumentException)
                {
                    // Printed without parameters; still reachable by name.
                }
            }
            return new ReferencedMember(group.Key.Name, group.Value.IsField, parameters, group.Value.Rows, called,
                typeParameterCount, returnType);
        }).ToList();
    }

    /// <summary>
    /// Every method whose code names one of <paramref name="methods"/>, in an instruction
    /// <see cref="AssemblyHost.NamesAMethod"/> accepts, as for a method the assembly defines.
    /// </summary>
    public IReadOnlyList<ISymbol> CallersOf(IEnumerable<ReferencedMember> methods)
    {
        var rows = methods.SelectMany(m => m.References).ToHashSet();
        return MethodsWhoseBody(AssemblyHost.NamesAMethod, (_, token) => RefersTo(token, rows));
    }

    /// <summary>
    /// What carries the attributes applied through one of <paramref name="constructors"/>.
    /// Applying an attribute uses its constructor, and the attribute names it by the reference
    /// row itself, so no method body shows that use.
    /// </summary>
    public IReadOnlyList<ISymbol> CarriersOf(IEnumerable<ReferencedMember> constructors)
    {
        var rows = constructors.SelectMany(c => c.References).ToHashSet();
        return AttributeCarriers.Of(_module, attribute =>
            attribute.Constructor.Kind == HandleKind.MemberReference
            && rows.Contains((MemberReferenceHandle)attribute.Constructor)).ToList();
    }

    /// <summary>
    /// The scan for the attributes that set a member of the type by name, as in
    /// <c>[Label(Text = "x")]</c>. Such an attribute names the member in its bytes alone, so
    /// no reference row shows that the assembly uses it.
    /// </summary>
    public NamedArgumentScan SetByName => _setByName ??= NamedArgumentScan.ForClass(_module, _inReferences, IsTheType);

    /// <summary>
    /// What carries the attributes that set <paramref name="member"/> of the type by name.
    /// </summary>
    public IReadOnlyList<ISymbol> CarriersOf(AttributeArgumentReader.NamedArgument member) =>
        AttributeCarriers.Of(_module, SetByName.Sets(member)).ToList();

    /// <summary>
    /// Every method that reads, or that writes, one of <paramref name="fields"/>, found by
    /// <see cref="FieldAccessScanner"/> as for a field the assembly defines.
    /// </summary>
    public IReadOnlyList<FieldAccessScanner.Access> AccessesOf(IEnumerable<ReferencedMember> fields,
        FieldAccessScanner.AccessKind kind)
    {
        var rows = fields.SelectMany(f => f.References).ToHashSet();
        return new FieldAccessScanner(_file, _module,
                token => token.Kind == HandleKind.MemberReference && rows.Contains((MemberReferenceHandle)token))
            .Find(kind);
    }

    private bool RefersTo(EntityHandle token, HashSet<MemberReferenceHandle> rows) => token.Kind switch
    {
        HandleKind.MemberReference => rows.Contains((MemberReferenceHandle)token),
        HandleKind.MethodSpecification =>
            RefersTo(_metadata.GetMethodSpecification((MethodSpecificationHandle)token).Method, rows),
        _ => false,
    };

    // --- Matching ----------------------------------------------------------------

    /// <summary>True if the type handle names the type anywhere, a type argument included.</summary>
    private bool Names(EntityHandle handle) => !handle.IsNil && Safely(() => _signatures.Names(_metadata, handle));

    /// <summary>
    /// True if the type handle is the type itself, or a construction of it for a generic
    /// type, rather than merely naming it as a type argument.
    /// </summary>
    private bool IsTheType(EntityHandle handle)
    {
        if (handle.IsNil)
            return false;
        if (handle.Kind == HandleKind.TypeReference)
            return _references.Contains((TypeReferenceHandle)handle);
        return handle.Kind == HandleKind.TypeSpecification
            && GenericTypeOf((TypeSpecificationHandle)handle) is { Kind: HandleKind.TypeReference } generic
            && _references.Contains((TypeReferenceHandle)generic);
    }

    private bool IsTheType(IType type)
    {
        if (type is ParameterizedType constructed)
            type = constructed.GenericType;
        return ReferenceFormatter.FullTypeNameOf(type) is { } name && _target.Matches(name);
    }

    private bool IsConstructorOfTheType(EntityHandle token)
    {
        if (token.Kind != HandleKind.MemberReference)
            return false;
        var reference = _metadata.GetMemberReference((MemberReferenceHandle)token);
        return _metadata.StringComparer.Equals(reference.Name, ".ctor") && IsTheType(reference.Parent);
    }

    /// <summary>The generic type a type specification constructs, or nil for any other shape.</summary>
    private EntityHandle GenericTypeOf(TypeSpecificationHandle handle)
    {
        try
        {
            var blob = _metadata.GetBlobReader(_metadata.GetTypeSpecification(handle).Signature);
            if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance)
                return default;
            blob.ReadSignatureTypeCode();
            return blob.ReadTypeHandle();
        }
        catch (BadImageFormatException)
        {
            return default;
        }
    }

    /// <summary>The assembly's own type a handle names, directly or as a generic construction.</summary>
    private TypeDefinitionHandle? DefinitionOf(EntityHandle handle) => handle.Kind switch
    {
        HandleKind.TypeDefinition when !handle.IsNil => (TypeDefinitionHandle)handle,
        HandleKind.TypeSpecification when GenericTypeOf((TypeSpecificationHandle)handle) is
            { Kind: HandleKind.TypeDefinition, IsNil: false } generic => (TypeDefinitionHandle)generic,
        _ => null,
    };

    /// <summary>
    /// True if <paramref name="type"/> contains the type. With <paramref name="topLevelOnly"/>
    /// the arguments of a constructed type are not looked into.
    /// </summary>
    private bool Contains(IType type, bool topLevelOnly)
    {
        var finder = new TypeFinder(_target.Matches, topLevelOnly);
        type.AcceptVisitor(finder);
        return finder.Found;
    }

    private bool NamedInConstraints(GenericParameterHandleCollection parameters) =>
        parameters.Any(parameter => _metadata.GetGenericParameter(parameter).GetConstraints()
            .Any(constraint => Names(_metadata.GetGenericParameterConstraint(constraint).Type)));

    private bool NamedInBody(MethodDefinition method)
    {
        if (method.RelativeVirtualAddress == 0)
            return false;
        try
        {
            var body = _file.GetMethodBody(method.RelativeVirtualAddress);
            if (!body.LocalSignature.IsNil && Safely(() => NamedInSignature(body.LocalSignature)))
                return true;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
        return AnyInBody(method,
            opCode => opCode.GetOperandType() is OperandType.Field or OperandType.Method
                or OperandType.Sig or OperandType.Tok or OperandType.Type,
            (_, token) => NamedInToken(token));
    }

    /// <summary>True if a token in a method body names the type: the places <c>TypeUsedByAnalyzer</c> looks.</summary>
    private bool NamedInToken(EntityHandle token)
    {
        if (token.IsNil)
            return false;
        switch (token.Kind)
        {
            case HandleKind.TypeReference:
            case HandleKind.TypeSpecification:
            case HandleKind.TypeDefinition:
                return Names(token);
            case HandleKind.FieldDefinition:
                var field = _metadata.GetFieldDefinition((FieldDefinitionHandle)token);
                return Defines(field.GetDeclaringType()) || field.DecodeSignature(_signatures, null);
            case HandleKind.MethodDefinition:
                var method = _metadata.GetMethodDefinition((MethodDefinitionHandle)token);
                return Defines(method.GetDeclaringType()) || AnyIn(method.DecodeSignature(_signatures, null));
            case HandleKind.MemberReference:
                var reference = _metadata.GetMemberReference((MemberReferenceHandle)token);
                // The parent is a type, or for a call that passes variable arguments the
                // method the assembly defines.
                return NamedInToken(reference.Parent) || reference.GetKind() switch
                {
                    MemberReferenceKind.Method => AnyIn(reference.DecodeMethodSignature(_signatures, null)),
                    MemberReferenceKind.Field => reference.DecodeFieldSignature(_signatures, null),
                    _ => false,
                };
            case HandleKind.MethodSpecification:
                var specification = _metadata.GetMethodSpecification((MethodSpecificationHandle)token);
                return NamedInToken(specification.Method)
                    || specification.DecodeSignature(_signatures, null).Any(found => found);
            case HandleKind.StandaloneSignature:
                return NamedInSignature((StandaloneSignatureHandle)token);
            default:
                return false;
        }
    }

    /// <summary>
    /// True if <paramref name="handle"/> is the row that defines the type, so a member it
    /// declares is the type's own. Never true for a type the assembly does not define.
    /// </summary>
    private bool Defines(TypeDefinitionHandle handle) => !_definition.IsNil && handle == _definition;

    private bool NamedInSignature(StandaloneSignatureHandle handle)
    {
        var signature = _metadata.GetStandaloneSignature(handle);
        return signature.GetKind() switch
        {
            StandaloneSignatureKind.Method => AnyIn(signature.DecodeMethodSignature(_signatures, null)),
            StandaloneSignatureKind.LocalVariables => signature.DecodeLocalSignature(_signatures, null).Any(found => found),
            _ => false,
        };
    }

    private static bool AnyIn(MethodSignature<bool> signature) =>
        signature.ReturnType || signature.ParameterTypes.Any(found => found);

    /// <summary>
    /// True if some instruction in the method's body whose opcode passes
    /// <paramref name="wanted"/> has a token satisfying <paramref name="matches"/>. What the
    /// metadata reader cannot decode matches nothing: a token, or from there on, the body.
    /// </summary>
    private bool AnyInBody(MethodDefinition method, Func<ILOpCode, bool> wanted,
        Func<ILOpCode, EntityHandle, bool> matches)
    {
        if (method.RelativeVirtualAddress == 0)
            return false;
        try
        {
            var reader = _file.GetMethodBody(method.RelativeVirtualAddress).GetILReader();
            while (reader.RemainingBytes > 0)
            {
                var opCode = reader.DecodeOpCode();
                if (!wanted(opCode))
                {
                    reader.SkipOperand(opCode);
                    continue;
                }
                var token = MetadataTokenHelpers.EntityHandleOrNil(reader.ReadInt32());
                if (Matches(opCode, token))
                    return true;
            }
        }
        catch (BadImageFormatException)
        {
        }
        return false;

        bool Matches(ILOpCode opCode, EntityHandle token)
        {
            try
            {
                return matches(opCode, token);
            }
            catch (BadImageFormatException)
            {
                return false;
            }
        }
    }

    private IReadOnlyList<ISymbol> MethodsWhoseBody(Func<ILOpCode, bool> wanted,
        Func<ILOpCode, EntityHandle, bool> matches) =>
        _metadata.MethodDefinitions
            .Where(handle => AnyInBody(_metadata.GetMethodDefinition(handle), wanted, matches))
            .SelectMany(_bodiesShowing)
            .Select(handle => SymbolOf(_module.GetDefinition(handle)))
            .ToList();

    /// <summary>A method as the analyzers report it: an accessor as its property or event.</summary>
    private static ISymbol SymbolOf(IMethod method) => (ISymbol)method.AccessorOwner ?? method;

    /// <summary>
    /// Runs a check that decodes metadata, answering false for metadata the reader cannot
    /// decode, or whose type arguments ILSpy's type system rejects as inconsistent.
    /// </summary>
    private static bool Safely(Func<bool> check)
    {
        try
        {
            return check();
        }
        catch (Exception e) when (e is BadImageFormatException or ArgumentException)
        {
            return false;
        }
    }

    // --- Attributes ----------------------------------------------------------------

    private EntityHandle AttributeTypeOf(CustomAttribute attribute) => attribute.Constructor.Kind switch
    {
        HandleKind.MemberReference => _metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent,
        HandleKind.MethodDefinition => _metadata.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType(),
        _ => default,
    };

    /// <summary>
    /// The entities carrying an attribute the compiler stores as a flag, looked for on the
    /// kinds of entity <c>AttributeAppliedToAnalyzer</c> looks at for it.
    /// </summary>
    /// <remarks>
    /// ILSpy turns each such flag into an attribute of the entity, but its own
    /// <c>HasAttribute</c> then recognizes the attribute only through the attribute type's
    /// definition. The core library ILens loads defines none of these types, so it answers
    /// false for all of them. The attribute ILSpy builds from the flag still carries the
    /// type's name, so it is recognized by that, and ILSpy's rules for which flags make which
    /// attribute still apply.
    /// </remarks>
    private IEnumerable<ISymbol> AppliedAsFlag(KnownAttribute attribute)
    {
        var types = _module.TypeDefinitions.ToList();
        IEnumerable<ISymbol> Methods() => types.SelectMany(t => t.DeclaredMethods())
            .Where(m => Carries(m.GetAttributes())).Select(SymbolOf);
        IEnumerable<ISymbol> Parameters() => types.SelectMany(t => t.DeclaredMethods())
            .Where(m => m.Parameters.Any(p => Carries(p.GetAttributes()))).Select(SymbolOf);
        IEnumerable<ISymbol> Fields() => types.SelectMany(t => t.Fields).Where(f => Carries(f.GetAttributes()));

        return attribute switch
        {
            KnownAttribute.Serializable or KnownAttribute.ComImport or KnownAttribute.StructLayout =>
                types.Where(t => Carries(t.GetAttributes())),
            KnownAttribute.DllImport or KnownAttribute.PreserveSig or KnownAttribute.MethodImpl => Methods(),
            KnownAttribute.FieldOffset or KnownAttribute.NonSerialized => Fields(),
            KnownAttribute.MarshalAs => Fields().Concat(Parameters()),
            KnownAttribute.In or KnownAttribute.Out or KnownAttribute.Optional => Parameters(),
            KnownAttribute.IndexerName => types.SelectMany(t => t.Properties).Where(p => Carries(p.GetAttributes())),
            _ => [],
        };

        bool Carries(IEnumerable<IAttribute> attributes) => attributes.Any(a => IsTheType(a.AttributeType));
    }

    // --- Decoders --------------------------------------------------------------------

    /// <summary>
    /// Decodes a signature to whether it names the type, by the rows and type codes that denote
    /// it: its reference rows for a type from another assembly, and its definition row for a
    /// type the assembly defines. The counterpart of ILSpy's <c>FindTypeDecoder</c>, which
    /// matches a reference by resolving it to a definition.
    /// </summary>
    private sealed class SignatureNames : ISignatureTypeProvider<bool, object>
    {
        private readonly HashSet<TypeReferenceHandle> _references;
        private readonly HashSet<PrimitiveTypeCode> _primitives;
        private readonly TypeDefinitionHandle _definition;

        /// <param name="definition">Nil for a type the assembly does not define.</param>
        public SignatureNames(HashSet<TypeReferenceHandle> references, HashSet<PrimitiveTypeCode> primitives,
            TypeDefinitionHandle definition)
        {
            _references = references;
            _primitives = primitives;
            _definition = definition;
        }

        public bool Names(MetadataReader reader, EntityHandle handle) => handle.Kind switch
        {
            HandleKind.TypeReference => _references.Contains((TypeReferenceHandle)handle),
            HandleKind.TypeDefinition => GetTypeFromDefinition(reader, (TypeDefinitionHandle)handle, 0),
            HandleKind.TypeSpecification => GetTypeFromSpecification(reader, null, (TypeSpecificationHandle)handle, 0),
            _ => false,
        };

        public bool GetPrimitiveType(PrimitiveTypeCode typeCode) => _primitives.Contains(typeCode);
        public bool GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
            !_definition.IsNil && handle == _definition;
        public bool GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
            _references.Contains(handle);
        public bool GetTypeFromSpecification(MetadataReader reader, object genericContext,
            TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
        public bool GetGenericInstantiation(bool genericType, ImmutableArray<bool> typeArguments) =>
            genericType || typeArguments.Any(found => found);
        public bool GetArrayType(bool elementType, ArrayShape shape) => elementType;
        public bool GetSZArrayType(bool elementType) => elementType;
        public bool GetByReferenceType(bool elementType) => elementType;
        public bool GetPointerType(bool elementType) => elementType;
        public bool GetPinnedType(bool elementType) => elementType;
        public bool GetModifiedType(bool modifier, bool unmodifiedType, bool isRequired) => unmodifiedType || modifier;
        public bool GetFunctionPointerType(MethodSignature<bool> signature) => AnyIn(signature);
        public bool GetGenericTypeParameter(object genericContext, int index) => false;
        public bool GetGenericMethodParameter(object genericContext, int index) => false;
    }

    /// <summary>
    /// Decodes a member signature to ILSpy types, for printing a referenced member and
    /// matching overload hints against it. Unlike ILSpy's own resolution of a member
    /// reference, it does not substitute a generic type's arguments into the signature, so
    /// the rows of one member of a generic type read alike whatever their type arguments.
    /// </summary>
    private sealed class SignatureTypes : ISignatureTypeProvider<IType, object>
    {
        private readonly MetadataModule _module;

        public SignatureTypes(MetadataModule module) => _module = module;

        public IType GetPrimitiveType(PrimitiveTypeCode typeCode) =>
            Enum.TryParse<KnownTypeCode>(typeCode.ToString(), out var known)
                ? _module.Compilation.FindType(known)
                : SpecialType.UnknownType;
        public IType GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
            _module.GetDefinition(handle);
        public IType GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
            _module.ResolveType(handle, default);
        public IType GetTypeFromSpecification(MetadataReader reader, object genericContext,
            TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
        public IType GetGenericInstantiation(IType genericType, ImmutableArray<IType> typeArguments) =>
            new ParameterizedType(genericType, typeArguments);
        public IType GetArrayType(IType elementType, ArrayShape shape) =>
            new ArrayType(_module.Compilation, elementType, shape.Rank);
        public IType GetSZArrayType(IType elementType) => new ArrayType(_module.Compilation, elementType);
        public IType GetByReferenceType(IType elementType) => new ByReferenceType(elementType);
        public IType GetPointerType(IType elementType) => new PointerType(elementType);
        public IType GetPinnedType(IType elementType) => elementType;
        public IType GetModifiedType(IType modifier, IType unmodifiedType, bool isRequired) => unmodifiedType;
        public IType GetFunctionPointerType(MethodSignature<IType> signature) => SpecialType.UnknownType;
        public IType GetGenericTypeParameter(object genericContext, int index) =>
            DummyTypeParameter.GetClassTypeParameter(index);
        public IType GetGenericMethodParameter(object genericContext, int index) =>
            DummyTypeParameter.GetMethodTypeParameter(index);
    }

    /// <summary>
    /// Finds the type in an ILSpy type by name, where it is a definition from the core library
    /// ILens loads or an unknown type standing for one from another assembly.
    /// </summary>
    private sealed class TypeFinder : TypeVisitor
    {
        private readonly Func<FullTypeName, bool> _matches;
        private readonly bool _topLevelOnly;

        public TypeFinder(Func<FullTypeName, bool> matches, bool topLevelOnly)
        {
            _matches = matches;
            _topLevelOnly = topLevelOnly;
        }

        public bool Found { get; private set; }

        public override IType VisitTypeDefinition(ITypeDefinition type)
        {
            Found |= _matches(type.FullTypeName);
            return base.VisitTypeDefinition(type);
        }

        public override IType VisitOtherType(IType type)
        {
            if (type is UnknownType unknown)
                Found |= _matches(unknown.FullTypeName);
            return base.VisitOtherType(type);
        }

        public override IType VisitParameterizedType(ParameterizedType type) =>
            _topLevelOnly ? type.GenericType.AcceptVisitor(this) : base.VisitParameterizedType(type);
    }

    /// <summary>
    /// Finds a type the assembly defines in an ILSpy type, among its type arguments too, by
    /// the definition itself: a type of the same name from another assembly is not it.
    /// </summary>
    private sealed class DefinitionFinder : TypeVisitor
    {
        private readonly ITypeDefinition _wanted;

        public DefinitionFinder(ITypeDefinition wanted) => _wanted = wanted;

        public bool Found { get; private set; }

        public override IType VisitTypeDefinition(ITypeDefinition type)
        {
            Found |= _wanted.Equals(type);
            return base.VisitTypeDefinition(type);
        }
    }
}
