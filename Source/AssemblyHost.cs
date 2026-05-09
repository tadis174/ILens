using System;
using System.IO;
using System.Reflection.Metadata;
using System.Threading;
using System.Xml.Linq;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using ICSharpCode.ILSpyX;
using ICSharpCode.ILSpyX.Abstractions;
using ICSharpCode.ILSpyX.Analyzers;
using ICSharpCode.ILSpyX.Settings;

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
    /// Create an AnalyzerContext for running ILSpyX analyzers.
    /// </summary>
    public AnalyzerContext CreateAnalyzerContext(CancellationToken ct = default)
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
    /// Returns the analyzer results as a list of symbols.
    /// </summary>
    public IReadOnlyList<ISymbol> RunAnalyzer(string header, ISymbol symbol,
        CancellationToken ct = default)
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

    /// <summary>
    /// Minimal ISettingsProvider stub for headless ILSpyX usage.
    /// </summary>
    private sealed class StubSettingsProvider : ISettingsProvider
    {
        private readonly XElement _root = new("Settings");

        public XElement this[XName section]
        {
            get
            {
                var el = _root.Element(section);
                if (el == null)
                {
                    el = new XElement(section);
                    _root.Add(el);
                }
                return el;
            }
        }

        public void Update(Action<XElement> action) => action(_root);
        public void SaveSettings(XElement section) { }
    }

    /// <summary>
    /// Minimal ILanguage stub. Analyzers don't call most of these methods,
    /// but the interface must be satisfied for AnalyzerContext.
    /// </summary>
    private sealed class StubLanguage : ILanguage
    {
        public bool ShowMember(IEntity member) => true;

        public CodeMappingInfo GetCodeMappingInfo(MetadataFile module, EntityHandle member)
            => member.Kind == HandleKind.TypeDefinition
                ? new(module, (TypeDefinitionHandle)member)
                : new(module, default(TypeDefinitionHandle));

        public string GetEntityName(MetadataFile module, EntityHandle handle,
            bool fullName, bool omitGenerics)
            => "";

        public string GetTooltip(IEntity entity) => entity.FullName;

        public string TypeToString(IType type, bool includeNamespace)
            => includeNamespace ? type.FullName : type.Name;

        public string MethodToString(IMethod method, bool includeDeclaringTypeName,
            bool includeNamespace, bool includeNamespaceOfDeclaringTypeName)
            => method.FullName;

        public string FieldToString(IField field, bool includeDeclaringTypeName,
            bool includeNamespace, bool includeNamespaceOfDeclaringTypeName)
            => field.FullName;

        public string PropertyToString(IProperty property, bool includeDeclaringTypeName,
            bool includeNamespace, bool includeNamespaceOfDeclaringTypeName)
            => property.FullName;

        public string EventToString(IEvent @event, bool includeDeclaringTypeName,
            bool includeNamespace, bool includeNamespaceOfDeclaringTypeName)
            => @event.FullName;
    }
}
