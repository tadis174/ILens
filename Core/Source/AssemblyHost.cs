using System;
using System.IO;
using System.Reflection.Metadata;
using System.Threading;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.OutputVisitor;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Transforms;
using ICSharpCode.Decompiler.Disassembler;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using ICSharpCode.ILSpyX;
using ICSharpCode.ILSpyX.Analyzers;

namespace ILens;

/// <summary>
/// Loads a .NET assembly and provides decompilation and analysis infrastructure.
/// One per loaded assembly; cached by <see cref="AssemblyHostRegistry"/>.
/// </summary>
public sealed class AssemblyHost : IDisposable
{
    private readonly object _decompilerLock = new();
    private readonly DecompilerSettings _settings = CreateSettings();
    private readonly CSharpDecompiler _decompiler;
    private CSharpDecompiler _positionalDecompiler;  // created on first use; guarded by _decompilerLock
    private CSharpDecompiler _fieldDecompiler;  // created on first use; guarded by _decompilerLock
    private readonly KeepOneField _keepOneField = new();  // _fieldDecompiler's pruning step
    private DecompilerTypeSystem _methodUseTypeSystem;  // FindUsedSymbols'; created on first use; guarded by _decompilerLock
    private readonly AssemblyList _assemblyList;
    private readonly LoadedAssembly _loadedAssembly;
    private readonly PEFile _peFile;
    private readonly UniversalAssemblyResolver _referenceSearch;
    private readonly PathGuard _guard;
    private readonly ReferenceResolver _references;
    private bool _disposed;  // guarded by _decompilerLock

    // What MethodUnit and MethodParts learn about a method or type, kept because every method
    // body names the same few thousand ones again; guarded by _decompilerLock.
    private readonly Dictionary<MethodDefinitionHandle, bool> _joinsUnit = new();
    private readonly Dictionary<MethodDefinitionHandle, bool> _localFunctions = new();
    private readonly Dictionary<MethodDefinitionHandle, bool> _generatedMethods = new();
    private readonly Dictionary<TypeDefinitionHandle, bool> _stateMachineClasses = new();
    private readonly Dictionary<TypeDefinitionHandle, ITypeDefinition> _classOwners = new();
    private readonly Dictionary<MemberReferenceHandle, MethodDefinitionHandle> _referencedMethods = new();
    private readonly Dictionary<TypeSpecificationHandle, TypeDefinitionHandle> _instantiatedTypes = new();

    // The methods that only pass a call on, with the code that calls each (see Proxies); read at
    // the first question that needs them; guarded by _decompilerLock.
    private Dictionary<MethodDefinitionHandle, Proxy> _proxies;

    /// <summary>
    /// Type system from the LoadedAssembly — symbols from this are compatible
    /// with the ILSpyX analyzer infrastructure.
    /// </summary>
    public ICompilation TypeSystem { get; }

    public string AssemblyPath { get; }

    /// <summary>
    /// Whether the assembly is a reference assembly: its row carries
    /// <c>System.Runtime.CompilerServices.ReferenceAssemblyAttribute</c>, which says that it holds
    /// a library's declarations without their code. Its methods have no body, an empty one or one
    /// that throws null, by the tool that wrote it, so no body of it says what the library does.
    /// The mark is read as ILSpy's <c>IsReferenceAssembly</c> reads it, but the file's path is not
    /// read, which that method also takes for one: under <c>Microsoft.NET\Framework</c> or
    /// <c>dotnet\shared</c> lie the libraries the runtime runs.
    /// </summary>
    public bool IsReferenceAssembly { get; }

    /// <summary>
    /// Symbol resolver bound to this host's type system. Lazily constructed on
    /// first access. A race between concurrent readers costs at most a second
    /// resolver, which builds its own tables when asked.
    /// </summary>
    public SymbolResolver Resolver => _resolver ??=
        new SymbolResolver(TypeSystem, ReadExternalBases, FindExternalType, UnreadLibrariesOf, ReferencedMembersOf, FitsExtension,
            () => _decompiler.TypeSystem);
    private SymbolResolver _resolver;

    public AssemblyHost(string assemblyPath, PathGuard guard)
    {
        AssemblyPath = Path.GetFullPath(assemblyPath);
        if (!File.Exists(AssemblyPath))
            throw new FileNotFoundException($"Assembly not found: {AssemblyPath}.");

        // Set up ILSpyX's AssemblyList and LoadedAssembly
        var settingsProvider = new StubSettingsProvider();
        var manager = new AssemblyListManager(settingsProvider);
        _assemblyList = manager.LoadList("explorer");
        _loadedAssembly = _assemblyList.OpenAssembly(AssemblyPath);

        // Wait for the assembly to load and get its type system
        // (LoadedAssembly loads asynchronously)
        _ = _loadedAssembly.GetLoadResultAsync().GetAwaiter().GetResult();
        TypeSystem = _loadedAssembly.GetTypeSystemOrNull()
            ?? throw new InvalidOperationException(
                $"Failed to load type system from {AssemblyPath}.");

        // Build the decompiler separately (for C# output — needs full resolver).
        // _peFile is the assembly as ILSpyX loaded it, whose metadata and method bodies
        // the scanners read directly.
        _peFile = _loadedAssembly.GetMetadataFileOrNull() as PEFile
            ?? throw new InvalidOperationException(
                $"Failed to load PE file from {AssemblyPath}.");
        IsReferenceAssembly = CarriesReferenceMark(_peFile.Metadata);
        // The resolver looks for a reference by its file name in the assembly's own directory,
        // which its constructor adds, then at the top of each allow-root in the order given, and
        // then among the installed .NET libraries, which it asks first for a .NET Core or .NET
        // Standard target. A mod's game and engine assemblies often sit in an allow-root of their
        // own, and without them the decompiled code has gaps and numbers where enum names belong.
        // The search does not descend into a root: a tree holding several builds of one assembly,
        // such as a game's older versions, would leave the choice to the order of the walk.
        _referenceSearch = new UniversalAssemblyResolver(
            AssemblyPath,
            throwOnError: false,
            _peFile.DetectTargetFrameworkId());
        foreach (var root in guard.AllowedRoots)
            _referenceSearch.AddSearchDirectory(root);
        _guard = guard;
        _references = new ReferenceResolver(_referenceSearch, guard);

        _decompiler = new CSharpDecompiler(AssemblyPath, _references, _settings);

        // A constant or a default of this assembly that is a value of an enum another assembly
        // defines is written by the enum's member, read where the decompiler reads that assembly.
        ConstantFormatter.ReadEnumsThrough(TypeSystem.MainModule, name => FindExternalType([name]));
    }

    /// <summary>
    /// Whether the assembly row carries ReferenceAssemblyAttribute (<see cref="IsReferenceAssembly"/>).
    /// ILSpy's DetectTargetFrameworkId, which the constructor runs, reads the same rows and passes
    /// over one it cannot read, so a malformed attribute row does not end the load here either.
    /// </summary>
    private static bool CarriesReferenceMark(MetadataReader metadata)
    {
        if (!metadata.IsAssembly)
            return false;
        foreach (var handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
        {
            try
            {
                if (metadata.GetCustomAttribute(handle).GetAttributeType(metadata).GetFullTypeName(metadata).ReflectionName
                    == "System.Runtime.CompilerServices.ReferenceAssemblyAttribute")
                    return true;
            }
            catch (BadImageFormatException)
            {
            }
        }
        return false;
    }

    /// <summary>
    /// The error for a question that reads code, asked of a reference assembly
    /// (<see cref="IsReferenceAssembly"/>). Answered from its placeholders, such a question would
    /// say that nothing uses, calls or creates a member, as if that were a fact about the library.
    /// <paramref name="cannot"/> says what cannot be answered and <paramref name="instead"/> where
    /// to ask instead. The error does not name the file, which can share its name with the
    /// library's own build, as a publicized copy of an assembly does.
    /// </summary>
    public static ArgumentException ReferenceAssemblyError(string cannot, string instead) =>
        new("This assembly is a reference assembly, which holds a library's declarations without their code, " +
            $"so {cannot}; {instead}");

    private static DecompilerSettings CreateSettings() =>
        new(ICSharpCode.Decompiler.CSharp.LanguageVersion.CSharp11_0)
        {
            ThrowOnAssemblyResolveErrors = false
        };

    /// <summary>
    /// The base classes of <paramref name="type"/> that another assembly defines, read from the
    /// decompiler's type system, which holds every assembly the reference resolver found. See
    /// <see cref="TypeWalker.WalkExternalBaseTypes"/>. None for a type of another module.
    /// </summary>
    /// <remarks>
    /// Read without <c>_decompilerLock</c>: the type system builds what it is asked for lazily
    /// and publishes it through <c>LazyInit</c>, so reading it beside a decompilation is safe,
    /// and the caller's lease keeps the host from being disposed meanwhile.
    /// </remarks>
    public ExternalBases ReadExternalBases(ITypeDefinition type)
    {
        if (type.ParentModule != TypeSystem.MainModule)
            return ExternalBases.None;
        return TypeWalker.WalkExternalBaseTypes(DecompilerCounterpartOf(type));
    }

    /// <summary>
    /// Whether <paramref name="type"/>, a type of the inspected assembly, is an attribute class,
    /// read from the decompiler's type system, since a base class another assembly defines ends
    /// the walk in ILens's own. See <see cref="TypeWalker.DerivesFromAttribute"/>. Read without
    /// <c>_decompilerLock</c>, as <see cref="ReadExternalBases"/> is.
    /// </summary>
    public (bool Derives, UnreadBase Unread) DerivesFromAttribute(ITypeDefinition type) =>
        TypeWalker.DerivesFromAttribute(DecompilerCounterpartOf(type));

    /// <summary>
    /// The interface members that <paramref name="member"/>, a member of the inspected
    /// assembly, implements, by the rules of <see cref="InterfaceImplementations"/>, and, where
    /// a base type can decide the answer (<see cref="InterfaceImplementations.DependsOnBaseTypes"/>),
    /// the base types of its class that could not be read, whose members the answer therefore
    /// leaves out. Both are read from the decompiler's type system, since ILens's own knows the
    /// interfaces of other assemblies by name alone. Read without <c>_decompilerLock</c>, as
    /// <see cref="ReadExternalBases"/> is.
    /// </summary>
    public (IReadOnlyList<InterfaceImplementations.Found> Members, IReadOnlyList<UnreadBase> Unread)
        FindImplementedInterfaceMembers(IMember member)
    {
        var counterpart = (IMember)_decompiler.TypeSystem.MainModule.ResolveEntity(member.MetadataToken);
        // An explicit implementation's own row names what it implements, and a member no base
        // type can bind implements by its rows alone, so nothing that could not be read leaves
        // either answer incomplete.
        IReadOnlyList<UnreadBase> unread = counterpart.IsExplicitInterfaceImplementation
            || !InterfaceImplementations.DependsOnBaseTypes(counterpart)
            ? []
            : TypeWalker.UnreadBaseTypes(counterpart.DeclaringTypeDefinition);
        return (InterfaceImplementations.Of(counterpart), unread);
    }

    /// <summary>
    /// The members that implement <paramref name="interfaceMember"/>, a member of an interface
    /// the inspected assembly defines, for a class of this assembly, by the rules of
    /// <see cref="InterfaceImplementations"/>, and the base classes that could not be read
    /// where a class's implementation was not found below them. Read from the decompiler's
    /// type system, since a class can implement the member through a member it inherits from a
    /// class of another assembly. Read without <c>_decompilerLock</c>, as
    /// <see cref="ReadExternalBases"/> is.
    /// </summary>
    /// <remarks>
    /// ILSpyX's three "Implemented By" analyzers are not asked. They list every member of a
    /// type below the interface that has the asked member's name and parameters, whether or
    /// not the interface member is bound to it, leave out a member a class inherits from a
    /// base class without the interface, and look at no accessor.
    /// </remarks>
    public (IReadOnlyList<InterfaceImplementations.Found> Members, IReadOnlyList<UnreadBase> Unread)
        FindImplementations(IMember interfaceMember)
    {
        var counterpart = (IMember)_decompiler.TypeSystem.MainModule.ResolveEntity(interfaceMember.MetadataToken);
        return InterfaceImplementations.ImplementersOf(counterpart);
    }

    /// <summary>
    /// The libraries this assembly's references or its forward name for <paramref name="type"/>, a
    /// type of another assembly that was not read, each as the decompiler's type system holds it
    /// (<see cref="UnreadLibrary"/>). Read without <c>_decompilerLock</c>, as
    /// <see cref="ReadExternalBases"/> is.
    /// </summary>
    public IReadOnlyList<UnreadLibrary> UnreadLibrariesOf(FullTypeName type) =>
        UnreadLibrary.Of(_decompiler.TypeSystem, _peFile, type);

    /// <summary>
    /// A type another assembly defines, as the decompiler's type system reads it from the
    /// assemblies the reference resolver found: the first of <paramref name="typeNames"/> one of
    /// them defines, or null when none does. The stand-in core types the decompiler adds for a
    /// core library it did not find have no members and do not count. Read without
    /// <c>_decompilerLock</c>, as <see cref="ReadExternalBases"/> is.
    /// </summary>
    public ITypeDefinition FindExternalType(IReadOnlyList<FullTypeName> typeNames) =>
        typeNames
            .Select(typeName => _decompiler.TypeSystem.FindType(typeName).GetDefinition())
            .FirstOrDefault(found => found is { ParentModule: MetadataModule });

    /// <summary>
    /// The class <paramref name="typeName"/> names in the decompiler's type system, which holds
    /// this assembly and the assemblies the reference resolver found: the one class of that full
    /// name among them, or among the assemblies named <paramref name="assemblyName"/> where that is
    /// given. Null where none or several define it, since which one code that names a class by
    /// text reaches depends on the order the runtime loaded them in. The stand-in core types the
    /// decompiler adds for a core library it did not find do not count. Read without
    /// <c>_decompilerLock</c>, as <see cref="ReadExternalBases"/> is.
    /// </summary>
    public ITypeDefinition FindTypeNamed(FullTypeName typeName, string assemblyName) =>
        _decompiler.TypeSystem.Modules
            .OfType<MetadataModule>()
            .Where(module => assemblyName is null || string.Equals(module.AssemblyName, assemblyName, StringComparison.OrdinalIgnoreCase))
            .Select(module => module.GetTypeDefinition(typeName))
            .Where(found => found is not null)
            .Take(2)
            .ToList() is [var only] ? only : null;

    /// <summary>
    /// The members of a type this assembly only references, as its references name them: the
    /// rows that name a member of the type, and the fields and properties the assembly's
    /// attributes set on it by name.
    /// </summary>
    public (IReadOnlyList<TypeUsageScanner.ReferencedMember> Members, IReadOnlyList<string> SetByName)
        ReferencedMembersOf(SymbolResolver.ExternalTypeName type) =>
        ScanReferences(type, scan => (scan.ReferencedMembers(),
            (IReadOnlyList<string>)[.. scan.SetByName.Members().Select(member => member.Name)]));

    /// <summary>A type of the inspected assembly as the decompiler's type system sees it.</summary>
    private ITypeDefinition DecompilerCounterpartOf(ITypeDefinition type) =>
        _decompiler.TypeSystem.MainModule.GetDefinition((TypeDefinitionHandle)type.MetadataToken);

    /// <summary>
    /// Whether code can call <paramref name="method"/>, an extension method of the inspected
    /// assembly, on <paramref name="type"/>, by C#'s rule as the decompiler checks it before it
    /// writes such a call: the method's type arguments are inferred from the type, each one
    /// inferred has to meet its constraints, and the type has to convert to the receiver by
    /// identity, reference or boxing. The type is one of the inspected assembly, or one the
    /// decompiler's type system already holds, such as another library's type or an array built
    /// there. Read without <c>_decompilerLock</c>, as <see cref="ReadExternalBases"/> is.
    /// </summary>
    /// <remarks>
    /// The check runs in the decompiler's type system, which reads the base classes and
    /// interfaces other libraries define. ILens's own knows a type of another assembly by name
    /// alone, and two such types never convert to each other there. The decompiler's rule takes
    /// unmanaged for struct, fits a ref struct to a method without constraints, which C# does not,
    /// leaves a type parameter the receiver does not name unbound, so a constraint naming one
    /// fails, fits a generic type asked without type arguments only where every construction
    /// of it fits, and does not convert an array to Span&lt;T&gt; or ReadOnlySpan&lt;T&gt;, which
    /// C# 14 does for a receiver. In version 9.1 it does not convert an array to
    /// IReadOnlyCollection&lt;T&gt; either, which SymbolResolver.FitsArray adds.
    /// </remarks>
    public bool FitsExtension(IType type, IMethod method)
    {
        var counterpart = type is ITypeDefinition definition && definition.ParentModule == TypeSystem.MainModule
            ? DecompilerCounterpartOf(definition)
            : type;
        var extension = (IMethod)_decompiler.TypeSystem.MainModule.ResolveEntity(method.MetadataToken);
        return ICSharpCode.Decompiler.CSharp.Resolver.CSharpResolver.IsEligibleExtensionMethod(
            counterpart, extension, useTypeInference: true, out _);
    }

    /// <summary>
    /// The field <paramref name="name"/> of a type another assembly defines, as the decompiler's
    /// type system reads it from the assemblies the reference resolver found. The type is the
    /// first of <paramref name="typeNames"/> one of them defines, and <c>TypeFound</c> is false
    /// when none does; the stand-in core types the decompiler adds for a core library it did not
    /// find have no members and do not count. The type's own field comes before one a base class
    /// of its assembly declares, which it hides, as in C#. Read without <c>_decompilerLock</c>,
    /// as <see cref="ReadExternalBases"/> is.
    /// </summary>
    public (bool TypeFound, IField Field) FindDeclaredField(IReadOnlyList<FullTypeName> typeNames, string name)
    {
        if (FindExternalType(typeNames) is not { } definition)
            return (false, null);
        return (true, TypeWalker.WalkBaseTypes(definition).Prepend(definition)
            .Select(level => level.Fields.FirstOrDefault(field => field.Name == name))
            .FirstOrDefault(field => field is not null));
    }

    /// <summary>
    /// Decompile a type to full C# source. Thread-safe.
    /// </summary>
    public string DecompileType(ITypeDefinition type)
    {
        lock (_decompilerLock)
        {
            return _decompiler.DecompileTypeAsString(type.FullTypeName);
        }
    }

    /// <summary>
    /// Decompile a single method to C# source. Thread-safe.
    /// </summary>
    /// <remarks>
    /// An accessor decompiled on its own comes back as a bare <see cref="Accessor"/> node.
    /// ILSpy writes an accessor's keyword (get, set, init, add, remove) from the role the node
    /// holds in its property or event, not from the keyword token it carries, so a bare one
    /// would print as its modifiers and body alone. The node is re-attached under the role its
    /// token names, which the output visitor reads.
    /// </remarks>
    public string DecompileMethod(IMethod method)
    {
        lock (_decompilerLock)
        {
            var tree = _decompiler.Decompile(method.MetadataToken);
            foreach (var accessor in tree.Descendants.OfType<Accessor>().ToList())
            {
                if (AccessorRoleOf(accessor.Keyword.Role) is not { } role || accessor.Role == role)
                    continue;
                var parent = accessor.Parent;
                var next = accessor.NextSibling;
                accessor.Remove();
                if (next is null)
                    parent.AddChild(accessor, role);
                else
                    parent.InsertChildBefore(next, accessor, role);
            }

            var writer = new StringWriter();
            tree.AcceptVisitor(new CSharpOutputVisitor(writer, _settings.CSharpFormattingOptions));
            return writer.ToString();
        }
    }

    private static Role<Accessor> AccessorRoleOf(Role keywordRole)
    {
        if (keywordRole == PropertyDeclaration.GetKeywordRole)
            return PropertyDeclaration.GetterRole;
        if (keywordRole == PropertyDeclaration.SetKeywordRole || keywordRole == PropertyDeclaration.InitKeywordRole)
            return PropertyDeclaration.SetterRole;
        if (keywordRole == CustomEventDeclaration.AddKeywordRole)
            return CustomEventDeclaration.AddAccessorRole;
        if (keywordRole == CustomEventDeclaration.RemoveKeywordRole)
            return CustomEventDeclaration.RemoveAccessorRole;
        return null;
    }

    /// <summary>
    /// Decompile a property to its full C# declaration (signature plus both accessor
    /// bodies). Thread-safe. ILSpy's decompiler accepts any entity handle, so the
    /// property's metadata token decompiles to the whole declaration, <c>{ get; set; }</c>
    /// included.
    /// </summary>
    public string DecompileProperty(IProperty property)
    {
        lock (_decompilerLock)
        {
            return _decompiler.DecompileAsString(property.MetadataToken);
        }
    }

    /// <summary>
    /// Decompile an event to its full C# declaration (signature plus add/remove
    /// accessor bodies). Thread-safe. See <see cref="DecompileProperty"/> for the
    /// shared entity-handle path through ILSpy.
    /// </summary>
    public string DecompileEvent(IEvent @event)
    {
        lock (_decompilerLock)
        {
            return _decompiler.DecompileAsString(@event.MetadataToken);
        }
    }

    /// <summary>
    /// Decompile a field to its C# declaration, with the initializer the compiler moved into
    /// the constructors. Thread-safe.
    /// </summary>
    /// <remarks>
    /// The decompiler moves an assignment at the start of a constructor back into the field's
    /// declaration only when it decompiles the constructors beside the fields, and it stops at
    /// the first assignment to a field it does not see. So every field and constructor of the
    /// field's kind, static or instance, is decompiled, as ILSpy's own field view does. A third
    /// decompiler, sharing the first one's type system, then removes every declaration but the
    /// field's right after the initializers have moved, before the using declarations are
    /// collected, so those name what the field needs rather than what the constructors do.
    /// </remarks>
    public string DecompileField(IField field)
    {
        lock (_decompilerLock)
        {
            if (_fieldDecompiler is null)
            {
                var decompiler = new CSharpDecompiler((DecompilerTypeSystem)_decompiler.TypeSystem, CreateSettings());
                var initializers = decompiler.AstTransforms
                    .ToList()
                    .FindIndex(transform => transform is TransformFieldAndConstructorInitializers);
                if (initializers < 0)
                    throw new InvalidOperationException(
                        "ILSpy no longer moves field initializers in a transform of its own, so a field " +
                        "cannot be decompiled with its initializer.");
                decompiler.AstTransforms.Insert(initializers + 1, _keepOneField);
                _fieldDecompiler = decompiler;
            }

            var type = field.DeclaringTypeDefinition;
            var handles = type.Fields.Where(f => f.IsStatic == field.IsStatic).Select(f => f.MetadataToken)
                .Concat(type.DeclaredMethods().Where(m => m.IsConstructor && m.IsStatic == field.IsStatic).Select(m => m.MetadataToken))
                .ToList();
            _keepOneField.Field = field.MetadataToken;
            var tree = _fieldDecompiler.Decompile(handles);

            var writer = new StringWriter();
            tree.AcceptVisitor(new CSharpOutputVisitor(writer, _settings.CSharpFormattingOptions));
            return writer.ToString();
        }
    }

    /// <summary>
    /// The step of <see cref="DecompileField"/>'s decompiler that removes every top-level
    /// declaration but <see cref="Field"/>'s.
    /// </summary>
    private sealed class KeepOneField : IAstTransform
    {
        public EntityHandle Field { get; set; }  // set under _decompilerLock before each run

        public void Run(AstNode rootNode, TransformContext context)
        {
            foreach (var declaration in rootNode.Children.OfType<EntityDeclaration>().ToList())
            {
                if (declaration.GetSymbol() is not IField field || field.MetadataToken != Field)
                    declaration.Remove();
            }
        }
    }

    /// <summary>
    /// Decompile a member to a syntax tree in which every call writes its arguments in
    /// parameter order, with no named arguments, and a call to an extension method is a static
    /// call whose first argument is the receiver. Thread-safe.
    /// </summary>
    /// <remarks>
    /// A call site search maps each argument to its parameter by position. The host's own
    /// decompiler renders calls for reading: it may name and reorder arguments, and it writes
    /// extension calls. This second decompiler turns named arguments off and leaves out the step
    /// that writes extension calls. It shares the first one's type system, which spares a second
    /// load of the assembly. ILSpy reads the setting <c>ExtensionMethods</c> only when it builds a
    /// type system, so the setting alone does not stop that step. It still keeps a foreach from
    /// hiding a call to an extension <c>GetEnumerator</c>. Trailing optional arguments equal to
    /// their default are still omitted: ILSpy drops them even with <c>OptionalArguments</c> off,
    /// once a required argument precedes them, and only when the value equals the default exactly.
    /// </remarks>
    public ICSharpCode.Decompiler.CSharp.Syntax.SyntaxTree DecompileWithPositionalArguments(
        IEntity entity)
    {
        lock (_decompilerLock)
        {
            if (_positionalDecompiler is null)
            {
                var settings = CreateSettings();
                settings.NamedArguments = false;
                settings.NonTrailingNamedArguments = false;
                settings.ExtensionMethods = false;
                var decompiler = new CSharpDecompiler((DecompilerTypeSystem)_decompiler.TypeSystem, settings);
                var extensionCallSteps = decompiler.AstTransforms.Where(transform => transform is IntroduceExtensionMethods).ToList();
                if (extensionCallSteps.Count == 0)
                    throw new InvalidOperationException(
                        "ILSpy no longer writes extension calls in a transform of its own, so the arguments " +
                        "of a call to an extension method cannot be read in parameter order.");
                foreach (var step in extensionCallSteps)
                    decompiler.AstTransforms.Remove(step);
                _positionalDecompiler = decompiler;
            }
            return _positionalDecompiler.Decompile(entity.MetadataToken);
        }
    }

    /// <summary>
    /// Decompile <paramref name="types"/>, top-level types of this assembly, for a search over
    /// their text, and hand each to <paramref name="texts"/> as <see cref="CodeTextPass"/> packs
    /// it. Thread-safe, and not serialized against the host's own decompiler.
    /// </summary>
    /// <remarks>
    /// The pass runs on a type system built for it and dropped with it. A type system keeps what
    /// it is asked for, so a pass over a large assembly leaves about 190 MB in it (measured on a
    /// game assembly of 16 MB), which on the host's own would put the process over the memory
    /// budget and have the registry close this host with everything it holds. The pass reads the
    /// assembly through the image the host has loaded and opens the references anew, which it
    /// closes when it ends.
    /// </remarks>
    public void DecompileForSearch(
        IReadOnlyList<TypeDefinitionHandle> types, CodeTextStore.Search texts, CodeTextPass.Limit limit)
    {
        using var references = new ReferenceResolver(_referenceSearch, _guard);
        var typeSystem = new DecompilerTypeSystem(_peFile, references, _settings);
        CodeTextPass.Run(typeSystem, _settings, types, texts, limit);
    }

    /// <summary>
    /// Every method body in the assembly that calls one of <paramref name="targets"/>, with the
    /// number of <c>call</c>, <c>callvirt</c> and <c>newobj</c> instructions that call each
    /// target, one entry per body and target. A body here is a method as the compiler emitted
    /// it, so a lambda or an iterator's state machine counts separately from the method it was
    /// written in. Thread-safe.
    /// </summary>
    /// <remarks>
    /// A call is matched to its target statically, by the method the instruction names: a call
    /// made through a base or interface method is a call to that method, not to an override.
    /// A virtual call that names an override of one of <paramref name="slots"/>, each of them
    /// a target that introduced its slot, is counted as a call to that declaration, since it
    /// runs through the same slot as a call that names the declaration (see
    /// <see cref="BodiesNaming"/>). A method-group reference (<c>ldftn</c>) is not a call and
    /// is not counted. A call of a method that only passes a call on counts as the call that
    /// method makes (see <see cref="Proxies"/>).
    /// </remarks>
    public IReadOnlyList<(IMethod Method, IMethod Target, int Calls)> CountCallsTo(
        IReadOnlyCollection<IMethod> targets, IReadOnlyCollection<IMethod> slots)
    {
        lock (_decompilerLock)
        {
            var module = (MetadataModule)TypeSystem.MainModule;
            return BodiesNaming(targets, opCode => opCode is ILOpCode.Call or ILOpCode.Callvirt or ILOpCode.Newobj, slots)
                .Select(found => (module.GetDefinition(found.Body), found.Target, found.Count))
                .ToList();
        }
    }

    /// <summary>
    /// Every method whose code names <paramref name="method"/>, a method this assembly defines,
    /// in an instruction <see cref="NamesAMethod"/> accepts. A method here is one as the
    /// compiler emitted it, so a use inside a lambda is the lambda's, which
    /// <see cref="AttributeToSource"/> reports under the method it was written in. Thread-safe.
    /// </summary>
    /// <remarks>
    /// An instruction that names the method itself counts. A call through a declaration the
    /// method overrides names that declaration, and a call of a base class's member the method
    /// hides names that member, whichever of them runs. For a method that introduced a virtual
    /// slot, a virtual call or a virtual method-group reference that names an override of it
    /// counts as well: it runs through the slot as one that names the method does, and which
    /// of the two an instruction names is the compiler's choice (see
    /// <see cref="BodiesNaming"/>). For an override, only what names that override counts.
    /// ILSpyX's "Used By" analyzers for a method are not asked: the one for a method that is
    /// not virtual also counts a virtual call of the nearest base class member with the
    /// method's name and parameters, which lists the users of a hidden member under the member
    /// that hides it, and both end in an error on a body that breaks off inside an instruction
    /// naming a method. Code that calls a method which only passes a call on to
    /// <paramref name="method"/> names it as well (see <see cref="Proxies"/>).
    /// </remarks>
    public IReadOnlyList<ISymbol> FindMethodUsers(IMethod method)
    {
        lock (_decompilerLock)
        {
            var module = (MetadataModule)TypeSystem.MainModule;
            var asked = (IMethod)method.MemberDefinition;
            IMethod[] slots = SymbolResolver.IntroducesSlot(asked) ? [asked] : [];
            return BodiesNaming([asked], NamesAMethod, slots)
                .Select(found => (ISymbol)module.GetDefinition(found.Body))
                .ToList();
        }
    }

    /// <summary>
    /// True for an instruction whose operand names a method: a call, the creation of an object,
    /// a jump, and the loading of a method's address or of its token.
    /// </summary>
    public static bool NamesAMethod(ILOpCode opCode) => opCode is ILOpCode.Call or ILOpCode.Callvirt
        or ILOpCode.Newobj or ILOpCode.Jmp or ILOpCode.Ldftn or ILOpCode.Ldvirtftn or ILOpCode.Ldtoken;

    /// <summary>
    /// Each of <paramref name="targets"/> a method body names in an instruction
    /// <paramref name="isUse"/> accepts, with the number of those instructions, for every method
    /// body in the assembly: one entry per body and target. Called under <c>_decompilerLock</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An instruction is matched to the method it names, by that method's own row, also where it
    /// names the method through a reference row, as code does for a method of a generic type,
    /// or as an instance of a generic method. What a proxy names is counted for the bodies that
    /// call the proxy, once for each of their calls, since that is where the decompiled code
    /// shows it (see <see cref="Proxies"/>).
    /// </para>
    /// <para>
    /// A virtual instruction, <c>callvirt</c> or <c>ldvirtftn</c>, that names an override is
    /// booked under the declaration that introduced the override's slot, when that declaration
    /// is one of <paramref name="slots"/>, each of which is a target. Such an instruction runs
    /// through the slot whichever declaration of it the compiler wrote. A C# compiler names
    /// the slot's declaration, except for a method with a type parameter of its own, where it
    /// names the nearest override the receiver's class sees, and for an override with a more
    /// specific return type, which a call on an object of its class names; the VB and the F#
    /// compiler name the nearest override for any method. A call that is not virtual, as
    /// through <c>base.</c>, runs exactly the method it names and is booked under it.
    /// </para>
    /// </remarks>
    private List<(MethodDefinitionHandle Body, IMethod Target, int Count)> BodiesNaming(
        IReadOnlyCollection<IMethod> targets, Func<ILOpCode, bool> isUse, IReadOnlyCollection<IMethod> slots)
    {
        var targetsByToken = targets.ToDictionary(t => t.MetadataToken);
        var targetNames = targets.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var slotTokens = slots.Select(slot => slot.MetadataToken).ToHashSet();
        var slotNames = slots.Select(slot => slot.Name).ToHashSet(StringComparer.Ordinal);
        var slotOf = new Dictionary<EntityHandle, EntityHandle>();
        var module = (MetadataModule)TypeSystem.MainModule;
        var metadata = _peFile.Metadata;
        var named = new Dictionary<EntityHandle, EntityHandle>();
        var found = new List<(MethodDefinitionHandle Body, IMethod Target, int Count)>();
        var entries = new Dictionary<(MethodDefinitionHandle, EntityHandle), int>();
        var proxies = Proxies();

        foreach (var handle in metadata.MethodDefinitions)
        {
            var definition = metadata.GetMethodDefinition(handle);
            if (definition.RelativeVirtualAddress == 0)
                continue;

            Dictionary<EntityHandle, int> inBody = null;
            try
            {
                var reader = _peFile.GetMethodBody(definition.RelativeVirtualAddress).GetILReader();
                while (reader.RemainingBytes > 0)
                {
                    var opCode = reader.DecodeOpCode();
                    if (isUse(opCode))
                    {
                        var virtualUse = opCode is ILOpCode.Callvirt or ILOpCode.Ldvirtftn;
                        var target = TargetOf(MetadataTokenHelpers.EntityHandleOrNil(reader.ReadInt32()), virtualUse);
                        if (!target.IsNil)
                        {
                            inBody ??= [];
                            inBody[target] = inBody.GetValueOrDefault(target) + 1;
                        }
                    }
                    else
                    {
                        reader.SkipOperand(opCode);
                    }
                }
            }
            catch (BadImageFormatException)
            {
                // The body breaks off, or the metadata reader cannot decode it further. What it
                // names up to there counts; the rest cannot be read either way.
            }

            if (inBody is null)
                continue;
            foreach (var (target, count) in inBody)
            {
                if (!proxies.TryGetValue(handle, out var proxy))
                {
                    Book(handle, target, count);
                    continue;
                }
                if (proxy.AddressTaken)
                    Book(handle, target, count);
                foreach (var (caller, calls) in proxy.Callers)
                    Book(caller, target, count * calls);
            }
        }
        return found;

        // A body can name a target itself and through a proxy; it keeps one entry.
        void Book(MethodDefinitionHandle body, EntityHandle target, int count)
        {
            if (entries.TryGetValue((body, target), out var index))
                found[index] = (body, found[index].Target, found[index].Count + count);
            else
            {
                entries[(body, target)] = found.Count;
                found.Add((body, targetsByToken[target], count));
            }
        }

        // The token of the target the operand's instruction is booked under, or a nil handle for
        // anything else: another method, or a row the metadata reader cannot decode, after which
        // the body is read on. virtualUse says that the instruction is a virtual one.
        EntityHandle TargetOf(EntityHandle operand, bool virtualUse)
        {
            try
            {
                switch (operand.Kind)
                {
                    case HandleKind.MethodDefinition:
                        return Booked(operand, virtualUse);
                    case HandleKind.MethodSpecification:
                        return TargetOf(metadata.GetMethodSpecification((MethodSpecificationHandle)operand).Method, virtualUse);
                    case HandleKind.MemberReference:
                        if (!named.TryGetValue(operand, out var definition))
                            named[operand] = definition = DefinitionNamed(operand);
                        return definition.IsNil ? default : Booked(definition, virtualUse);
                    default:
                        return default;
                }
            }
            catch (BadImageFormatException)
            {
                return default;
            }
        }

        // What an instruction naming a method of this assembly is booked under: for a virtual
        // one that names an override of a slot, that slot's declaration, and otherwise the
        // method itself when it is a target.
        EntityHandle Booked(EntityHandle definition, bool virtualUse)
        {
            if (virtualUse && slotTokens.Count > 0 && SlotOf(definition) is { IsNil: false } slot)
                return slot;
            return targetsByToken.ContainsKey(definition) ? definition : default;
        }

        // The one of slots that the method overrides, or a nil handle. An override is virtual
        // by its flags and has the name of the declaration it overrides, which are cheap
        // filters; only a method that passes both is looked up. Whether it is a new slot does
        // not filter: an override with a more specific return type is one.
        EntityHandle SlotOf(EntityHandle definition)
        {
            if (slotOf.TryGetValue(definition, out var slot))
                return slot;
            var handle = (MethodDefinitionHandle)definition;
            var row = metadata.GetMethodDefinition(handle);
            const System.Reflection.MethodAttributes dispatch = System.Reflection.MethodAttributes.Static
                | System.Reflection.MethodAttributes.Virtual;
            slot = (row.Attributes & dispatch) == System.Reflection.MethodAttributes.Virtual
                && slotNames.Contains(metadata.GetString(row.Name))
                && module.GetDefinition(handle) is { } method
                && Overriding.Of(method)
                && SymbolResolver.SlotDeclarationOf(method) is { } declaration
                && declaration.ParentModule == module
                && slotTokens.Contains(declaration.MetadataToken)
                ? declaration.MetadataToken
                : default;
            return slotOf[definition] = slot;
        }

        // The method of this assembly a reference row names, as code names a method of a
        // generic type, or a nil handle. The name is a cheap filter, since a slot is a target
        // and an override has its name; only a row that passes it is resolved.
        EntityHandle DefinitionNamed(EntityHandle reference)
        {
            var name = metadata.GetString(metadata.GetMemberReference((MemberReferenceHandle)reference).Name);
            return targetNames.Contains(name)
                && module.ResolveEntity(reference, default) is IMember { MemberDefinition: { } resolved }
                && resolved.ParentModule == module
                && resolved.MetadataToken.Kind == HandleKind.MethodDefinition
                ? resolved.MetadataToken
                : default;
        }
    }

    /// <summary>
    /// The members whose code calls a method <paramref name="isTarget"/> accepts, each with one
    /// entry per call. A method is matched by the full name of the type declaring it, with '+'
    /// for nesting, and by its name, as the call instruction names them, so the assembly that
    /// defines it need not load. A method-group reference counts as a call. A call in code the
    /// compiler generated, such as a lambda or an iterator's state machine, counts toward the
    /// member the decompiler shows it in, whose <see cref="MethodUnit"/> holds it; generated code
    /// no member's unit holds counts as a member of its own. Thread-safe.
    /// </summary>
    public IReadOnlyList<(IMethod Member, IReadOnlyList<(string Type, string Name)> Calls)> MembersCalling(
        Func<string, string, bool> isTarget) =>
        MembersUsing(opCode => opCode is ILOpCode.Call or ILOpCode.Callvirt or ILOpCode.Newobj
            or ILOpCode.Ldftn or ILOpCode.Ldvirtftn, isTarget);

    /// <summary>
    /// The members whose code stores to a field <paramref name="isTarget"/> accepts, each with one
    /// entry per store, matched and attributed as <see cref="MembersCalling"/> matches and
    /// attributes a call. Thread-safe.
    /// </summary>
    public IReadOnlyList<(IMethod Member, IReadOnlyList<(string Type, string Name)> Stores)> MembersStoring(
        Func<string, string, bool> isTarget) =>
        MembersUsing(opCode => opCode is ILOpCode.Stfld or ILOpCode.Stsfld, isTarget);

    /// <summary>
    /// The members whose code loads a field <paramref name="isTarget"/> accepts, its value or its
    /// address, each with one entry per load, matched and attributed as <see cref="MembersCalling"/>
    /// matches and attributes a call. Thread-safe.
    /// </summary>
    public IReadOnlyList<(IMethod Member, IReadOnlyList<(string Type, string Name)> Loads)> MembersLoading(
        Func<string, string, bool> isTarget) =>
        MembersUsing(opCode => opCode is ILOpCode.Ldfld or ILOpCode.Ldflda or ILOpCode.Ldsfld or ILOpCode.Ldsflda, isTarget);

    /// <summary>
    /// The members whose code holds an instruction <paramref name="isUse"/> accepts whose operand
    /// names a method or field <paramref name="isTarget"/> accepts, by the full name of its declaring
    /// type and its name.
    /// </summary>
    private IReadOnlyList<(IMethod Member, IReadOnlyList<(string Type, string Name)> Uses)> MembersUsing(
        Func<ILOpCode, bool> isUse, Func<string, string, bool> isTarget)
    {
        lock (_decompilerLock)
        {
            var module = (MetadataModule)TypeSystem.MainModule;
            var metadata = _peFile.Metadata;
            var members = new Dictionary<EntityHandle, (IMethod Member, List<(string Type, string Name)> Uses)>();
            var units = new Dictionary<EntityHandle, HashSet<EntityHandle>>();

            foreach (var handle in metadata.MethodDefinitions)
            {
                var definition = metadata.GetMethodDefinition(handle);
                if (definition.RelativeVirtualAddress == 0)
                    continue;

                List<(string Type, string Name)> uses = null;
                try
                {
                    var reader = _peFile.GetMethodBody(definition.RelativeVirtualAddress).GetILReader();
                    while (reader.RemainingBytes > 0)
                    {
                        var opCode = reader.DecodeOpCode();
                        if (isUse(opCode))
                        {
                            if (OperandName(MetadataTokenHelpers.EntityHandleOrNil(reader.ReadInt32())) is { } operand
                                && isTarget(operand.Type, operand.Name))
                                (uses ??= []).Add(operand);
                        }
                        else
                        {
                            reader.SkipOperand(opCode);
                        }
                    }
                }
                catch (BadImageFormatException)
                {
                    // A body the metadata reader cannot decode is passed over as a whole.
                    continue;
                }

                if (uses is null)
                    continue;
                var member = MemberShowing(module.GetDefinition(handle), units);
                if (!members.TryGetValue(member.MetadataToken, out var entry))
                    members[member.MetadataToken] = entry = (member, []);
                entry.Uses.AddRange(uses);
            }
            return members.Values
                .Select(entry => (entry.Member, (IReadOnlyList<(string Type, string Name)>)entry.Uses))
                .ToList();

            // The declaring type's full name and the name of the method or field an instruction
            // names, or null for a member of a generic type's instance, which is never matched.
            (string Type, string Name)? OperandName(EntityHandle operand)
            {
                switch (operand.Kind)
                {
                    case HandleKind.MethodSpecification:
                        return OperandName(metadata.GetMethodSpecification((MethodSpecificationHandle)operand).Method);
                    case HandleKind.MemberReference:
                        var reference = metadata.GetMemberReference((MemberReferenceHandle)operand);
                        return reference.Parent.Kind is HandleKind.TypeReference or HandleKind.TypeDefinition
                            ? (reference.Parent.GetFullTypeName(metadata).ReflectionName, metadata.GetString(reference.Name))
                            : null;
                    case HandleKind.MethodDefinition:
                        var method = metadata.GetMethodDefinition((MethodDefinitionHandle)operand);
                        return (((EntityHandle)method.GetDeclaringType()).GetFullTypeName(metadata).ReflectionName,
                            metadata.GetString(method.Name));
                    case HandleKind.FieldDefinition:
                        var field = metadata.GetFieldDefinition((FieldDefinitionHandle)operand);
                        return (((EntityHandle)field.GetDeclaringType()).GetFullTypeName(metadata).ReflectionName,
                            metadata.GetString(field.Name));
                    default:
                        return null;
                }
            }
        }
    }

    /// <summary>
    /// The member whose decompiled code shows <paramref name="method"/>'s body: the method itself,
    /// or for code the compiler generated, the method or accessor of the type it was generated for
    /// whose <see cref="MethodUnit"/> holds it. Units are computed once into
    /// <paramref name="units"/>. Called under <c>_decompilerLock</c>.
    /// </summary>
    private IMethod MemberShowing(IMethod method, Dictionary<EntityHandle, HashSet<EntityHandle>> units)
    {
        if (!CompilerGeneratedFilter.IsCompilerGenerated(method) || method.DeclaringTypeDefinition is not { } declaring)
            return method;

        foreach (var candidate in WrittenMembers(OwnerOf(declaring)))
        {
            if (!units.TryGetValue(candidate.MetadataToken, out var unit))
                units[candidate.MetadataToken] = unit = MethodUnit(candidate).Select(part => part.MetadataToken).ToHashSet();
            if (unit.Contains(method.MetadataToken))
                return candidate;
        }
        return method;
    }

    /// <summary>
    /// The methods and accessors of <paramref name="type"/> that someone wrote, each of which
    /// generated code can belong to.
    /// </summary>
    private static IEnumerable<IMethod> WrittenMembers(ITypeDefinition type) =>
        type.DeclaredMethods().Where(candidate => !CompilerGeneratedFilter.IsCompilerGenerated(candidate))
            .Concat(type.Properties.SelectMany(property => new[] { property.Getter, property.Setter }))
            .Concat(type.Events.SelectMany(@event => new[] { @event.AddAccessor, @event.RemoveAccessor, @event.InvokeAccessor }))
            .Where(candidate => candidate is not null);

    /// <summary>
    /// Build an AnalyzerContext for the ILSpyX analyzer infrastructure.
    /// Private — callers go through <see cref="RunAnalyzer"/>, which serializes
    /// the analyzer call against the decompiler via <c>_decompilerLock</c>.
    /// </summary>
    private AnalyzerContext CreateAnalyzerContext(CancellationToken ct = default)
    {
        return new AnalyzerContext
        {
            AssemblyList = _assemblyList,
            Language = new StubLanguage(),
            CancellationToken = ct,
            SortResults = true
        };
    }

    /// <summary>
    /// Run an ILSpyX analyzer by header name (e.g., "Instantiated By", "Exposed By").
    /// Returns the analyzer results as a list of symbols. Thread-safe — analyzers
    /// walk the underlying assembly metadata, so the call serializes against
    /// the decompiler through <c>_decompilerLock</c>.
    /// </summary>
    /// <param name="analyzerName">
    /// The type name of the analyzer to run, which then runs whether or not it would show the
    /// symbol; <c>null</c> runs the first analyzer under the header that shows it.
    /// </param>
    public IReadOnlyList<ISymbol> RunAnalyzer(string header, ISymbol symbol, string analyzerName = null,
        CancellationToken ct = default)
    {
        lock (_decompilerLock)
        {
            // Multiple analyzers can share the same header (e.g., "Used By" exists
            // for types, methods, and fields). Find the one that accepts this symbol.
            foreach (var (attr, analyzerType) in ExportAnalyzerAttribute.GetAnnotatedAnalyzers()
                .Where(a => a.AttributeData.Header == header
                    && (analyzerName is null || a.AnalyzerType.Name == analyzerName)))
            {
                var analyzer = (IAnalyzer)Activator.CreateInstance(analyzerType)!;
                if (analyzerName is not null || analyzer.Show(symbol))
                {
                    var context = CreateAnalyzerContext(ct);
                    return analyzer.Analyze(symbol, context).ToList();
                }
            }

            return Array.Empty<ISymbol>();
        }
    }

    /// <summary>
    /// Report each result of a "who uses this?" question under the member it was written in:
    /// a use inside a lambda, local function, iterator or async body under the method that
    /// contains it, and a use inside an accessor under its property or event. Returns one
    /// result per input, in order. Thread-safe — reads method bodies, so the call serializes
    /// against the decompiler through <c>_decompilerLock</c>.
    /// </summary>
    /// <remarks>
    /// The incoming analyzers get an empty map from <see cref="StubLanguage.GetCodeMappingInfo"/>
    /// and report a use inside generated code under the generated method, as ILens's own passes
    /// over the method bodies do. This is the one place that attributes it to source, so every
    /// incoming kind gives the same answer whatever produced it. A source method reported twice this way, once directly
    /// and once through its generated code, is one line in analyze's answer, which prints
    /// once what prints alike in the full form as well. Fields and types the compiler
    /// generated pass through unchanged.
    /// </remarks>
    public IReadOnlyList<ISymbol> AttributeToSource(IReadOnlyList<ISymbol> results)
    {
        lock (_decompilerLock)
        {
            var writtenIn = new Dictionary<EntityHandle, Dictionary<EntityHandle, EntityHandle>>();
            return results
                .Select(result => result is IMethod method ? SourceMemberOf(method, writtenIn) : result)
                .ToList();
        }
    }

    private ISymbol SourceMemberOf(IMethod method, Dictionary<EntityHandle, Dictionary<EntityHandle, EntityHandle>> writtenIn)
    {
        var source = SourceMethodOf(method, writtenIn);
        return (ISymbol)source.AccessorOwner ?? source;
    }

    /// <summary>
    /// The method <paramref name="method"/> was written in: the method or accessor of the type
    /// it was generated in whose <see cref="MethodParts"/> hold it. <paramref name="method"/>
    /// itself when the compiler did not generate it, and when no member's parts hold it, as for
    /// an iterator class's <c>Reset</c>. The parts of each type's members are collected once
    /// into <paramref name="writtenIn"/>. Called under <c>_decompilerLock</c>.
    /// </summary>
    /// <remarks>
    /// An ILSpyX analyzer hands out methods of a type system of its own over the same file.
    /// The parts are read in this host's type system, and the answer comes from the one
    /// <paramref name="method"/> came from.
    /// </remarks>
    private IMethod SourceMethodOf(IMethod method, Dictionary<EntityHandle, Dictionary<EntityHandle, EntityHandle>> writtenIn)
    {
        var main = (MetadataModule)TypeSystem.MainModule;
        if (method.ParentModule is not MetadataModule module || module.MetadataFile != main.MetadataFile
            || method.MetadataToken.IsNil || method.MetadataToken.Kind != HandleKind.MethodDefinition
            || !CompilerGeneratedFilter.IsCompilerGenerated(method)
            || main.GetDefinition((MethodDefinitionHandle)method.MetadataToken).DeclaringTypeDefinition is not { } declaringType)
            return method;

        var owner = OwnerOf(declaringType);
        if (!writtenIn.TryGetValue(owner.MetadataToken, out var sources))
        {
            writtenIn[owner.MetadataToken] = sources = [];
            // A generated method of the type that no written member reaches may hold generated
            // code of its own, as the method made of a file's top-level statements does with
            // its lambdas. It is asked after the written members, which come first for a part
            // two candidates reach.
            var generated = owner.DeclaredMethods().Where(CompilerGeneratedFilter.IsCompilerGenerated);
            foreach (var candidate in WrittenMembers(owner).Concat(generated))
            {
                // A generated method that is a part already was read together with its member.
                if (sources.ContainsKey(candidate.MetadataToken))
                    continue;
                foreach (var part in MethodParts(candidate).Skip(1))
                    sources.TryAdd(part.MetadataToken, candidate.MetadataToken);
            }
        }
        return sources.TryGetValue(method.MetadataToken, out var source)
            ? module.GetDefinition((MethodDefinitionHandle)source)
            : method;
    }

    /// <summary>
    /// Disassemble a method body to normalized IL text. The cross-assembly comparer
    /// builds a method's IL from it together with that of its generated code, for its
    /// verdict and for the IL-form output of <c>compare</c> (text equality = body
    /// equality across rebuilds, since operand tokens render as symbolic names).
    /// Returns <c>null</c> for a method with no body —
    /// abstract, extern, or an interface method, as <see cref="WhyNoBody"/> reads
    /// its row. Thread-safe — shares the
    /// per-host <c>_decompilerLock</c> with the C# decompiler so the underlying
    /// PE handle cannot be torn down mid-walk.
    ///
    /// Strips the disassembler's leading <c>// Method begins at RVA 0xNNNN</c>
    /// comment line — it's file-position metadata, not body content, and varies
    /// between rebuilds whenever anything earlier in the assembly shifts. Without
    /// this strip every method-body comparison across two real builds would
    /// false-positive on RVA-only differences.
    /// </summary>
    public string DisassembleMethodBody(IMethod method)
    {
        var token = method.MetadataToken;
        // Specialized generics and a few synthetic methods carry a non-
        // MethodDefinition handle (MemberReference, MethodSpecification). They
        // have no readable body in this assembly's metadata — return null and
        // let the caller treat that as "no body to compare".
        if (token.IsNil || token.Kind != HandleKind.MethodDefinition) return null;
        var handle = (MethodDefinitionHandle)token;
        var def = _peFile.Metadata.GetMethodDefinition(handle);
        if (def.RelativeVirtualAddress == 0) return null;

        lock (_decompilerLock)
        {
            var output = new PlainTextOutput();
            var disasm = new MethodBodyDisassembler(output, CancellationToken.None)
            {
                DetectControlStructure = false
            };
            disasm.Disassemble(_peFile, handle);
            return StripRvaHeader(output.ToString());
        }
    }

    /// <summary>Why a method has no body in its file, as its row says.</summary>
    public enum MissingBody
    {
        /// <summary>The method has a body.</summary>
        None,
        /// <summary>Abstract, as an interface method without a default implementation is too.</summary>
        Abstract,
        /// <summary>Extern: a DllImport, or a call into the runtime's own code (InternalCall).</summary>
        Extern,
        /// <summary>The runtime provides its code, as it does a delegate's methods.</summary>
        Runtime,
        /// <summary>The row says nothing of why, as in a copy whose bodies a tool removed.</summary>
        Unstated,
    }

    /// <summary>
    /// Why <paramref name="method"/> has no body in this file, read from its row:
    /// <see cref="MissingBody.None"/> where the row holds the address of a body, which is where
    /// <see cref="DisassembleMethodBody"/> reads one.
    /// </summary>
    public MissingBody WhyNoBody(IMethod method)
    {
        var token = method.MetadataToken;
        if (token.IsNil || token.Kind != HandleKind.MethodDefinition) return MissingBody.Unstated;
        var row = _peFile.Metadata.GetMethodDefinition((MethodDefinitionHandle)token);
        if (row.RelativeVirtualAddress != 0) return MissingBody.None;
        if ((row.Attributes & System.Reflection.MethodAttributes.Abstract) != 0) return MissingBody.Abstract;
        if ((row.Attributes & System.Reflection.MethodAttributes.PinvokeImpl) != 0
            || (row.ImplAttributes & System.Reflection.MethodImplAttributes.InternalCall) != 0)
            return MissingBody.Extern;
        return (row.ImplAttributes & System.Reflection.MethodImplAttributes.CodeTypeMask)
            == System.Reflection.MethodImplAttributes.Runtime
                ? MissingBody.Runtime
                : MissingBody.Unstated;
    }

    /// <summary>
    /// Drop ILSpy's <c>// Method begins at RVA 0xNNNN</c> first line if present.
    /// The other comment lines the disassembler emits (<c>Header size</c>,
    /// <c>Code size</c>, <c>.maxstack</c>, locals block) are stable when the IL
    /// doesn't change, so they don't need stripping.
    /// </summary>
    private static string StripRvaHeader(string text)
    {
        const string rvaPrefix = "// Method begins at RVA ";
        if (!text.StartsWith(rvaPrefix, StringComparison.Ordinal)) return text;
        var newline = text.IndexOf('\n');
        return newline >= 0 ? text.Substring(newline + 1) : "";
    }

    /// <summary>
    /// The methods <c>compare</c> judges as one: <paramref name="method"/> first, then each method
    /// the compiler generated for it, in the order the IL first reaches them. These are its lambdas
    /// and local functions, the methods of its closure classes and of an iterator's or async
    /// method's state machine, and for a method Burst compiles, the copy of its code Burst keeps,
    /// <c>X$BurstManaged</c>. A generated method of another type the IL calls joins as well, such
    /// as an anonymous type's constructor, whose body names the properties the type's name leaves
    /// out. Thread-safe.
    /// </summary>
    /// <remarks>
    /// A generated class nested in the method's own type brings in all of its methods, because the
    /// runtime, not the method, calls a state machine's <c>MoveNext</c>. The class <c>&lt;&gt;c</c>
    /// is the exception: it holds the lambdas of every method of its type that capture nothing, so
    /// only the ones referenced join. Burst's wrapper class and delegate are walked through, which
    /// is how the copy is reached, but left out: Burst writes them from the method's signature, so
    /// they differ only where the signature does, or where Burst itself changed.
    /// </remarks>
    public IReadOnlyList<IMethod> MethodUnit(IMethod method) => GeneratedFor(method, writtenOnly: false);

    /// <summary>
    /// The methods that hold the code someone wrote in <paramref name="method"/>, which
    /// <c>analyze</c> reports as one member: <paramref name="method"/> first, then its local
    /// functions and lambdas and theirs, and of an iterator's or async method's class the
    /// methods that hold the method's body. Thread-safe.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Narrower than <see cref="MethodUnit"/>, which counts everything the compiler generated
    /// for a method. A called method joins only when it is a local function, and a method
    /// whose address is taken only when the compiler generated it. A class nested in the type
    /// the code is read in joins only when it is an iterator's or async method's class, and
    /// then without its accessors and without the methods the compiler marks
    /// <c>DebuggerHidden</c>: its <c>Reset</c> throws <c>NotSupportedException</c>, which no
    /// iterator anyone wrote uses. A closure class brings in the lambdas referenced and not
    /// its constructor, and Burst's copy of a method stays a member of its own.
    /// </para>
    /// <para>
    /// These are the rules of ILSpy's <c>CSharpDecompiler.GetCodeMappingInfo</c>, which ILens
    /// does not ask, since its <c>ReadCodeMappingInfo</c> loses or misplaces code wherever an
    /// instruction names a generated method through a reference and not through its
    /// definition, as it does in a generic type and in a generic method (read in
    /// ICSharpCode.Decompiler 9.1.0.7988 and in 11.1.0.9782). A call is followed only to a
    /// method definition, so not to a local function of a generic type. An address is not
    /// followed when it names a method specification, as for a lambda that is a generic method
    /// itself. An address taken through a member reference is followed only into a class nested
    /// in the type of the code being read, so not from an iterator's or async method's class to
    /// a lambda's class beside it; and where it is followed, every method of that class is
    /// connected, so the first method of a type that takes a lambda of the shared class
    /// <c>&lt;&gt;c</c> gets all of that class's lambdas. With an ILSpy whose map has none of
    /// these faults, <see cref="AttributeToSource"/> and <see cref="FindUsedSymbols"/> can take
    /// a method's parts from that map and this reading can go.
    /// </para>
    /// <para>
    /// A method the compiler added only to pass a call on (see <see cref="Proxies"/>) is no
    /// part, although the decompiler shows its call in the member: the attributes on it are the
    /// compiler's, and what carries an attribute is reported by the member whose parts hold
    /// it. What such a method names is counted for its callers where the code is read.
    /// </para>
    /// </remarks>
    public IReadOnlyList<IMethod> MethodParts(IMethod method) => GeneratedFor(method, writtenOnly: true);

    /// <summary>
    /// What the code of <paramref name="method"/>, a method this assembly defines, uses, its
    /// lambdas, local functions and iterator or async body included
    /// (<see cref="MethodUseScanner"/>). Thread-safe — reads method bodies, so the call
    /// serializes against the decompiler through <c>_decompilerLock</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The scan resolves in a type system of its own, built at the first call and kept, over
    /// the reference files the decompiler's type system holds (<see cref="LoadedReferences"/>).
    /// A member of a referenced assembly found there prints with what only its definition
    /// says, such as <c>out</c> for a parameter, wherever the reference search found the
    /// assembly. The results are read after the call without <c>_decompilerLock</c>, as
    /// <see cref="ReadExternalBases"/> reads the decompiler's type system.
    /// </para>
    /// <para>
    /// The decompiler's own type system reads by the rules of the C# version the decompiler
    /// writes (<see cref="CreateSettings"/>), this one by the default rules. A parameter's
    /// keyword depends on neither, since <see cref="ParameterPassing"/> takes it from the
    /// metadata row. Whether the scan would answer alike in the decompiler's type system,
    /// which would save keeping this one, is not measured: one answer that differs over an
    /// assembly compiled with a newer C# version would show that it does not. A type system
    /// built for each call does not serve: it answers the same and took four times as long
    /// over a large assembly, 101 s against 24 s for 9,130 calls (measured 2026-10-11).
    /// </para>
    /// </remarks>
    public IReadOnlyList<ISymbol> FindUsedSymbols(IMethod method)
    {
        if (method.ParentModule != TypeSystem.MainModule || method.MetadataToken.Kind != HandleKind.MethodDefinition)
            return [];

        lock (_decompilerLock)
        {
            _methodUseTypeSystem ??= new DecompilerTypeSystem(_peFile, new LoadedReferences(_decompiler.TypeSystem));
            // A proxy the method's code calls is read with it: the call it passes on is one
            // the method's source writes.
            var parts = MethodParts(method).Select(part => (MethodDefinitionHandle)part.MetadataToken).ToList();
            var passedOn = Proxies()
                .Where(proxy => !parts.Contains(proxy.Key) && proxy.Value.Callers.Any(calling => parts.Contains(calling.Body)))
                .Select(proxy => proxy.Key)
                .ToList();
            return MethodUseScanner.Scan(_methodUseTypeSystem.MainModule, parts.Concat(passedOn));
        }
    }

    /// <summary>
    /// <see cref="MethodUnit"/>, or with <paramref name="writtenOnly"/> <see cref="MethodParts"/>:
    /// one walk over the IL from <paramref name="method"/> through the generated methods it
    /// leads to, which the two read by different rules.
    /// </summary>
    private IReadOnlyList<IMethod> GeneratedFor(IMethod method, bool writtenOnly)
    {
        var module = (MetadataModule)TypeSystem.MainModule;
        if (method.ParentModule != module || method.MetadataToken.IsNil
            || method.MetadataToken.Kind != HandleKind.MethodDefinition
            || method.DeclaringTypeDefinition is not { } declaringType)
            return [method];

        lock (_decompilerLock)
        {
            var metadata = _peFile.Metadata;
            var owner = OwnerOf(declaringType);
            var unit = new List<IMethod>();
            var queued = new HashSet<MethodDefinitionHandle>();
            var visitedTypes = new HashSet<TypeDefinitionHandle>();
            var queue = new Queue<MethodDefinitionHandle>();
            Enqueue((MethodDefinitionHandle)method.MetadataToken);

            while (queue.Count > 0)
            {
                var handle = queue.Dequeue();
                var part = module.GetDefinition(handle);
                if (part.DeclaringTypeDefinition is not { } partType || !CompilerGeneratedFilter.IsBurstWrapper(partType))
                    unit.Add(part);

                var definition = metadata.GetMethodDefinition(handle);
                if (definition.RelativeVirtualAddress == 0)
                    continue;
                try
                {
                    var reader = _peFile.GetMethodBody(definition.RelativeVirtualAddress).GetILReader();
                    while (reader.RemainingBytes > 0)
                    {
                        var opCode = reader.DecodeOpCode();
                        if (writtenOnly)
                        {
                            ReadWritten(opCode, ref reader, definition.GetDeclaringType());
                            continue;
                        }
                        switch (opCode)
                        {
                            case ILOpCode.Call or ILOpCode.Callvirt or ILOpCode.Newobj or ILOpCode.Jmp
                                or ILOpCode.Ldftn or ILOpCode.Ldvirtftn:
                                VisitMethod(MetadataTokenHelpers.EntityHandleOrNil(reader.ReadInt32()));
                                break;
                            case ILOpCode.Ldfld or ILOpCode.Ldflda or ILOpCode.Stfld
                                or ILOpCode.Ldsfld or ILOpCode.Ldsflda or ILOpCode.Stsfld:
                                VisitType(FieldOwner(MetadataTokenHelpers.EntityHandleOrNil(reader.ReadInt32())));
                                break;
                            case ILOpCode.Ldtoken:
                                var token = MetadataTokenHelpers.EntityHandleOrNil(reader.ReadInt32());
                                if (token.Kind is HandleKind.MethodDefinition or HandleKind.MethodSpecification)
                                    VisitMethod(token);
                                else if (token.Kind is HandleKind.FieldDefinition)
                                    VisitType(FieldOwner(token));
                                else if (token.Kind is HandleKind.MemberReference)
                                {
                                    VisitMethod(token);
                                    VisitType(FieldOwner(token));
                                }
                                else
                                    VisitType(TypeOf(token));
                                break;
                            case ILOpCode.Box or ILOpCode.Castclass or ILOpCode.Cpobj or ILOpCode.Initobj
                                or ILOpCode.Isinst or ILOpCode.Ldelem or ILOpCode.Ldelema or ILOpCode.Ldobj
                                or ILOpCode.Mkrefany or ILOpCode.Newarr or ILOpCode.Refanyval or ILOpCode.Sizeof
                                or ILOpCode.Stelem or ILOpCode.Stobj or ILOpCode.Unbox or ILOpCode.Unbox_any
                                or ILOpCode.Constrained:
                                VisitType(TypeOf(MetadataTokenHelpers.EntityHandleOrNil(reader.ReadInt32())));
                                break;
                            default:
                                reader.SkipOperand(opCode);
                                break;
                        }
                    }
                }
                catch (BadImageFormatException)
                {
                    // A body the metadata reader cannot decode still counts by its disassembly;
                    // only the generated code it would lead to is not reached.
                }
            }
            return unit;

            void Enqueue(MethodDefinitionHandle handle)
            {
                if (queued.Add(handle))
                    queue.Enqueue(handle);
            }

            // One instruction as MethodParts reads it, in code that readIn declares.
            void ReadWritten(ILOpCode opCode, ref BlobReader reader, TypeDefinitionHandle readIn)
            {
                switch (opCode)
                {
                    case ILOpCode.Call or ILOpCode.Callvirt:
                        var called = MethodOf(MetadataTokenHelpers.EntityHandleOrNil(reader.ReadInt32()));
                        if (!called.IsNil && IsLocalFunction(called))
                            Enqueue(called);
                        break;
                    case ILOpCode.Ldftn:
                        var taken = MethodOf(MetadataTokenHelpers.EntityHandleOrNil(reader.ReadInt32()));
                        if (!taken.IsNil && IsGenerated(taken))
                            Enqueue(taken);
                        break;
                    case ILOpCode.Newobj or ILOpCode.Stfld:
                        var token = MetadataTokenHelpers.EntityHandleOrNil(reader.ReadInt32());
                        var constructor = MethodOf(token);
                        VisitStateMachine(
                            constructor.IsNil ? FieldOwner(token) : metadata.GetMethodDefinition(constructor).GetDeclaringType(),
                            readIn);
                        break;
                    default:
                        reader.SkipOperand(opCode);
                        break;
                }
            }

            bool IsLocalFunction(MethodDefinitionHandle handle)
            {
                if (!_localFunctions.TryGetValue(handle, out var local))
                    _localFunctions[handle] = local =
                        ICSharpCode.Decompiler.IL.Transforms.LocalFunctionDecompiler.IsLocalFunctionMethod(_peFile, handle);
                return local;
            }

            bool IsGenerated(MethodDefinitionHandle handle)
            {
                if (!_generatedMethods.TryGetValue(handle, out var generated))
                    _generatedMethods[handle] = generated =
                        CompilerGeneratedFilter.IsCompilerGenerated(module.GetDefinition(handle));
                return generated;
            }

            // An iterator's or async method's class joins when the type the code is read in
            // declares it, with the methods that hold the body: not its accessors, and not the
            // ones marked DebuggerHidden.
            void VisitStateMachine(TypeDefinitionHandle handle, TypeDefinitionHandle readIn)
            {
                if (handle.IsNil)
                    return;
                var type = metadata.GetTypeDefinition(handle);
                if (type.GetDeclaringType() != readIn || !IsStateMachineClass(handle) || !visitedTypes.Add(handle))
                    return;
                foreach (var nestedMethod in type.GetMethods())
                {
                    var nested = module.GetDefinition(nestedMethod);
                    if (nested.AccessorOwner is null && !nested.HasAttribute(KnownAttribute.DebuggerHidden))
                        Enqueue(nestedMethod);
                }
            }

            // A generated class that implements the interface the runtime drives an iterator
            // or an async method through.
            bool IsStateMachineClass(TypeDefinitionHandle handle)
            {
                if (_stateMachineClasses.TryGetValue(handle, out var stateMachine))
                    return stateMachine;
                stateMachine = CompilerGeneratedFilter.IsCompilerGenerated(module.GetDefinition(handle))
                    && metadata.GetTypeDefinition(handle).GetInterfaceImplementations().Any(implementation =>
                        metadata.GetInterfaceImplementation(implementation).Interface.GetFullTypeName(metadata).ReflectionName
                            is "System.Collections.IEnumerator" or "System.Runtime.CompilerServices.IAsyncStateMachine");
                return _stateMachineClasses[handle] = stateMachine;
            }

            // A generated method this module defines joins, and the class that declares the method
            // may join with it.
            void VisitMethod(EntityHandle token)
            {
                var handle = MethodOf(token);
                if (handle.IsNil)
                    return;
                if (!_joinsUnit.TryGetValue(handle, out var joins))
                {
                    var target = module.GetDefinition(handle);
                    _joinsUnit[handle] = joins = CompilerGeneratedFilter.IsCompilerGenerated(target)
                        || CompilerGeneratedFilter.IsBurstManagedCopy(target);
                }
                if (joins)
                    Enqueue(handle);
                VisitType(metadata.GetMethodDefinition(handle).GetDeclaringType());
            }

            // A generated class nested in the method's own type joins with all its methods.
            void VisitType(TypeDefinitionHandle handle)
            {
                if (handle.IsNil || !visitedTypes.Add(handle))
                    return;
                if (!_classOwners.TryGetValue(handle, out var classOwner))
                {
                    var type = module.GetDefinition(handle);
                    _classOwners[handle] = classOwner =
                        type.DeclaringTypeDefinition is null || type.Name == "<>c"
                        || !CompilerGeneratedFilter.IsCompilerGenerated(type)
                            ? null
                            : OwnerOf(type);
                }
                if (classOwner is null || !classOwner.Equals(owner))
                    return;
                foreach (var nestedMethod in metadata.GetTypeDefinition(handle).GetMethods())
                    Enqueue(nestedMethod);
            }
        }
    }

    /// <summary>
    /// The method a token names, when this module defines it. A reference to a method of a
    /// generic type is looked up by name, which is unique for a generated method, and is not
    /// followed for any other. Called under <c>_decompilerLock</c>.
    /// </summary>
    private MethodDefinitionHandle MethodOf(EntityHandle token)
    {
        switch (token.Kind)
        {
            case HandleKind.MethodDefinition:
                return (MethodDefinitionHandle)token;
            case HandleKind.MethodSpecification:
                return MethodOf(_peFile.Metadata.GetMethodSpecification((MethodSpecificationHandle)token).Method);
            case HandleKind.MemberReference:
                var referenceHandle = (MemberReferenceHandle)token;
                if (!_referencedMethods.TryGetValue(referenceHandle, out var referenced))
                    _referencedMethods[referenceHandle] = referenced = ReferencedMethod(referenceHandle);
                return referenced;
            default:
                return default;
        }
    }

    private MethodDefinitionHandle ReferencedMethod(MemberReferenceHandle handle)
    {
        var metadata = _peFile.Metadata;
        var reference = metadata.GetMemberReference(handle);
        if (reference.GetKind() != MemberReferenceKind.Method)
            return default;
        var type = TypeOf(reference.Parent);
        if (type.IsNil)
            return default;
        var name = metadata.GetString(reference.Name);
        if (!name.StartsWith('<')
            && !CompilerGeneratedFilter.IsCompilerGenerated(((MetadataModule)TypeSystem.MainModule).GetDefinition(type)))
            return default;
        foreach (var candidate in metadata.GetTypeDefinition(type).GetMethods())
            if (metadata.StringComparer.Equals(metadata.GetMethodDefinition(candidate).Name, name))
                return candidate;
        return default;
    }

    private TypeDefinitionHandle FieldOwner(EntityHandle token)
    {
        var metadata = _peFile.Metadata;
        return token.Kind switch
        {
            HandleKind.FieldDefinition => metadata.GetFieldDefinition((FieldDefinitionHandle)token).GetDeclaringType(),
            HandleKind.MemberReference
                when metadata.GetMemberReference((MemberReferenceHandle)token).GetKind() == MemberReferenceKind.Field
                => TypeOf(metadata.GetMemberReference((MemberReferenceHandle)token).Parent),
            _ => default,
        };
    }

    /// <summary>
    /// The type a token names, when this module defines it: the type itself, or the generic
    /// type an instantiation of it is built from. Called under <c>_decompilerLock</c>.
    /// </summary>
    private TypeDefinitionHandle TypeOf(EntityHandle token)
    {
        switch (token.Kind)
        {
            case HandleKind.TypeDefinition:
                return (TypeDefinitionHandle)token;
            case HandleKind.TypeSpecification:
                var specification = (TypeSpecificationHandle)token;
                if (!_instantiatedTypes.TryGetValue(specification, out var instantiated))
                    _instantiatedTypes[specification] = instantiated = InstantiatedType(specification);
                return instantiated;
            default:
                return default;
        }
    }

    private TypeDefinitionHandle InstantiatedType(TypeSpecificationHandle handle)
    {
        var metadata = _peFile.Metadata;
        var blob = metadata.GetBlobReader(metadata.GetTypeSpecification(handle).Signature);
        if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance
            || blob.ReadSignatureTypeCode() != SignatureTypeCode.TypeHandle)
            return default;
        var generic = blob.ReadTypeHandle();
        return generic.Kind == HandleKind.TypeDefinition ? (TypeDefinitionHandle)generic : default;
    }

    /// <summary>
    /// A method that only passes a call on, which the code that calls it stands for.
    /// </summary>
    /// <param name="Callers">
    /// The bodies that call the proxy from a place where the decompiler shows the proxy's call
    /// instead, each with the number of such calls.
    /// </param>
    /// <param name="AddressTaken">
    /// True when an instruction takes the proxy's address or its token. The decompiler then
    /// still shows the proxy's own code, as a lambda or a local function.
    /// </param>
    private sealed record Proxy(IReadOnlyList<(MethodDefinitionHandle Body, int Calls)> Callers, bool AddressTaken);

    /// <summary>
    /// The proxies of the assembly: each method the compiler generated that only passes its
    /// arguments on to one call, and that some code calls. Called under <c>_decompilerLock</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The C# compiler adds such a method, named <c>&lt;&gt;n__0</c> and so on, for a call through
    /// <c>base.</c> in code it moves into another class: a lambda that captures a local, an
    /// iterator and an async method. From there the call cannot be made, so the moved code
    /// calls the added method, which makes it. A local function that only passes a call on has
    /// the same shape. The decompiler shows a call of such a method as the call the method
    /// makes, <c>base.Open(width)</c>, and the method itself nowhere. Read as the compiler
    /// emitted it, the proxy would be a member of its own that nobody wrote: the member the
    /// call is written in would not be a user of the base method, and the base method not
    /// among what that member uses.
    /// </para>
    /// <para>
    /// A method is a proxy by the test of ILSpy's <c>ProxyCallReplacer</c> (read in
    /// ICSharpCode.Decompiler 9.1.0.7988), so that what is counted here is what the decompiled
    /// code shows: an instance method the compiler generated whose body passes <c>this</c> and
    /// its parameters, in order, to one non-virtual call of an instance method that is no
    /// constructor and takes as many parameters, and a call of it counts when the calling code
    /// lies in the proxy's class or in a class nested in it. The test goes by this shape, not
    /// by the method's name. Where it and ILSpy's ever disagree, <c>call_sites</c> shows it:
    /// the calls counted and the calls found in the decompiled code then differ.
    /// </para>
    /// </remarks>
    private Dictionary<MethodDefinitionHandle, Proxy> Proxies()
    {
        if (_proxies is not null)
            return _proxies;

        var metadata = _peFile.Metadata;
        var shaped = metadata.MethodDefinitions.Where(PassesOneCallOn).ToHashSet();
        if (shaped.Count == 0)
            return _proxies = [];

        var callers = new Dictionary<MethodDefinitionHandle, List<(MethodDefinitionHandle Body, int Calls)>>();
        var addressTaken = new HashSet<MethodDefinitionHandle>();
        foreach (var handle in metadata.MethodDefinitions)
        {
            var definition = metadata.GetMethodDefinition(handle);
            if (definition.RelativeVirtualAddress == 0)
                continue;

            Dictionary<MethodDefinitionHandle, int> calls = null;
            try
            {
                var reader = _peFile.GetMethodBody(definition.RelativeVirtualAddress).GetILReader();
                while (reader.RemainingBytes > 0)
                {
                    var opCode = reader.DecodeOpCode();
                    switch (opCode)
                    {
                        case ILOpCode.Call or ILOpCode.Callvirt:
                            var called = MethodOf(MetadataTokenHelpers.EntityHandleOrNil(reader.ReadInt32()));
                            if (!called.IsNil && shaped.Contains(called)
                                && LiesIn(definition.GetDeclaringType(), metadata.GetMethodDefinition(called).GetDeclaringType()))
                            {
                                calls ??= [];
                                calls[called] = calls.GetValueOrDefault(called) + 1;
                            }
                            break;
                        case ILOpCode.Ldftn or ILOpCode.Ldvirtftn or ILOpCode.Ldtoken:
                            var taken = MethodOf(MetadataTokenHelpers.EntityHandleOrNil(reader.ReadInt32()));
                            if (!taken.IsNil && shaped.Contains(taken))
                                addressTaken.Add(taken);
                            break;
                        default:
                            reader.SkipOperand(opCode);
                            break;
                    }
                }
            }
            catch (BadImageFormatException)
            {
                // What the body calls up to the point where it breaks off counts.
            }

            if (calls is null)
                continue;
            foreach (var (called, count) in calls)
            {
                if (!callers.TryGetValue(called, out var calling))
                    callers[called] = calling = [];
                calling.Add((handle, count));
            }
        }
        return _proxies = callers.ToDictionary(
            entry => entry.Key, entry => new Proxy(entry.Value, addressTaken.Contains(entry.Key)));

        // True when type is container or is nested in it, at any depth.
        bool LiesIn(TypeDefinitionHandle type, TypeDefinitionHandle container)
        {
            for (; !type.IsNil; type = metadata.GetTypeDefinition(type).GetDeclaringType())
            {
                if (type == container)
                    return true;
            }
            return false;
        }
    }

    /// <summary>
    /// True for a method with the shape of a proxy (see <see cref="Proxies"/>): the compiler
    /// generated it, and its body loads <c>this</c> and each parameter in order, makes one
    /// non-virtual call of an instance method with as many parameters that is no constructor,
    /// and returns, directly or through a local as a debug build writes it.
    /// </summary>
    private bool PassesOneCallOn(MethodDefinitionHandle handle)
    {
        var metadata = _peFile.Metadata;
        var definition = metadata.GetMethodDefinition(handle);
        if ((definition.Attributes & System.Reflection.MethodAttributes.Static) != 0
            || definition.RelativeVirtualAddress == 0)
            return false;
        try
        {
            var reader = _peFile.GetMethodBody(definition.RelativeVirtualAddress).GetILReader();
            var parameters = ParameterCount(definition.Signature);
            var loaded = 0;
            var opCode = Next(ref reader);
            while (ArgumentLoaded(opCode, ref reader) is { } index)
            {
                if (index != loaded++)
                    return false;
                opCode = Next(ref reader);
            }
            if (loaded != parameters + 1 || opCode != ILOpCode.Call
                || !IsInstanceCallWith(MetadataTokenHelpers.EntityHandleOrNil(reader.ReadInt32()), parameters))
                return false;

            opCode = Next(ref reader);
            if (opCode is >= ILOpCode.Stloc_0 and <= ILOpCode.Stloc_3 or ILOpCode.Stloc_s or ILOpCode.Stloc)
            {
                // A debug build stores the result, jumps to the next instruction and loads it.
                reader.SkipOperand(opCode);
                opCode = Next(ref reader);
                if (opCode is ILOpCode.Br_s or ILOpCode.Br)
                {
                    if ((opCode == ILOpCode.Br_s ? reader.ReadSByte() : reader.ReadInt32()) != 0)
                        return false;
                    opCode = Next(ref reader);
                }
                if (opCode is not (>= ILOpCode.Ldloc_0 and <= ILOpCode.Ldloc_3 or ILOpCode.Ldloc_s or ILOpCode.Ldloc))
                    return false;
                reader.SkipOperand(opCode);
                opCode = Next(ref reader);
            }
            return opCode == ILOpCode.Ret && reader.RemainingBytes == 0
                && handle.IsCompilerGeneratedOrIsInCompilerGeneratedClass(metadata);
        }
        catch (BadImageFormatException)
        {
            return false;
        }

        // The next instruction that is no nop, or a nop at the body's end.
        static ILOpCode Next(ref BlobReader reader)
        {
            while (reader.RemainingBytes > 0)
            {
                var opCode = reader.DecodeOpCode();
                if (opCode != ILOpCode.Nop)
                    return opCode;
            }
            return ILOpCode.Nop;
        }

        // The index of the argument an instruction loads, or null for any other instruction.
        static int? ArgumentLoaded(ILOpCode opCode, ref BlobReader reader) => opCode switch
        {
            >= ILOpCode.Ldarg_0 and <= ILOpCode.Ldarg_3 => opCode - ILOpCode.Ldarg_0,
            ILOpCode.Ldarg_s => reader.ReadByte(),
            ILOpCode.Ldarg => reader.ReadUInt16(),
            _ => null,
        };

        int ParameterCount(BlobHandle signature)
        {
            var blob = metadata.GetBlobReader(signature);
            if (blob.ReadSignatureHeader().IsGeneric)
                blob.ReadCompressedInteger();
            return blob.ReadCompressedInteger();
        }

        bool IsInstanceCallWith(EntityHandle token, int parameters)
        {
            if (token.Kind == HandleKind.MethodSpecification)
                token = metadata.GetMethodSpecification((MethodSpecificationHandle)token).Method;
            BlobHandle signature;
            StringHandle name;
            switch (token.Kind)
            {
                case HandleKind.MethodDefinition:
                    var called = metadata.GetMethodDefinition((MethodDefinitionHandle)token);
                    (signature, name) = (called.Signature, called.Name);
                    break;
                case HandleKind.MemberReference:
                    var reference = metadata.GetMemberReference((MemberReferenceHandle)token);
                    (signature, name) = (reference.Signature, reference.Name);
                    break;
                default:
                    return false;
            }
            return metadata.GetBlobReader(signature).ReadSignatureHeader().IsInstance
                && !metadata.StringComparer.Equals(name, ".ctor")
                && ParameterCount(signature) == parameters;
        }
    }

    /// <summary>
    /// The bodies whose decompiled code shows the code of <paramref name="body"/>: the body
    /// itself, and for a proxy the bodies that call it, with the proxy itself only when its
    /// address is taken (see <see cref="Proxies"/>). Called under <c>_decompilerLock</c>.
    /// </summary>
    private IEnumerable<MethodDefinitionHandle> BodiesShowing(MethodDefinitionHandle body)
    {
        if (!Proxies().TryGetValue(body, out var proxy))
            return [body];
        var callers = proxy.Callers.Select(calling => calling.Body);
        return proxy.AddressTaken ? callers.Prepend(body) : callers;
    }

    /// <summary>
    /// The type whose code <paramref name="type"/> was generated for: the type itself unless the
    /// compiler generated it, and otherwise the nearest type around it that it did not generate.
    /// </summary>
    private static ITypeDefinition OwnerOf(ITypeDefinition type)
    {
        var owner = type;
        while (owner.DeclaringTypeDefinition is { } outer && CompilerGeneratedFilter.IsCompilerGenerated(owner))
            owner = outer;
        return owner;
    }

    /// <summary>
    /// Decode a type's or method's custom attributes straight from metadata, tolerating the
    /// cross-assembly-enum failure that empties ILSpy's high-level
    /// <c>IAttribute.FixedArguments</c> (see <see cref="LenientAttributeReader"/>).
    /// Returns an empty list for any other symbol, and when the symbol carries no real
    /// metadata handle.
    /// </summary>
    public IReadOnlyList<LenientAttributeReader.DecodedAttribute> ReadCustomAttributesLenient(
        IEntity entity)
    {
        var metadata = _peFile.Metadata;
        var token = entity.MetadataToken;
        if (token.IsNil)
            return Array.Empty<LenientAttributeReader.DecodedAttribute>();
        return token.Kind switch
        {
            HandleKind.TypeDefinition => LenientAttributeReader.ReadAttributes(
                metadata, metadata.GetTypeDefinition((TypeDefinitionHandle)token).GetCustomAttributes()),
            HandleKind.MethodDefinition => LenientAttributeReader.ReadAttributes(
                metadata, metadata.GetMethodDefinition((MethodDefinitionHandle)token).GetCustomAttributes()),
            _ => Array.Empty<LenientAttributeReader.DecodedAttribute>(),
        };
    }

    /// <summary>
    /// Enumerate every type in the main module that implements <paramref name="interfaceType"/>,
    /// directly or transitively. Synthesized in-process because ILSpyX ships member-level
    /// <c>Implemented By</c> analyzers (per method, property, event) but no type-level
    /// equivalent. Includes derived interfaces alongside concrete classes and structs —
    /// they are also "types whose base set contains this interface", which is the most
    /// useful framing for the cross-ref question.
    /// </summary>
    public IReadOnlyList<ISymbol> FindImplementingTypes(ITypeDefinition interfaceType)
    {
        // ReflectionName, not FullName: it carries the generic arity, so IComparable and
        // IComparable<T> are told apart.
        var target = interfaceType.ReflectionName;
        var results = new List<ISymbol>();
        foreach (var type in TypeSystem.MainModule.TypeDefinitions)
        {
            // Equals, not ==: the type system is uncached, so every lookup hands out a new
            // object for the same type, and a reference comparison never matches.
            if (type.Equals(interfaceType))
                continue;
            if (type.GetAllBaseTypeDefinitions().Any(b => b.ReflectionName == target))
                results.Add(type);
        }
        return results;
    }

    /// <summary>
    /// The extension methods of this assembly whose receiver is <paramref name="type"/> or a
    /// construction of it, passed by value or by reference, or a type parameter whose constraints
    /// name it (<see cref="TypeWalker.ListedUnder"/>), from the resolver's table, which takes a
    /// Visual Basic module's and an F# class's as well (<see cref="SymbolResolver.ExtensionMethods"/>).
    /// A method for a base type or an interface of the type is not listed: the list names the
    /// methods written for exactly this type, and the lookup by name finds the others.
    /// </summary>
    /// <remarks>
    /// ILSpyX's "Extension Methods" analyzer is not run. It looks only in the classes ILSpy marks
    /// as holding extension methods, the static ones, and compares the receiver's definition,
    /// which a receiver passed by reference does not have.
    /// </remarks>
    public IReadOnlyList<ISymbol> FindExtensionMethods(ITypeDefinition type)
    {
        var module = (MetadataModule)TypeSystem.MainModule;
        return Resolver.ExtensionMethods
            .Select(handle => module.GetDefinition(handle))
            // Equals, not ==: the type system is uncached, and Equals checks the metadata row and file.
            .Where(method => TypeWalker.ListedUnder(method).Any(listed => type.Equals(listed.GetDefinition())))
            .ToList<ISymbol>();
    }

    /// <summary>
    /// The members of this assembly that override <paramref name="member"/>, a method, an
    /// accessor, a property or an event, directly or through further overrides: each member
    /// of its kind and name whose chain of overridden members, read by
    /// <see cref="Overriding"/>, reaches it. An accessor is answered with the accessors that
    /// override it.
    /// </summary>
    /// <remarks>
    /// ILSpyX's "Overridden By" analyzers are not asked. They take a member for an override by
    /// its flags, which leaves out one that overrides with a more specific return type, and
    /// they list it under every base member of its name and parameters, also under one that a
    /// member declared anew between the two hides. They look at a type's methods without its
    /// accessors, so an accessor has no overrides for them.
    /// </remarks>
    public IReadOnlyList<ISymbol> FindOverrides(IMember member)
    {
        var asked = member.MemberDefinition;
        // Most members asked about are not virtual, and nothing overrides one that is not, one
        // that is sealed or one of a class nothing derives from. Without this test every such
        // question walks every type, which takes 87 % more time over the methods of a large
        // assembly than the analyzers do, which are not run for such a member (measured on
        // 2026-10-13).
        if (asked.DeclaringTypeDefinition is not { Kind: not TypeKind.Interface, IsSealed: false } declaring
            || !(asked.IsVirtual || asked.IsAbstract || Overriding.Of(asked))
            || Overriding.IsSealed(asked))
            return [];

        var results = new List<ISymbol>();
        foreach (var type in TypeSystem.MainModule.TypeDefinitions)
        {
            // Equals, not ==: the type system hands out a new object for the same type.
            if (type.Equals(declaring)
                || !type.GetNonInterfaceBaseTypes().Any(baseType => declaring.Equals(baseType.GetDefinition())))
                continue;

            foreach (var candidate in MembersOfKind(type, asked).Where(candidate => candidate.Name == asked.Name))
            {
                for (var overridden = Overriding.Overridden(candidate); overridden is not null;
                     overridden = Overriding.Overridden(overridden))
                {
                    if (overridden.MemberDefinition.Equals(asked))
                    {
                        results.Add(candidate);
                        break;
                    }
                }
            }
        }
        return results;
    }

    /// <summary>
    /// The members <paramref name="type"/> declares that are of the kind <paramref name="like"/>
    /// is: its methods, its accessors, its properties or its events.
    /// </summary>
    private static IEnumerable<IMember> MembersOfKind(ITypeDefinition type, IMember like) => like switch
    {
        IMethod { IsAccessor: true } => type.Properties
            .SelectMany(property => new[] { property.Getter, property.Setter })
            .Concat(type.Events.SelectMany(@event => new[] { @event.AddAccessor, @event.RemoveAccessor, @event.InvokeAccessor }))
            .Where(accessor => accessor is not null),
        IMethod => type.DeclaredMethods(),
        IProperty => type.Properties,
        IEvent => type.Events,
        _ => [],
    };

    /// <summary>
    /// The methods that read, or that write, <paramref name="field"/>, a field this assembly
    /// defines, found by <see cref="FieldAccessScanner"/>: among <paramref name="within"/>, methods
    /// this assembly defines, where it is given, and among all methods otherwise. Thread-safe —
    /// reads method bodies, so the call serializes against the decompiler through
    /// <c>_decompilerLock</c>.
    /// </summary>
    public IReadOnlyList<FieldAccessScanner.Access> FindFieldAccesses(IField field, FieldAccessScanner.AccessKind kind,
        IEnumerable<IMethod> within = null)
    {
        lock (_decompilerLock)
        {
            var module = (MetadataModule)TypeSystem.MainModule;
            var metadata = _peFile.Metadata;
            var definition = field.MemberDefinition.MetadataToken;
            var named = new Dictionary<EntityHandle, bool>();
            var scanner = new FieldAccessScanner(_peFile, module, NamesTheField);
            return within is null
                ? scanner.Find(kind)
                : scanner.Find(kind, within.Select(method => (MethodDefinitionHandle)method.MetadataToken));

            // Code names a field of a generic type through a reference row, Box<T>::Item, even
            // inside the type itself; such a row names the field when it resolves to it.
            bool NamesTheField(EntityHandle token)
            {
                if (token == definition)
                    return true;
                if (token.Kind != HandleKind.MemberReference)
                    return false;
                if (!named.TryGetValue(token, out var names))
                    named[token] = names = ResolvesToTheField(token);
                return names;
            }

            bool ResolvesToTheField(EntityHandle token)
            {
                if (!metadata.StringComparer.Equals(metadata.GetMemberReference((MemberReferenceHandle)token).Name, field.Name))
                    return false;
                try
                {
                    return module.ResolveEntity(token, default) is IField resolved
                        && resolved.MemberDefinition.MetadataToken == definition
                        && resolved.ParentModule == module;
                }
                catch (Exception e) when (e is BadImageFormatException or ArgumentException)
                {
                    return false;
                }
            }
        }
    }

    /// <summary>
    /// Everything that uses <paramref name="type"/>, a type this assembly defines, and what
    /// carries an attribute that may name it (<see cref="TypeUsageScanner.UsersOf"/>).
    /// Thread-safe — resolves metadata through the type system, so the call serializes against
    /// the decompiler through <c>_decompilerLock</c>.
    /// </summary>
    public (IReadOnlyList<ISymbol> Users, IReadOnlyList<AttributeCarriers.Undecided> Undecided) FindTypeUsers(
        ITypeDefinition type)
    {
        lock (_decompilerLock)
        {
            return TypeUsageScanner.UsersOf(_peFile, (MetadataModule)TypeSystem.MainModule, type, BodiesShowing);
        }
    }

    /// <summary>
    /// The assembly and the module, each when it carries an attribute of <paramref name="type"/>
    /// (<see cref="GlobalAttributeTarget.Carrying"/>). Thread-safe — resolves metadata through
    /// the type system, so the call serializes against the decompiler through
    /// <c>_decompilerLock</c>.
    /// </summary>
    public IReadOnlyList<ISymbol> FindGlobalAttributeTargets(ITypeDefinition type)
    {
        lock (_decompilerLock)
        {
            return GlobalAttributeTarget.Carrying((MetadataModule)TypeSystem.MainModule, type);
        }
    }

    /// <summary>
    /// What carries the attributes applied through <paramref name="constructor"/>, a constructor
    /// this assembly defines (<see cref="AttributeCarriers.AppliedThrough"/>). Applying an
    /// attribute uses one of its constructors, and no method body shows that use. Thread-safe —
    /// resolves metadata through the type system, so the call serializes against the decompiler
    /// through <c>_decompilerLock</c>.
    /// </summary>
    public IReadOnlyList<ISymbol> FindAttributeCarriers(IMethod constructor)
    {
        lock (_decompilerLock)
        {
            var module = (MetadataModule)TypeSystem.MainModule;
            return AttributeCarriers.Of(module, AttributeCarriers.AppliedThrough(module, constructor)).ToList();
        }
    }

    /// <summary>
    /// How many attributes are applied through one of <paramref name="constructors"/>, each a
    /// constructor this assembly defines, counting those <see cref="FindAttributeCarriers"/>
    /// names a carrier for. Thread-safe, as that method is.
    /// </summary>
    public int CountAttributeUses(IEnumerable<IMethod> constructors)
    {
        lock (_decompilerLock)
        {
            var module = (MetadataModule)TypeSystem.MainModule;
            return constructors.Sum(constructor =>
                AttributeCarriers.Count(module, AttributeCarriers.AppliedThrough(module, constructor)));
        }
    }

    /// <summary>
    /// What carries the attributes that set <paramref name="member"/>, a field or property
    /// this assembly defines, by name, as in <c>[Label(Text = "x")]</c>
    /// (<see cref="NamedArgumentScan"/>), and what carries those whose bytes did not decide
    /// whether they set it. Such an attribute writes the member, and no method body shows it.
    /// Thread-safe — resolves metadata through the type system, so the call serializes against
    /// the decompiler through <c>_decompilerLock</c>.
    /// </summary>
    public (IReadOnlyList<ISymbol> Carriers, IReadOnlyList<AttributeCarriers.Undecided> Undecided)
        FindNamedArgumentCarriers(IMember member)
    {
        lock (_decompilerLock)
        {
            if (NamedArgumentsFor(member) is not { } scan)
                return ([], []);
            var key = NamedArgumentScan.KeyOf(member);
            return (AttributeCarriers.Of((MetadataModule)TypeSystem.MainModule, scan.Sets(key)).ToList(),
                scan.UndecidedOn(key));
        }
    }

    /// <summary>
    /// How many attributes set one of <paramref name="members"/> by name, each a field or
    /// property this assembly defines, counting those <see cref="FindNamedArgumentCarriers"/>
    /// names a carrier for. Thread-safe, as that method is.
    /// </summary>
    public int CountNamedArgumentUses(IEnumerable<IMember> members)
    {
        lock (_decompilerLock)
        {
            var module = (MetadataModule)TypeSystem.MainModule;
            return members.Sum(member => NamedArgumentsFor(member) is { } scan
                ? AttributeCarriers.Count(module, scan.Sets(NamedArgumentScan.KeyOf(member)))
                : 0);
        }
    }

    /// <summary>
    /// The scan for the attributes that set <paramref name="member"/> by name, or null for a
    /// member no attribute can set that way: one that is static, or whose class is no attribute
    /// class. A class with a base class that could not be read may be one. Called under
    /// <c>_decompilerLock</c>.
    /// </summary>
    private NamedArgumentScan NamedArgumentsFor(IMember member)
    {
        if (member.IsStatic || member is not (IField or IProperty)
            || member.MemberDefinition.DeclaringTypeDefinition is not { } declaring
            || declaring.ParentModule != TypeSystem.MainModule)
            return null;
        var (derives, unread) = DerivesFromAttribute(declaring);
        if (!derives && unread is null)
            return null;
        return NamedArgumentScan.ForMember(
            (MetadataModule)TypeSystem.MainModule, _decompiler.TypeSystem.MainModule, member);
    }

    /// <summary>
    /// Answer a question about a type this assembly references but does not define, from this
    /// assembly's own metadata. Thread-safe — the scan serializes against the decompiler through
    /// <c>_decompilerLock</c>.
    /// </summary>
    public T ScanReferences<T>(SymbolResolver.ExternalTypeName type, Func<TypeUsageScanner, T> question)
    {
        lock (_decompilerLock)
        {
            return question(new TypeUsageScanner(
                _peFile, (MetadataModule)TypeSystem.MainModule, type, _decompiler.TypeSystem.MainModule, BodiesShowing));
        }
    }

    /// <summary>
    /// Release every file this host opened: each assembly in ILSpyX's list, which holds the
    /// inspected one and the references ILSpyX's analyzers loaded, each read whole into memory;
    /// the decompiler's own copy of the inspected assembly; and each referenced assembly the
    /// decompiler resolved. Called by <see cref="AssemblyHostRegistry"/> once it closes the host,
    /// which it does only while no call holds it. Idempotent — a second Dispose call is a no-op.
    /// Calling tool methods on a disposed host is a programming error.
    /// </summary>
    public void Dispose()
    {
        lock (_decompilerLock)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var assembly in _assemblyList.GetAssemblies())
                (assembly.GetMetadataFileOrNull() as IDisposable)?.Dispose();
            (_decompiler.TypeSystem.MainModule.MetadataFile as IDisposable)?.Dispose();
            _references.Dispose();
        }
    }

    /// <summary>
    /// Resolves the assembly's references for the decompiler where the inner resolver finds them,
    /// opening a file only when <see cref="PathGuard.AdmitsReference"/> admits it, and records
    /// every file it opens, so that <see cref="Dispose"/> can close them. The decompiler resolves
    /// the assembly's references, transitively, when it is built, and maps each file it finds,
    /// and nothing else closes them.
    /// </summary>
    private sealed class ReferenceResolver(UniversalAssemblyResolver inner, PathGuard guard)
        : IAssemblyResolver, IDisposable
    {
        private readonly List<MetadataFile> _opened = new();

        public MetadataFile Resolve(IAssemblyReference reference) =>
            Open(() => inner.FindAssemblyFile(reference), () => inner.Resolve(reference));

        public MetadataFile ResolveModule(MetadataFile mainModule, string moduleName) =>
            Open(
                () => Path.GetDirectoryName(mainModule.FileName) is { } directory
                    ? Path.Combine(directory, moduleName)
                    : null,
                () => inner.ResolveModule(mainModule, moduleName));

        public Task<MetadataFile> ResolveAsync(IAssemblyReference reference) =>
            Task.Run(() => Resolve(reference));

        public Task<MetadataFile> ResolveModuleAsync(MetadataFile mainModule, string moduleName) =>
            Task.Run(() => ResolveModule(mainModule, moduleName));

        /// <summary>
        /// The file <paramref name="find"/> names, opened by <paramref name="open"/> when
        /// <see cref="PathGuard.AdmitsReference"/> admits it. A file the process may not read
        /// leaves the reference unresolved, as the inner resolver leaves one it cannot open or
        /// parse: the decompiler does not catch what a resolver throws, so the exception would
        /// stop the host from being built and fail every tool on the assembly.
        /// </summary>
        private MetadataFile Open(Func<string> find, Func<MetadataFile> open)
        {
            try
            {
                return find() is { } file && guard.AdmitsReference(file) ? Track(open()) : null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        private MetadataFile Track(MetadataFile file)
        {
            if (file is not null)
                lock (_opened)
                    _opened.Add(file);
            return file;
        }

        public void Dispose()
        {
            lock (_opened)
            {
                foreach (var file in _opened)
                    (file as IDisposable)?.Dispose();
                _opened.Clear();
            }
        }
    }

    /// <summary>
    /// Resolves a reference to the file <paramref name="loaded"/>, a type system built over
    /// <see cref="ReferenceResolver"/>, holds for it, and opens nothing. A type system built
    /// over this one reads the files that resolver admitted and no others, and has none of its
    /// own to close.
    /// </summary>
    /// <remarks>
    /// A reference is matched by the file's name, as the reference search finds a file, and
    /// failing that by the name of the assembly in it.
    /// </remarks>
    private sealed class LoadedReferences(ICompilation loaded) : IAssemblyResolver
    {
        public MetadataFile Resolve(IAssemblyReference reference)
        {
            var modules = loaded.Modules.OfType<MetadataModule>().ToList();
            var found = modules.FirstOrDefault(module => Named(Path.GetFileNameWithoutExtension(module.MetadataFile.FileName)))
                ?? modules.FirstOrDefault(module => Named(module.AssemblyName));
            return found?.MetadataFile;

            bool Named(string name) => string.Equals(name, reference.Name, StringComparison.OrdinalIgnoreCase);
        }

        public MetadataFile ResolveModule(MetadataFile mainModule, string moduleName) =>
            loaded.Modules.OfType<MetadataModule>()
                .Select(module => module.MetadataFile)
                .FirstOrDefault(file => string.Equals(
                    Path.GetFileName(file.FileName), moduleName, StringComparison.OrdinalIgnoreCase));

        public Task<MetadataFile> ResolveAsync(IAssemblyReference reference) => Task.FromResult(Resolve(reference));

        public Task<MetadataFile> ResolveModuleAsync(MetadataFile mainModule, string moduleName) =>
            Task.FromResult(ResolveModule(mainModule, moduleName));
    }
}
