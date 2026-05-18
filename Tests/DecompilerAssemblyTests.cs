using System.Reflection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests: spawn the real ILens MCP server and exercise its tools
/// against ICSharpCode.Decompiler.dll. Assertions check the <em>shape</em> of
/// each response, not exact contents — the inspected assembly's API drifts
/// across versions, but the tool contract (call in, useful text out) does not.
/// </summary>
public sealed class DecompilerAssemblyTests : IClassFixture<ILensServerFixture>
{
    private const BindingFlags PublicDeclared = BindingFlags.Public | BindingFlags.Instance
        | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private readonly ILensServerFixture _server;

    public DecompilerAssemblyTests(ILensServerFixture server) => _server = server;

    [Fact]
    public async Task ListAllowedRoots_ReportsAConfiguredRoot()
    {
        string text = await CallToolText("list_allowed_roots", new());
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.DoesNotContain("No allowed roots", text);
    }

    [Fact]
    public async Task ListTypes_OnAPopulatedNamespace_ReturnsTypes()
    {
        string text = await CallToolText("list_types", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["namespaceName"] = "ICSharpCode.Decompiler.TypeSystem",
        });
        Assert.DoesNotContain("No types in namespace", text);
        Assert.Contains("ICSharpCode.Decompiler.TypeSystem.", text);
    }

    [Fact]
    public async Task SearchTypes_BySubstring_FindsMatches()
    {
        string text = await CallToolText("search_types", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["pattern"] = "Decompiler",
        });
        Assert.DoesNotContain("No types match", text);
        Assert.Contains("Decompiler", text);
    }

    [Fact]
    public async Task FindMethods_ByNamePattern_ReturnsMatches()
    {
        string text = await CallToolText("find_methods", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["namePattern"] = "Decompile",
        });
        Assert.DoesNotContain("No methods match", text);
    }

    [Fact]
    public async Task DecompileType_OnAKnownType_YieldsPlausibleCSharp()
    {
        string text = await CallToolText("decompile_type", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["typeName"] = "ICSharpCode.Decompiler.CSharp.CSharpDecompiler",
        });
        Assert.Contains("CSharpDecompiler", text);
        Assert.Contains("class", text);
    }

    [Fact]
    public async Task Analyze_UsedBy_OnAKnownType_DoesNotError()
    {
        string text = await CallToolText("analyze", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["typeName"] = "ICSharpCode.Decompiler.CSharp.CSharpDecompiler",
            ["kind"] = "UsedBy",
        });
        // Shape only — a non-empty report, no exception. The cross-references
        // themselves drift with the assembly.
        Assert.False(string.IsNullOrWhiteSpace(text));
    }

    [Fact]
    public async Task DecompileMethod_OnAPropertyGetter_ReturnsAccessorBody()
    {
        // ThrowOnAssemblyResolveErrors is a stable public property on
        // DecompilerSettings — our own AssemblyHost.cs uses it, so the test target
        // is build-guaranteed to expose the get_ accessor.
        string text = await CallToolText("decompile_method", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["typeName"] = "ICSharpCode.Decompiler.DecompilerSettings",
            ["methodName"] = "get_ThrowOnAssemblyResolveErrors",
        });
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.Contains("ThrowOnAssemblyResolveErrors", text);
        // A header alone would satisfy the name check above — require a brace so
        // we know the decompiler actually emitted a property/method body.
        Assert.Contains("{", text);
    }

    [Fact]
    public async Task FindMethods_DoesNotEmitPropertyAccessors()
    {
        // Mirror of the decompile test above: find_methods deliberately hides
        // accessors from generic browsing, so a name-pattern search for a known
        // accessor should report zero matches even though decompile_method finds it.
        string text = await CallToolText("find_methods", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["namePattern"] = "get_ThrowOnAssemblyResolveErrors",
        });
        Assert.Contains("No methods match", text);
    }

    [Fact]
    public async Task ListTypes_OnAPathOutsideTheAllowedRoots_ReturnsError()
    {
        var arguments = new Dictionary<string, object?>
        {
            ["assembly"] = @"C:\Windows\System32\kernel32.dll",
            ["namespaceName"] = "System",
        };
        CallToolResult result = await _server.Client.CallToolAsync("list_types", arguments);

        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.True(result.IsError, $"Expected an error for an out-of-root path, got: {text}");
        Assert.Contains("allowed root", text);
    }

    [Fact]
    public async Task PathGuard_RejectsSymlinkThatEscapesAllowedRoot()
    {
        // PathGuard's lexical check accepts the symlink (it lives under the
        // allow-root); the reparse-point pass then resolves it and rejects the
        // escape. Creating a symbolic link on Windows requires admin or
        // Developer Mode — silently pass if the environment doesn't allow it,
        // so a stricter CI sandbox doesn't fail this suite.
        string allowRoot = Path.GetDirectoryName(_server.AssemblyPath)!;
        string outsideDir = Path.Combine(Path.GetTempPath(),
            "ilens-symlink-test-" + Guid.NewGuid().ToString("N"));
        string outsideFile = Path.Combine(outsideDir, "outside.dll");
        string symlinkInside = Path.Combine(allowRoot,
            "escape-link-" + Guid.NewGuid().ToString("N") + ".dll");

        Directory.CreateDirectory(outsideDir);
        File.Copy(_server.AssemblyPath, outsideFile);
        try
        {
            try
            {
                File.CreateSymbolicLink(symlinkInside, outsideFile);
            }
            catch (UnauthorizedAccessException) { return; }
            catch (IOException) { return; }

            CallToolResult result = await _server.Client.CallToolAsync("list_types",
                new Dictionary<string, object?>
                {
                    ["assembly"] = symlinkInside,
                    ["namespaceName"] = "ICSharpCode.Decompiler",
                });

            string text = string.Join(
                "\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
            Assert.True(result.IsError,
                $"Expected symlink-escape rejection, got: {text}");
            Assert.Contains("symbolic link", text);
        }
        finally
        {
            try { if (File.Exists(symlinkInside)) File.Delete(symlinkInside); } catch { }
            try { if (Directory.Exists(outsideDir)) Directory.Delete(outsideDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task DecompileMethod_WithParameterTypes_DisambiguatesSameArityOverloads()
    {
        // Find a real same-arity overload group on CSharpDecompiler via reflection,
        // so the test stays valid even if the upstream package adds or removes
        // overloads — instead of hardcoding a signature that drifts.
        Type type = Type.GetType(
            "ICSharpCode.Decompiler.CSharp.CSharpDecompiler, ICSharpCode.Decompiler")
            ?? throw new InvalidOperationException(
                "CSharpDecompiler type not loadable from the referenced ICSharpCode.Decompiler package.");

        var overload = type.GetMethods()
            .GroupBy(m => (m.Name, ArgCount: m.GetParameters().Length))
            .Where(g => g.Count() >= 2)
            .SelectMany(g => g)
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                "No same-arity method overload group found on CSharpDecompiler — " +
                "the test target's API changed; pick a different type.");

        string[] parameterTypeNames = overload.GetParameters()
            .Select(p => p.ParameterType.Name)
            .ToArray();

        string text = await CallToolText("decompile_method", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["typeName"] = type.FullName!,
            ["methodName"] = overload.Name,
            ["parameterCount"] = overload.GetParameters().Length,
            ["parameterTypes"] = parameterTypeNames,
        });

        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.Contains(overload.Name, text);
        // A body brace proves the decompiler emitted source, not just a header.
        Assert.Contains("{", text);
    }

    [Fact]
    public async Task DecompileMethod_WithCSharpKeywordParameterTypes_DisambiguatesSameArityOverloads()
    {
        // Companion to the test above: that one feeds reflection-short-name parameter
        // types ("Boolean", "Int32"); this one feeds the C# keyword form ("bool", "int")
        // — what find_methods and decompile_method's "Available: ..." error message
        // both render. The matcher must accept that form too, since it is what an LLM
        // is most likely to feed back in. The same-arity-overload requirement is
        // essential: SymbolResolver.DisambiguateMethod short-circuits when the candidate
        // set has a single method, so without it the picked method might bypass the
        // matcher entirely and the test would pass without exercising the keyword path.
        // The search scans the whole assembly because no single stable type on the
        // upstream package is guaranteed to host an overload group whose members include
        // a primitive parameter.
        var assembly = typeof(ICSharpCode.Decompiler.CSharp.CSharpDecompiler).Assembly;

        var method = assembly.GetExportedTypes()
            .SelectMany(t => t.GetMethods()
                .Where(m => !m.IsGenericMethod)
                .GroupBy(m => (m.Name, ArgCount: m.GetParameters().Length))
                .Where(g => g.Count() >= 2)
                .SelectMany(g => g))
            .FirstOrDefault(m => m.GetParameters()
                .Any(p => CSharpKeyword(p.ParameterType.Name) != null))
            ?? throw new InvalidOperationException(
                "No non-generic same-arity overload with a keyword-mappable primitive " +
                "parameter found in ICSharpCode.Decompiler.dll — the test target's API " +
                "drifted; pick a different assembly or test shape.");

        Type type = method.DeclaringType!;
        string[] parameterTypeNames = method.GetParameters()
            .Select(p => CSharpKeyword(p.ParameterType.Name) ?? p.ParameterType.Name)
            .ToArray();

        string text = await CallToolText("decompile_method", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["typeName"] = type.FullName!,
            ["methodName"] = method.Name,
            ["parameterCount"] = method.GetParameters().Length,
            ["parameterTypes"] = parameterTypeNames,
        });

        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.Contains(method.Name, text);
    }

    [Fact]
    public async Task FindMethods_WithCSharpKeywordParameterTypes_ReturnsMatches()
    {
        // TypeMatcher backs both decompile_method and find_methods. The keyword-form
        // fix must benefit both consumers. Any reasonably-sized .NET library has
        // methods taking a single string parameter, so "string" must yield matches.
        string text = await CallToolText("find_methods", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["parameterTypes"] = new[] { "string" },
        });
        Assert.DoesNotContain("No methods match", text);
    }

    [Fact]
    public async Task GetOrLoad_RejectsAssemblyLargerThanTotalMemoryBudget()
    {
        // The shared fixture's server uses the default 200 MB budget, which fits
        // the multi-MB test target comfortably. Spawn a transient second server
        // with --max-total-size 1 (1 MB) so the same assembly now exceeds the
        // whole budget on its own — exercising the precondition documented in
        // CLAUDE.md's "Sharp edges" section.
        string allowRoot = Path.GetDirectoryName(_server.AssemblyPath)!;
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "ILens-budget",
            Command = ILensServerFixture.ResolveILensExecutable(),
            Arguments = ["--allow-root", allowRoot, "--max-total-size", "1"],
        });
        await using var tightClient = await McpClient.CreateAsync(transport);

        CallToolResult result = await tightClient.CallToolAsync("list_types",
            new Dictionary<string, object?>
            {
                ["assembly"] = _server.AssemblyPath,
                ["namespaceName"] = "ICSharpCode.Decompiler",
            });

        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        Assert.True(result.IsError,
            $"Expected over-budget rejection, got: {text}");
        Assert.Contains("exceeds", text);
        Assert.Contains("budget", text);
    }

    [Fact]
    public async Task Analyze_ImplementedBy_OnAnInterface_ListsImplementers()
    {
        // ImplementedBy at the type level is synthesized in-process — ILSpyX ships
        // only member-level analyzers for the "Implemented By" header. Discover a
        // suitable interface/implementer pair via reflection so the test stays
        // valid as the upstream package drifts. Restrict to non-generic, non-nested
        // types so the reflection FullName matches ILSpy's ITypeDefinition.FullName.
        var assembly = typeof(ICSharpCode.Decompiler.CSharp.CSharpDecompiler).Assembly;
        var exported = assembly.GetExportedTypes()
            .Where(t => !t.IsGenericType && !t.IsNested)
            .ToArray();

        var pair = exported
            .Where(t => t.IsInterface)
            .Select(iface => new
            {
                Interface = iface,
                Implementer = exported.FirstOrDefault(t =>
                    !t.IsInterface && iface.IsAssignableFrom(t))
            })
            .FirstOrDefault(p => p.Implementer != null)
            ?? throw new InvalidOperationException(
                "No non-generic interface with an in-assembly implementer found " +
                "in ICSharpCode.Decompiler.dll — the test target's API drifted.");

        string text = await CallToolText("analyze", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["typeName"] = pair.Interface.FullName!,
            ["kind"] = "ImplementedBy",
        });

        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.Contains(pair.Implementer!.FullName!, text);
    }

    [Fact]
    public async Task Analyze_ImplementedBy_OnANonInterfaceType_ReturnsError()
    {
        // The other type-kinds (UsedBy, InstantiatedBy, ...) accept any type, but
        // ImplementedBy is meaningful only for interfaces — a class or struct yields
        // an empty result indistinguishable from "implemented nowhere". The guard
        // mirrors AppliedTo's attribute-type check.
        CallToolResult result = await _server.Client.CallToolAsync("analyze",
            new Dictionary<string, object?>
            {
                ["assembly"] = _server.AssemblyPath,
                ["typeName"] = "ICSharpCode.Decompiler.CSharp.CSharpDecompiler",
                ["kind"] = "ImplementedBy",
            });

        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        Assert.True(result.IsError,
            $"Expected ImplementedBy-on-non-interface rejection, got: {text}");
        Assert.Contains("interface", text);
    }

    [Fact]
    public async Task Analyze_AppliedToOnANonAttributeType_ReturnsError()
    {
        // AppliedTo asks "what is this attribute applied to?" — calling it on a
        // non-attribute type would silently yield an empty result that is
        // indistinguishable from a real attribute applied nowhere. AnalyzeTool
        // guards against this with an explicit base-type check.
        CallToolResult result = await _server.Client.CallToolAsync("analyze",
            new Dictionary<string, object?>
            {
                ["assembly"] = _server.AssemblyPath,
                ["typeName"] = "ICSharpCode.Decompiler.CSharp.CSharpDecompiler",
                ["kind"] = "AppliedTo",
            });

        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        Assert.True(result.IsError,
            $"Expected AppliedTo-on-non-attribute rejection, got: {text}");
        Assert.Contains("System.Attribute", text);
    }

    [Fact]
    public async Task DecompileProperty_OnAKnownProperty_ReturnsPropertyDeclaration()
    {
        // ThrowOnAssemblyResolveErrors is the same stable public property that
        // DecompileMethod_OnAPropertyGetter_... targets via the get_ prefix.
        // decompile_property takes the unprefixed name and returns the whole
        // property declaration (signature plus accessor bodies).
        string text = await CallToolText("decompile_property", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["typeName"] = "ICSharpCode.Decompiler.DecompilerSettings",
            ["propertyName"] = "ThrowOnAssemblyResolveErrors",
        });
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.Contains("ThrowOnAssemblyResolveErrors", text);
        Assert.Contains("{", text);
    }

    [Fact]
    public async Task DecompileProperty_OnAReadWriteProperty_ReturnsBothAccessors()
    {
        // Reflection-discover a read-write public property declared on a type in
        // the test target — so the test stays valid as the upstream package drifts.
        // DeclaredOnly avoids inherited properties whose declaring type may live in
        // another assembly the ILens server isn't pointed at.
        var assembly = typeof(ICSharpCode.Decompiler.CSharp.CSharpDecompiler).Assembly;
        var pair = assembly.GetExportedTypes()
            .Where(t => !t.IsGenericType && !t.IsNested)
            .SelectMany(t => t.GetProperties(PublicDeclared)
                .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0)
                .Select(p => new { Type = t, Property = p }))
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                "No read-write public property found in ICSharpCode.Decompiler.dll — " +
                "the test target's API drifted; pick a different shape.");

        string text = await CallToolText("decompile_property", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["typeName"] = pair.Type.FullName!,
            ["propertyName"] = pair.Property.Name,
        });
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.Contains(pair.Property.Name, text);
        // Anchor on the accessor keyword followed by `{` (custom body) or `;`
        // (auto-property) — both are the decompiler's canonical forms.
        Assert.Matches(@"\bget\s*[{;]", text);
        Assert.Matches(@"\bset\s*[{;]", text);
    }

    [Fact]
    public async Task DecompileProperty_OnAMissingProperty_ReturnsError()
    {
        CallToolResult result = await _server.Client.CallToolAsync("decompile_property",
            new Dictionary<string, object?>
            {
                ["assembly"] = _server.AssemblyPath,
                ["typeName"] = "ICSharpCode.Decompiler.DecompilerSettings",
                ["propertyName"] = "ThisPropertyDoesNotExist",
            });

        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        Assert.True(result.IsError, $"Expected not-found rejection, got: {text}");
        Assert.Contains("not found", text);
    }

    [Fact]
    public async Task DecompileProperty_OnAnIndexerWithMultipleOverloads_ReturnsAmbiguityError()
    {
        // Discover a type in the test target with two-or-more indexers. If the
        // upstream package exposes none, the test bails out — the SymbolResolver
        // ambiguity branch then stays exercised only by the resolver change itself,
        // and will become end-to-end-covered as soon as a multi-indexer type lands
        // in the assembly.
        var assembly = typeof(ICSharpCode.Decompiler.CSharp.CSharpDecompiler).Assembly;
        var typeWithMultipleIndexers = assembly.GetExportedTypes()
            .Where(t => !t.IsGenericType && !t.IsNested)
            .FirstOrDefault(t => t.GetProperties(PublicDeclared)
                .Count(p => p.GetIndexParameters().Length > 0) >= 2);

        if (typeWithMultipleIndexers == null)
            return;

        CallToolResult result = await _server.Client.CallToolAsync("decompile_property",
            new Dictionary<string, object?>
            {
                ["assembly"] = _server.AssemblyPath,
                ["typeName"] = typeWithMultipleIndexers.FullName!,
                ["propertyName"] = "Item",
            });

        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        Assert.True(result.IsError,
            $"Expected indexer-ambiguity rejection, got: {text}");
        Assert.Contains("overloads", text);
        Assert.Contains("decompile_method", text);
    }

    [Fact]
    public async Task DecompileEvent_OnAKnownEvent_ReturnsEventDeclaration()
    {
        // Reflection-discover a public event declared on a type in the test target.
        // If the upstream package exposes no public events, the test bails out —
        // decompile_event's happy path stays unverified until one lands.
        var assembly = typeof(ICSharpCode.Decompiler.CSharp.CSharpDecompiler).Assembly;
        var pair = assembly.GetExportedTypes()
            .Where(t => !t.IsGenericType && !t.IsNested)
            .SelectMany(t => t.GetEvents(PublicDeclared)
                .Select(e => new { Type = t, Event = e }))
            .FirstOrDefault();

        if (pair == null)
            return;

        string text = await CallToolText("decompile_event", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["typeName"] = pair.Type.FullName!,
            ["eventName"] = pair.Event.Name,
        });
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.Contains(pair.Event.Name, text);
        // Anchor on the `event` keyword followed by whitespace — the decompiler
        // emits this for both field-like (`event T Foo;`) and custom-accessor
        // (`event T Foo { add { ... } }`) forms.
        Assert.Matches(@"\bevent\s+", text);
    }

    /// <summary>
    /// Calls an ILens MCP tool, fails the test if it returned an error, and
    /// returns the concatenated text content.
    /// </summary>
    private async Task<string> CallToolText(string toolName, Dictionary<string, object?> arguments)
    {
        CallToolResult result = await _server.Client.CallToolAsync(toolName, arguments);
        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.True(result.IsError != true, $"Tool '{toolName}' returned an error: {text}");
        return text;
    }

    /// <summary>
    /// Reflection short-name → C# keyword, for the 17 framework primitives ILens
    /// renders via <c>ReferenceFormatter.TypeKeyword</c>. Returns <c>null</c> for
    /// any other name. Duplicated here (a few rows) for test isolation rather than
    /// reaching into the production map.
    /// </summary>
    private static string? CSharpKeyword(string reflectionName) => reflectionName switch
    {
        "Boolean" => "bool",    "Byte"    => "byte",    "SByte"  => "sbyte",
        "Char"    => "char",    "Decimal" => "decimal", "Double" => "double",
        "Single"  => "float",   "Int32"   => "int",     "UInt32" => "uint",
        "Int64"   => "long",    "UInt64"  => "ulong",   "Int16"  => "short",
        "UInt16"  => "ushort",  "Object"  => "object",  "String" => "string",
        "Void"    => "void",    _         => null,
    };
}
