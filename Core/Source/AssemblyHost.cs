using System;
using System.IO;
using System.Reflection.Metadata;
using System.Threading;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
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
    private readonly CSharpDecompiler _decompiler;
    private readonly AssemblyList _assemblyList;
    private readonly LoadedAssembly _loadedAssembly;
    private readonly PEFile _peFile;
    private bool _disposed;  // guarded by _decompilerLock

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
    public SymbolResolver Resolver => _resolver ??= new SymbolResolver(TypeSystem);
    private SymbolResolver _resolver;

    public AssemblyHost(string assemblyPath)
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
        // _peFile is held as a field so Dispose() can release the mapped file handle
        // when the registry evicts this host from the LRU cache.
        _peFile = _loadedAssembly.GetMetadataFileOrNull() as PEFile
            ?? throw new InvalidOperationException(
                $"Failed to load PE file from {AssemblyPath}.");
        var resolver = new UniversalAssemblyResolver(
            AssemblyPath,
            throwOnError: false,
            _peFile.DetectTargetFrameworkId());
        resolver.AddSearchDirectory(Path.GetDirectoryName(AssemblyPath)!);

        var settings = new DecompilerSettings(
            ICSharpCode.Decompiler.CSharp.LanguageVersion.CSharp11_0)
        {
            ThrowOnAssemblyResolveErrors = false
        };
        _decompiler = new CSharpDecompiler(AssemblyPath, resolver, settings);
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
    public string DecompileMethod(IMethod method)
    {
        lock (_decompilerLock)
        {
            return _decompiler.DecompileAsString(method.MetadataToken);
        }
    }

    /// <summary>
    /// Decompile a property to its full C# declaration (signature plus both accessor
    /// bodies). Thread-safe. ILSpy's decompiler accepts any entity handle, so the
    /// property's metadata token routes to the same path as <see cref="DecompileMethod"/>
    /// but emits proper <c>{ get; set; }</c> syntax.
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
    /// Decompile a type and return the syntax tree for further processing. Thread-safe.
    /// </summary>
    public ICSharpCode.Decompiler.CSharp.Syntax.SyntaxTree DecompileTypeSyntaxTree(
        ITypeDefinition type)
    {
        lock (_decompilerLock)
        {
            return _decompiler.DecompileType(type.FullTypeName);
        }
    }

    /// <summary>
    /// Build an AnalyzerContext for the ILSpyX analyzer infrastructure.
    /// Private — callers go through <see cref="RunAnalyzer"/>, which serializes
    /// the analyzer call against decompile/Dispose via <c>_decompilerLock</c>.
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
    /// decompile and Dispose through <c>_decompilerLock</c>.
    /// </summary>
    public IReadOnlyList<ISymbol> RunAnalyzer(string header, ISymbol symbol,
        CancellationToken ct = default)
    {
        lock (_decompilerLock)
        {
            // Multiple analyzers can share the same header (e.g., "Overridden By" exists
            // for methods, properties, and events). Find the one that accepts this symbol.
            foreach (var (attr, analyzerType) in ExportAnalyzerAttribute.GetAnnotatedAnalyzers()
                .Where(a => a.AttributeData.Header == header))
            {
                var analyzer = (IAnalyzer)Activator.CreateInstance(analyzerType)!;
                if (analyzer.Show(symbol))
                {
                    var context = CreateAnalyzerContext(ct);
                    return analyzer.Analyze(symbol, context).ToList();
                }
            }

            return Array.Empty<ISymbol>();
        }
    }

    /// <summary>
    /// Disassemble a method body to normalized IL text. Used by the cross-assembly
    /// comparer (text equality = body equality across rebuilds, since operand
    /// tokens render as symbolic names) and as the IL-form output of
    /// <c>compare_method</c>. Returns <c>null</c> for a method with no body —
    /// abstract, extern, or an interface method. Thread-safe — shares the
    /// per-host <c>_decompilerLock</c> with the C# decompiler so the underlying
    /// PE handle cannot be torn down mid-walk.
    ///
    /// Strips the disassembler's leading <c>// Method begins at RVA 0xNNNN</c>
    /// comment line — it's file-position metadata, not body content, and varies
    /// between rebuilds whenever anything earlier in the assembly shifts. Without
    /// this strip every method-body comparison across two real builds would
    /// false-positive on RVA-only differences (see TASK-21).
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
    /// Decode a type's custom attributes straight from metadata, tolerating the
    /// cross-assembly-enum failure that empties ILSpy's high-level
    /// <c>IAttribute.FixedArguments</c> (see <see cref="LenientAttributeReader"/>).
    /// Returns an empty list when the symbol carries no real metadata handle.
    /// </summary>
    public IReadOnlyList<LenientAttributeReader.DecodedAttribute> ReadCustomAttributesLenient(
        ITypeDefinition type)
    {
        var token = type.MetadataToken;
        if (token.IsNil || token.Kind != HandleKind.TypeDefinition)
            return Array.Empty<LenientAttributeReader.DecodedAttribute>();
        return LenientAttributeReader.ReadTypeAttributes(
            _peFile.Metadata, (TypeDefinitionHandle)token);
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
        var target = interfaceType.FullName;
        var results = new List<ISymbol>();
        foreach (var type in TypeSystem.MainModule.TypeDefinitions)
        {
            if (type == interfaceType)
                continue;
            if (type.GetAllBaseTypeDefinitions().Any(b => b.FullName == target))
                results.Add(type);
        }
        return results;
    }

    /// <summary>
    /// Release the PEFile and its mapped file handle. Called by
    /// <see cref="AssemblyHostRegistry"/> when this host is evicted from the LRU
    /// cache. Takes <c>_decompilerLock</c> so it waits for any in-flight decompile
    /// to finish before tearing down the underlying file handle. Idempotent — a
    /// second Dispose call is a no-op. Calling tool methods on a disposed host
    /// is a programming error and will throw via ICSharpCode internals.
    /// </summary>
    public void Dispose()
    {
        lock (_decompilerLock)
        {
            if (_disposed) return;
            _disposed = true;
            _peFile.Dispose();
        }
    }
}
