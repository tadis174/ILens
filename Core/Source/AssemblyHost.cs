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
    private readonly AssemblyList _assemblyList;
    private readonly LoadedAssembly _loadedAssembly;
    private readonly PEFile _peFile;
    private readonly ReferenceResolver _references;
    private bool _disposed;  // guarded by _decompilerLock

    // What MethodUnit learns about a method or type, kept because every method body names the
    // same few thousand ones again; guarded by _decompilerLock.
    private readonly Dictionary<MethodDefinitionHandle, bool> _joinsUnit = new();
    private readonly Dictionary<TypeDefinitionHandle, ITypeDefinition> _classOwners = new();
    private readonly Dictionary<MemberReferenceHandle, MethodDefinitionHandle> _referencedMethods = new();
    private readonly Dictionary<TypeSpecificationHandle, TypeDefinitionHandle> _instantiatedTypes = new();

    /// <summary>
    /// Type system from the LoadedAssembly — symbols from this are compatible
    /// with the ILSpyX analyzer infrastructure.
    /// </summary>
    public ICompilation TypeSystem { get; }

    public string AssemblyPath { get; }

    /// <summary>
    /// Symbol resolver bound to this host's type system. Lazily constructed on
    /// first access. SymbolResolver is stateless, so a race between concurrent
    /// readers costs at most one extra allocation.
    /// </summary>
    public SymbolResolver Resolver => _resolver ??= new SymbolResolver(TypeSystem, ReadExternalBases);
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
        // The resolver looks for a reference by its file name in the assembly's own directory,
        // which its constructor adds, then at the top of each allow-root in the order given, and
        // then among the installed .NET libraries, which it asks first for a .NET Core or .NET
        // Standard target. A mod's game and engine assemblies often sit in an allow-root of their
        // own, and without them the decompiled code has gaps and numbers where enum names belong.
        // The search does not descend into a root: a tree holding several builds of one assembly,
        // such as a game's older versions, would leave the choice to the order of the walk.
        var universal = new UniversalAssemblyResolver(
            AssemblyPath,
            throwOnError: false,
            _peFile.DetectTargetFrameworkId());
        foreach (var root in guard.AllowedRoots)
            universal.AddSearchDirectory(root);
        _references = new ReferenceResolver(universal, guard);

        _decompiler = new CSharpDecompiler(AssemblyPath, _references, _settings);
    }

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
        var module = _decompiler.TypeSystem.MainModule;
        return TypeWalker.WalkExternalBaseTypes(module.GetDefinition((TypeDefinitionHandle)type.MetadataToken));
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
                .Concat(type.Methods.Where(m => m.IsConstructor && m.IsStatic == field.IsStatic).Select(m => m.MetadataToken))
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
    /// Every method body in the assembly that calls one of <paramref name="targets"/>, with the
    /// number of <c>call</c>, <c>callvirt</c> and <c>newobj</c> instructions that call each
    /// target, one entry per body and target. A body here is a method as the compiler emitted
    /// it, so a lambda or an iterator's state machine counts separately from the method it was
    /// written in. Thread-safe.
    /// </summary>
    /// <remarks>
    /// A call is matched to its target statically, by the method the instruction names: a call
    /// made through a base or interface method is a call to that method, not to an override.
    /// A method-group reference (<c>ldftn</c>) is not a call and is not counted.
    /// </remarks>
    public IReadOnlyList<(IMethod Method, IMethod Target, int Calls)> CountCallsTo(IReadOnlyCollection<IMethod> targets)
    {
        var targetsByToken = targets.ToDictionary(t => t.MetadataToken);
        var targetNames = targets.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        lock (_decompilerLock)
        {
            var module = (MetadataModule)TypeSystem.MainModule;
            var metadata = _peFile.Metadata;
            var counts = new List<(IMethod, IMethod, int)>();

            foreach (var handle in metadata.MethodDefinitions)
            {
                var definition = metadata.GetMethodDefinition(handle);
                if (definition.RelativeVirtualAddress == 0)
                    continue;

                Dictionary<EntityHandle, int> calls = null;
                try
                {
                    var reader = _peFile.GetMethodBody(definition.RelativeVirtualAddress).GetILReader();
                    while (reader.RemainingBytes > 0)
                    {
                        var opCode = reader.DecodeOpCode();
                        if (opCode is ILOpCode.Call or ILOpCode.Callvirt or ILOpCode.Newobj)
                        {
                            var target = TargetOf(MetadataTokenHelpers.EntityHandleOrNil(reader.ReadInt32()));
                            if (!target.IsNil)
                            {
                                calls ??= [];
                                calls[target] = calls.GetValueOrDefault(target) + 1;
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
                    // A body the metadata reader cannot decode is skipped, as the ILSpyX
                    // analyzers skip it; its calls then cannot be counted either way.
                    continue;
                }

                if (calls is null)
                    continue;
                var method = module.GetDefinition(handle);
                foreach (var (target, count) in calls)
                    counts.Add((method, targetsByToken[target], count));
            }
            return counts;

            // The token of the target the operand calls, or a nil handle for any other method.
            EntityHandle TargetOf(EntityHandle operand)
            {
                switch (operand.Kind)
                {
                    case HandleKind.MethodDefinition:
                        return targetsByToken.ContainsKey(operand) ? operand : default;
                    case HandleKind.MethodSpecification:
                        return TargetOf(metadata.GetMethodSpecification((MethodSpecificationHandle)operand).Method);
                    case HandleKind.MemberReference:
                        // A call into a method of a generic type goes through a reference. Its name
                        // is a cheap filter; only a candidate that passes it is resolved.
                        var name = metadata.GetString(
                            metadata.GetMemberReference((MemberReferenceHandle)operand).Name);
                        if (!targetNames.Contains(name))
                            return default;
                        try
                        {
                            return module.ResolveEntity(operand, default) is IMember
                                   {
                                       MemberDefinition: { } resolved
                                   }
                                && resolved.ParentModule == module
                                && targetsByToken.ContainsKey(resolved.MetadataToken)
                                ? resolved.MetadataToken
                                : default;
                        }
                        catch (BadImageFormatException)
                        {
                            return default;
                        }
                    default:
                        return default;
                }
            }
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
                    // A body the metadata reader cannot decode is skipped, as CountCallsTo skips it.
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

        var owner = OwnerOf(declaring);
        var candidates = owner.Methods.Where(candidate => !CompilerGeneratedFilter.IsCompilerGenerated(candidate))
            .Concat(owner.Properties.SelectMany(property => new[] { property.Getter, property.Setter }))
            .Concat(owner.Events.SelectMany(@event => new[] { @event.AddAccessor, @event.RemoveAccessor, @event.InvokeAccessor }))
            .Where(candidate => candidate is not null);
        foreach (var candidate in candidates)
        {
            if (!units.TryGetValue(candidate.MetadataToken, out var unit))
                units[candidate.MetadataToken] = unit = MethodUnit(candidate).Select(part => part.MetadataToken).ToHashSet();
            if (unit.Contains(method.MetadataToken))
                return candidate;
        }
        return method;
    }

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
    /// Run an ILSpyX analyzer by header name (e.g., "Overridden By", "Used By").
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
            // Multiple analyzers can share the same header (e.g., "Overridden By" exists
            // for methods, properties, and events). Find the one that accepts this symbol.
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
    /// and report a use inside generated code under the generated method. This is the one
    /// place that attributes it to source, so every incoming kind gives the same answer
    /// whichever analyzer produced it. A source method reported twice this way, once directly
    /// and once through its generated code, is one line in analyze's answer, which prints
    /// results that print alike once. Fields and types the compiler generated pass through
    /// unchanged.
    /// </remarks>
    public IReadOnlyList<ISymbol> AttributeToSource(IReadOnlyList<ISymbol> results)
    {
        lock (_decompilerLock)
        {
            var maps = new Dictionary<(MetadataFile, TypeDefinitionHandle), CodeMappingInfo>();
            return results
                .Select(result => result is IMethod method ? SourceMemberOf(method, maps) : result)
                .ToList();
        }
    }

    private static ISymbol SourceMemberOf(IMethod method,
        Dictionary<(MetadataFile, TypeDefinitionHandle), CodeMappingInfo> maps)
    {
        var source = SourceMethodOf(method, maps);
        return (ISymbol)source.AccessorOwner ?? source;
    }

    /// <summary>
    /// The method <paramref name="method"/> was written in, or <paramref name="method"/>
    /// itself when the compiler did not generate it.
    /// </summary>
    private static IMethod SourceMethodOf(IMethod method,
        Dictionary<(MetadataFile, TypeDefinitionHandle), CodeMappingInfo> maps)
    {
        var token = method.MetadataToken;
        if (method.ParentModule is not MetadataModule module || token.IsNil
            || token.Kind != HandleKind.MethodDefinition
            || method.DeclaringTypeDefinition is not { } declaringType)
            return method;

        // The map covers one user-written type together with the generated types nested in
        // it, and is built for the type that declares the handle it is asked with. So ask with
        // the method's own handle when it sits on the user-written type, as local functions
        // do, and otherwise with the outermost generated type's. Roslyn starts the name of
        // every lambda, local function, closure class and state machine with '<', which no
        // C# identifier can.
        EntityHandle key = token;
        var userType = declaringType;
        while (userType.Name.StartsWith('<'))
        {
            key = userType.MetadataToken;
            userType = userType.DeclaringTypeDefinition;
            // A generated type at the top level, such as <PrivateImplementationDetails>,
            // holds no code written inside a method.
            if (userType is null)
                return method;
        }
        if (key == token && !method.Name.StartsWith('<'))
            return method;

        var cacheKey = (module.MetadataFile, (TypeDefinitionHandle)userType.MetadataToken);
        if (!maps.TryGetValue(cacheKey, out var map))
            maps[cacheKey] = map = CSharpDecompiler.GetCodeMappingInfo(module.MetadataFile, key);

        var parent = map.GetParentMethod((MethodDefinitionHandle)token);
        return parent == (MethodDefinitionHandle)token ? method : module.GetDefinition(parent);
    }

    /// <summary>
    /// Disassemble a method body to normalized IL text. The cross-assembly comparer
    /// builds a method's IL from it together with that of its generated code, for its
    /// verdict and for the IL-form output of <c>compare</c> (text equality = body
    /// equality across rebuilds, since operand tokens render as symbolic names).
    /// Returns <c>null</c> for a method with no body —
    /// abstract, extern, or an interface method. Thread-safe — shares the
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
    public IReadOnlyList<IMethod> MethodUnit(IMethod method)
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

            // The method a token names, when this module defines it. A reference to a method of
            // a generic type is looked up by name, which is unique for a generated method.
            MethodDefinitionHandle MethodOf(EntityHandle token)
            {
                switch (token.Kind)
                {
                    case HandleKind.MethodDefinition:
                        return (MethodDefinitionHandle)token;
                    case HandleKind.MethodSpecification:
                        return MethodOf(metadata.GetMethodSpecification((MethodSpecificationHandle)token).Method);
                    case HandleKind.MemberReference:
                        var referenceHandle = (MemberReferenceHandle)token;
                        if (!_referencedMethods.TryGetValue(referenceHandle, out var referenced))
                            _referencedMethods[referenceHandle] = referenced = ReferencedMethod(referenceHandle);
                        return referenced;
                    default:
                        return default;
                }
            }

            MethodDefinitionHandle ReferencedMethod(MemberReferenceHandle handle)
            {
                var reference = metadata.GetMemberReference(handle);
                if (reference.GetKind() != MemberReferenceKind.Method)
                    return default;
                var type = TypeOf(reference.Parent);
                if (type.IsNil)
                    return default;
                var name = metadata.GetString(reference.Name);
                if (!name.StartsWith('<') && !CompilerGeneratedFilter.IsCompilerGenerated(module.GetDefinition(type)))
                    return default;
                foreach (var candidate in metadata.GetTypeDefinition(type).GetMethods())
                    if (metadata.StringComparer.Equals(metadata.GetMethodDefinition(candidate).Name, name))
                        return candidate;
                return default;
            }

            TypeDefinitionHandle FieldOwner(EntityHandle token) => token.Kind switch
            {
                HandleKind.FieldDefinition => metadata.GetFieldDefinition((FieldDefinitionHandle)token).GetDeclaringType(),
                HandleKind.MemberReference
                    when metadata.GetMemberReference((MemberReferenceHandle)token).GetKind() == MemberReferenceKind.Field
                    => TypeOf(metadata.GetMemberReference((MemberReferenceHandle)token).Parent),
                _ => default,
            };

            // The type a token names, when this module defines it: the type itself, or the
            // generic type an instantiation of it is built from.
            TypeDefinitionHandle TypeOf(EntityHandle token)
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

            TypeDefinitionHandle InstantiatedType(TypeSpecificationHandle handle)
            {
                var blob = metadata.GetBlobReader(metadata.GetTypeSpecification(handle).Signature);
                if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance
                    || blob.ReadSignatureTypeCode() != SignatureTypeCode.TypeHandle)
                    return default;
                var generic = blob.ReadTypeHandle();
                return generic.Kind == HandleKind.TypeDefinition ? (TypeDefinitionHandle)generic : default;
            }
        }
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
    /// The methods that read, or that write, <paramref name="field"/>, a field this assembly
    /// defines, found by <see cref="FieldAccessScanner"/>. Thread-safe — reads method bodies, so
    /// the call serializes against the decompiler through <c>_decompilerLock</c>.
    /// </summary>
    public IReadOnlyList<FieldAccessScanner.Access> FindFieldAccesses(IField field, FieldAccessScanner.AccessKind kind)
    {
        lock (_decompilerLock)
        {
            var module = (MetadataModule)TypeSystem.MainModule;
            var metadata = _peFile.Metadata;
            var definition = field.MemberDefinition.MetadataToken;
            var named = new Dictionary<EntityHandle, bool>();
            return new FieldAccessScanner(_peFile, module, NamesTheField).Find(kind);

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
    /// Answer a question about a type this assembly references but does not define, from this
    /// assembly's own metadata. Thread-safe — the scan serializes against the decompiler through
    /// <c>_decompilerLock</c>.
    /// </summary>
    public T ScanReferences<T>(SymbolResolver.ExternalTypeName type, Func<ReferencedTypeScanner, T> question)
    {
        lock (_decompilerLock)
        {
            return question(new ReferencedTypeScanner(_peFile, (MetadataModule)TypeSystem.MainModule, type));
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
}
