using System;
using System.IO;
using System.Threading;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
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
    /// Release the PEFile and its mapped file handle. Called by
    /// <see cref="AssemblyHostRegistry"/> when this host is evicted from the LRU
    /// cache. Takes <c>_decompilerLock</c> so it waits for any in-flight decompile
    /// to finish before tearing down the underlying file handle. Calling tool
    /// methods on a disposed host is a programming error and will throw via
    /// ICSharpCode internals.
    /// </summary>
    public void Dispose()
    {
        lock (_decompilerLock)
        {
            _peFile.Dispose();
        }
    }
}
