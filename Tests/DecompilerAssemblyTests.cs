using System.Reflection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests: spawn the real ILens MCP server and exercise its tools
/// against ICSharpCode.Decompiler.dll, and against the runtime's System.Private.CoreLib
/// for type shapes ICSharpCode.Decompiler.dll does not show. Assertions check the <em>shape</em> of
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
    public async Task FindTypes_InANamespace_ListsItsTypesButNotThoseOfItsSubNamespaces()
    {
        string text = await CallToolText("find_types", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["namespace"] = "ICSharpCode.Decompiler.TypeSystem",
            ["limit"] = 1000,
        });

        // The namespace holds internal types as well, which the last line counts.
        var lines = text.Split('\n');
        Assert.Matches(@"^\d+ types:$", lines[0]);
        Assert.All(lines[1..^1], line => Assert.StartsWith("ICSharpCode.Decompiler.TypeSystem.", line));
        Assert.DoesNotContain(lines, line => line.StartsWith("ICSharpCode.Decompiler.TypeSystem.Implementation."));
        Assert.Matches(@"^Not shown: \d+ internal or private$", lines[^1]);
    }

    [Fact]
    public async Task FindTypes_WithANamespaceThatDoesNotExist_ReturnsError()
    {
        // An exact namespace asserts it exists; answering a misspelling with "No types"
        // would read as an empty namespace.
        var (result, text) = await CallToolExpectingError("find_types", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["namespace"] = "ICSharpCode.Decompiler.TypeSystm",
        });

        Assert.True(result.IsError, $"Expected an error, got: {text}");
        Assert.Contains("Namespace not found: ICSharpCode.Decompiler.TypeSystm.", text);
    }

    [Fact]
    public async Task FindTypes_WithoutAFilter_ListsTypesUpToTheLimitAndCountsAll()
    {
        string text = await CallToolText("find_types", new()
        {
            ["assembly"] = _server.AssemblyPath,
        });

        // After the list come the line for what the limit cut and the line for the internal
        // types, which the accessibility filter left out.
        var lines = text.Split('\n');
        int total = ParseLeadingCount(text);
        Assert.True(total > 100, $"Expected more types than the default limit, got: {lines[0]}");
        Assert.Equal(103, lines.Length);
        Assert.Equal($"... ({total - 100} more; raise limit to see all)", lines[^2]);
        Assert.Matches(@"^Not shown: \d+ internal or private$", lines[^1]);
        Assert.Equal(lines[1..^2].Order(StringComparer.Ordinal), lines[1..^2]);
    }

    [Fact]
    public async Task FindTypes_ListsAnInternalTypeOnlyWithAccessibilityAll()
    {
        var assembly = typeof(ICSharpCode.Decompiler.CSharp.CSharpDecompiler).Assembly;
        var names = assembly.GetTypes().Select(t => t.Name).ToList();
        var type = assembly.GetTypes()
            .Where(t => t.IsNotPublic && !t.Name.Contains('<') && !t.Name.Contains('`'))
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .FirstOrDefault(t => names.Count(n => n.Contains(t.Name, StringComparison.OrdinalIgnoreCase)) == 1)
            ?? throw new InvalidOperationException(
                "No internal top-level type with a distinctive name found in ICSharpCode.Decompiler.dll — " +
                "the test target's API drifted; pick a different shape.");
        var arguments = new Dictionary<string, object?>
        {
            ["assembly"] = _server.AssemblyPath,
            ["name_contains"] = type.Name,
        };

        Assert.Equal(
            "No types\nNot shown: 1 internal or private; accessibility all lists it",
            await CallToolText("find_types", arguments));
        Assert.Equal(
            $"1 type:\n{type.FullName}",
            await CallToolText("find_types", new(arguments) { ["accessibility"] = "all" }));
    }

    [Fact]
    public async Task FindTypes_ByNameContains_FindsMatches()
    {
        string text = await CallToolText("find_types", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["name_contains"] = "decompiler",
        });
        var lines = text.Split('\n');
        Assert.Matches(@"^\d+ types?:$", lines[0]);
        Assert.Contains("ICSharpCode.Decompiler.CSharp.CSharpDecompiler", lines);
    }

    [Fact]
    public async Task FindTypes_DefaultsToHidingRoslynClosureClasses()
    {
        // <>c is the canonical Roslyn name for the per-method cached-delegate
        // host. Filtered by default — searching for it lists no type and says how
        // many the filter left out. With the switch on, the same search lists
        // them. The closure classes are private, so both calls ask for every
        // accessibility.
        string filtered = await CallToolText("find_types", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["name_contains"] = "<>c",
            ["accessibility"] = "all",
        });

        string unfiltered = await CallToolText("find_types", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["name_contains"] = "<>c",
            ["accessibility"] = "all",
            ["include_compiler_generated"] = true,
        });
        Assert.NotEqual("No types", unfiltered);
        Assert.Equal(
            $"No types\nNot shown: {ParseLeadingCount(unfiltered)} compiler-generated; " +
            "include_compiler_generated lists them",
            filtered);
    }

    [Fact]
    public async Task FindTypes_FiltersBurstDirectCallWrapperNestedType()
    {
        // Burst's source generator emits nested helper types
        // whose names carry `$BurstDirectCall` / `$PostfixBurstDelegate` suffixes
        // (the outer type is a regular user class, so the chain-walk in
        // CompilerGeneratedFilter only catches them via the inner name).
        // '$' isn't a valid C# identifier character, so no C# fixture project can
        // produce this shape; BurstShapedAssembly writes one.
        var fixturePath = BurstShapedAssembly.Write("0000ABCD", BurstShapedAssembly.Returns(1));
        try
        {
            // Filter on by default — the nested `$BurstDirectCall` type drops out. The
            // type is internal, so both calls ask for every accessibility.
            string filtered = await CallToolText("find_types", new()
            {
                ["assembly"] = fixturePath,
                ["name_contains"] = "$BurstDirectCall",
                ["accessibility"] = "all",
            });
            Assert.Equal(
                "No types\nNot shown: 1 compiler-generated; include_compiler_generated lists it",
                filtered);

            // Filter off — the same search hits the synthesized nested type.
            string unfiltered = await CallToolText("find_types", new()
            {
                ["assembly"] = fixturePath,
                ["name_contains"] = "$BurstDirectCall",
                ["accessibility"] = "all",
                ["include_compiler_generated"] = true,
            });
            Assert.NotEqual("No types", unfiltered);
        }
        finally
        {
            BurstShapedAssembly.TryDelete(fixturePath);
        }
    }

    [Fact]
    public async Task FindMethods_FiltersTheMethodBurstAddsToStartAWrapper()
    {
        // Burst adds Initialize$Compute_HHHHHHHH$BurstDirectCall to the class it compiles a
        // method of. It only starts the wrapper and holds nothing the developer wrote.
        var path = BurstShapedAssembly.Write("0000ABCD", BurstShapedAssembly.Returns(1));
        try
        {
            string filtered = await CallToolText("find_methods", new()
            {
                ["assembly"] = path,
                ["name_contains"] = "$BurstDirectCall",
            });
            Assert.Equal(
                "No methods\nNot shown: 1 compiler-generated; include_compiler_generated lists it",
                filtered);

            string unfiltered = await CallToolText("find_methods", new()
            {
                ["assembly"] = path,
                ["name_contains"] = "$BurstDirectCall",
                ["include_compiler_generated"] = true,
            });
            Assert.Contains("Initialize$Compute_0000ABCD$BurstDirectCall", unfiltered);
        }
        finally
        {
            BurstShapedAssembly.TryDelete(path);
        }
    }

    [Fact]
    public async Task FindTypes_DropsCountWhenCompilerGeneratedIsExcluded()
    {
        // Any well-trafficked namespace in ICSharpCode.Decompiler.dll has
        // Roslyn helpers (display classes, anonymous types). The filtered set
        // must be strictly smaller — assert the count drops, without naming
        // a specific generated type (whose presence drifts upstream). Those
        // helpers are private, so both calls ask for every accessibility.
        string filtered = await CallToolText("find_types", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["namespace"] = "ICSharpCode.Decompiler.IL",
            ["accessibility"] = "all",
        });
        string unfiltered = await CallToolText("find_types", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["namespace"] = "ICSharpCode.Decompiler.IL",
            ["accessibility"] = "all",
            ["include_compiler_generated"] = true,
        });

        int filteredCount = ParseLeadingCount(filtered);
        int unfilteredCount = ParseLeadingCount(unfiltered);
        Assert.True(unfilteredCount > filteredCount,
            $"Expected the unfiltered count ({unfilteredCount}) to exceed the " +
            $"filtered count ({filteredCount}); the filter would otherwise be a no-op.");
    }

    /// <summary>
    /// Parses the leading "N types:" from find_types output. The tool's first
    /// line is always "<count> types:" — pull the integer.
    /// </summary>
    private static int ParseLeadingCount(string text)
    {
        var firstSpace = text.IndexOf(' ');
        if (firstSpace < 0) return -1;
        return int.TryParse(text.AsSpan(0, firstSpace), out var n) ? n : -1;
    }

    [Fact]
    public async Task FindMethods_ByNamePattern_ReturnsMatches()
    {
        string text = await CallToolText("find_methods", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["name_contains"] = "Decompile",
        });
        Assert.DoesNotContain("No methods match", text);
    }

    [Fact]
    public async Task DecompileType_OnAKnownType_YieldsPlausibleCSharp()
    {
        string text = await CallToolText("decompile", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["type_name"] = "ICSharpCode.Decompiler.CSharp.CSharpDecompiler",
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
            ["type_name"] = "ICSharpCode.Decompiler.CSharp.CSharpDecompiler",
            ["kind"] = "used_by",
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
        string text = await CallToolText("decompile", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["type_name"] = "ICSharpCode.Decompiler.DecompilerSettings",
            ["member_name"] = "get_ThrowOnAssemblyResolveErrors",
        });
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.Contains("ThrowOnAssemblyResolveErrors", text);
        // A header alone would satisfy the name check above — require a brace so
        // we know the decompiler actually emitted a property/method body.
        Assert.Contains("{", text);
    }

    [Theory]
    [InlineData("get")]
    [InlineData("readonly get")]
    [InlineData("set")]
    [InlineData("init")]
    [InlineData("add")]
    [InlineData("remove")]
    public async Task DecompileMethod_OnAnAccessor_ShowsItsKeyword(string declaration)
    {
        // An accessor decompiled on its own must still say which accessor it is: without the
        // keyword, a getter printed its attributes and body alone, and a readonly getter a
        // lone "readonly" line before the body.
        var accessor = CoreLibAccessor(declaration);
        string text = await DecompileCoreLibMethod(accessor.DeclaringType!, accessor.Name,
            new() { ["parameter_count"] = accessor.GetParameters().Length });

        Assert.Matches($@"(?m)^{declaration}\s*$", text);
    }

    // Members whose C# declaration carries a keyword that the metadata records as a reference
    // type or a modifier: a ref and a ref readonly return, a ref readonly parameter, an init
    // accessor.
    [Theory]
    [InlineData("System.Span<>", "Item", "public ref T Item[int index] { get; }")]
    [InlineData("System.ReadOnlySpan<>", "Item", "public ref readonly T Item[int index] { get; }")]
    [InlineData("System.Runtime.CompilerServices.Unsafe", "AsRef",
        "public static ref T AsRef<T>(ref readonly T source) where T : allows ref struct")]
    [InlineData("System.Runtime.CompilerServices.CompilerFeatureRequiredAttribute", "IsOptional",
        "public bool IsOptional { get; init; }")]
    public async Task ListMembers_WritesReferencesAndInitAsCSharp(string typeName, string name, string expected)
    {
        string text = await CallToolText("list_members", new()
        {
            ["assembly"] = _server.CoreLibPath,
            ["type_name"] = typeName,
            ["name_contains"] = name,
        });

        Assert.Contains(expected, text.Split('\n').Select(line => line.Trim()));
        Assert.DoesNotContain("modreq", text);
    }

    [Fact]
    public async Task ListMembers_OnAVolatileField_WritesVolatile()
    {
        // The metadata records volatile as a modreq on the field's type.
        var field = typeof(object).Assembly.GetExportedTypes()
            .Where(t => !t.IsGenericType)
            .SelectMany(t => t.GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .First(f => f.GetRequiredCustomModifiers().Any(m => m.Name == "IsVolatile"));

        string text = await CallToolText("list_members", new()
        {
            ["assembly"] = _server.CoreLibPath,
            ["type_name"] = field.DeclaringType!.FullName!,
            ["name_contains"] = field.Name,
            ["kinds"] = new[] { "field" },
            ["accessibility"] = "all",
        });

        Assert.Matches($@"\bvolatile \S+ {System.Text.RegularExpressions.Regex.Escape(field.Name)}\b", text);
        Assert.DoesNotContain("modreq", text);
    }

    // The first line of decompile writes each parameter and the return as C# does, and
    // a parameter type copied from it selects the overload it came from. Write has overloads
    // without type parameters, so its name without them reaches a generic one only through
    // parameter types that the generic one alone has.
    [Theory]
    [InlineData("System.Collections.Generic.Dictionary<,>", "TryGetValue", new[] { "TKey", "out TValue" },
        "// System.Collections.Generic.Dictionary<,>.TryGetValue(TKey, out TValue) → bool")]
    [InlineData("System.ReadOnlySpan<>", "get_Item", new[] { "int" },
        "// System.ReadOnlySpan<>.get_Item(int) → ref readonly T")]
    [InlineData("System.Runtime.CompilerServices.CompilerFeatureRequiredAttribute", "set_IsOptional", new[] { "bool" },
        "// System.Runtime.CompilerServices.CompilerFeatureRequiredAttribute.set_IsOptional(bool) → void")]
    [InlineData("System.Diagnostics.Tracing.EventSource", "Write<T>", new[] { "string", "ref EventSourceOptions", "ref T" },
        "// System.Diagnostics.Tracing.EventSource.Write<T>(string, ref EventSourceOptions, ref T) → void")]
    [InlineData("System.Diagnostics.Tracing.EventSource", "Write", new[] { "string", "ref EventSourceOptions", "ref T" },
        "// System.Diagnostics.Tracing.EventSource.Write<T>(string, ref EventSourceOptions, ref T) → void")]
    public async Task DecompileMethod_WritesReferencesAsCSharp_AndTakesThemBack(
        string typeName, string methodName, string[] parameterTypes, string expectedHeader)
    {
        string text = await CallToolText("decompile", new()
        {
            ["assembly"] = _server.CoreLibPath,
            ["type_name"] = typeName,
            ["member_name"] = methodName,
            ["parameter_types"] = parameterTypes,
        });

        Assert.StartsWith(expectedHeader + "\n", text);
    }

    /// <summary>
    /// A public CoreLib accessor whose standalone declaration opens with
    /// <paramref name="declaration"/>, such as <c>readonly get</c> or <c>init</c>.
    /// </summary>
    private static MethodInfo CoreLibAccessor(string declaration)
    {
        const BindingFlags Declared = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        static bool IsInitOnly(MethodInfo setter) =>
            setter.ReturnParameter.GetRequiredCustomModifiers().Any(m => m.Name == "IsExternalInit");
        static bool IsReadOnly(MemberInfo member) =>
            member.CustomAttributes.Any(a => a.AttributeType.Name == "IsReadOnlyAttribute");

        var types = typeof(object).Assembly.GetExportedTypes().Where(t => !t.IsGenericType);
        return declaration switch
        {
            "get" => typeof(string).GetProperty(nameof(string.Length))!.GetMethod!,
            "set" => typeof(System.Text.StringBuilder).GetProperty(nameof(System.Text.StringBuilder.Length))!.SetMethod!,
            "readonly get" => types
                .Where(t => t.IsValueType && !IsReadOnly(t))
                .SelectMany(t => t.GetProperties(Declared))
                .Select(p => p.GetMethod)
                .First(g => g is { IsPublic: true } && IsReadOnly(g))!,
            "init" => types
                .SelectMany(t => t.GetProperties(Declared))
                .Select(p => p.SetMethod)
                .First(s => s is { IsPublic: true } && IsInitOnly(s))!,
            "add" => typeof(AppDomain).GetEvent(nameof(AppDomain.ProcessExit))!.AddMethod!,
            "remove" => typeof(AppDomain).GetEvent(nameof(AppDomain.ProcessExit))!.RemoveMethod!,
            _ => throw new ArgumentOutOfRangeException(nameof(declaration)),
        };
    }

    [Fact]
    public async Task FindMethods_DoesNotEmitPropertyAccessors()
    {
        // Mirror of the decompile test above: find_methods deliberately hides
        // accessors from generic browsing, so a name search for a known accessor
        // should report no methods even though decompile finds it.
        string text = await CallToolText("find_methods", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["name_contains"] = "get_ThrowOnAssemblyResolveErrors",
        });
        Assert.Equal("No methods", text);
    }

    [Fact]
    public async Task FindTypes_OnAPathOutsideTheAllowedRoots_ReturnsError()
    {
        var arguments = new Dictionary<string, object?>
        {
            ["assembly"] = @"C:\Windows\System32\kernel32.dll",
            ["namespace"] = "System",
        };
        CallToolResult result = await _server.Client.CallToolAsync("find_types", arguments);

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

            CallToolResult result = await _server.Client.CallToolAsync("find_types",
                new Dictionary<string, object?>
                {
                    ["assembly"] = symlinkInside,
                    ["namespace"] = "ICSharpCode.Decompiler",
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

        string text = await CallToolText("decompile", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["type_name"] = type.FullName!,
            ["member_name"] = overload.Name,
            ["parameter_count"] = overload.GetParameters().Length,
            ["parameter_types"] = parameterTypeNames,
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
        // — what find_methods and decompile's "Available: ..." error message
        // both render. The matcher must accept that form too, since it is what an LLM
        // is most likely to feed back in. Selecting a same-arity overload group keeps the
        // test honest about why it passes: DisambiguateMethod no longer short-circuits on
        // a single candidate, so the keyword patterns are checked either way, but only a
        // same-arity group forces them to actually discriminate between candidates rather
        // than merely validate the one method that was going to be returned anyway.
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

        string text = await CallToolText("decompile", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["type_name"] = type.FullName!,
            ["member_name"] = method.Name,
            ["parameter_count"] = method.GetParameters().Length,
            ["parameter_types"] = parameterTypeNames,
        });

        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.Contains(method.Name, text);
    }

    [Fact]
    public async Task FindMethods_WithCSharpKeywordParameterTypes_ReturnsMatches()
    {
        // TypeMatcher backs both decompile and find_methods. The keyword-form
        // fix must benefit both consumers. Any reasonably-sized .NET library has
        // methods taking a single string parameter, so "string" must yield matches.
        string text = await CallToolText("find_methods", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["parameter_types"] = new[] { "string" },
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

        CallToolResult result = await tightClient.CallToolAsync("find_types",
            new Dictionary<string, object?>
            {
                ["assembly"] = _server.AssemblyPath,
                ["namespace"] = "ICSharpCode.Decompiler",
            });

        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        Assert.True(result.IsError,
            $"Expected over-budget rejection, got: {text}");
        Assert.Contains("exceeds", text);
        Assert.Contains("budget", text);
    }

    [Fact]
    public async Task GetOrLoad_ClosesAssembliesWhenACallEndsOverTheMemoryBudget()
    {
        // Loading an assembly makes the decompiler open the assemblies it references from the
        // same directory, and closing the host must close them again. Each server gets its own
        // copy of ExternalUser.dll beside its reference ExternalLib.dll. Under the default budget
        // the host stays cached after the call, so the reference stays open, which shows the
        // check can fail at all. Under a 1 MB budget, below what ILens needs with nothing open,
        // every host is closed when its call ends, so the reference is free once the call has
        // answered, and the next call on the assembly opens it again and answers alike.
        string root = Path.Combine(Path.GetTempPath(), "ILens-budget-" + Guid.NewGuid().ToString("N"));
        string cached = CopyUserAndReference("Cached");
        string closed = CopyUserAndReference("Closed");

        try
        {
            await using (var client = await StartServer("ILens-budget-default", root))
            {
                await FindTypes(client, Path.Combine(cached, "ExternalUser.dll"));
                Assert.False(CanOpenExclusively(Path.Combine(cached, "ExternalLib.dll")),
                    "The reference should be open while its host is cached.");
            }

            await using (var client = await StartServer("ILens-budget-tight", root, "--max-total-size", "1"))
            {
                string first = await FindTypes(client, Path.Combine(closed, "ExternalUser.dll"));
                Assert.True(CanOpenExclusively(Path.Combine(closed, "ExternalLib.dll")),
                    "The host should have been closed when its call ended, and with it the reference it opened.");
                Assert.Equal(first, await FindTypes(client, Path.Combine(closed, "ExternalUser.dll")));
            }
        }
        finally
        {
            await ILensServerFixture.DeleteWhenReleased(root);
        }

        string CopyUserAndReference(string folder)
        {
            string binDir = Path.GetDirectoryName(_server.AssemblyPath)!;
            string dir = Directory.CreateDirectory(Path.Combine(root, folder)).FullName;
            File.Copy(Path.Combine(binDir, "ExternalUser.dll"), Path.Combine(dir, "ExternalUser.dll"));
            File.Copy(Path.Combine(binDir, "References", "ExternalLib.dll"), Path.Combine(dir, "ExternalLib.dll"));
            return dir;
        }

        static async Task<McpClient> StartServer(string name, string root, params string[] options) =>
            await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = name,
                Command = ILensServerFixture.ResolveILensExecutable(),
                Arguments = ["--allow-root", root, .. options],
            }));

        static async Task<string> FindTypes(McpClient client, string assembly)
        {
            CallToolResult result = await client.CallToolAsync("find_types",
                new Dictionary<string, object?> { ["assembly"] = assembly });
            string text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
            Assert.True(result.IsError != true, text);
            return text;
        }

        static bool CanOpenExclusively(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
        }
    }

    [Fact]
    public async Task Analyze_ImplementedBy_OnAnInterface_ListsImplementers()
    {
        // ImplementedBy at the type level is synthesized in-process — ILSpyX ships
        // only member-level analyzers for the "Implemented By" header. Discover a
        // suitable interface/implementer pair via reflection so the test stays
        // valid as the upstream package drifts. Restrict to non-generic, non-nested
        // types, whose names reflection's FullName and ILens spell alike.
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
            ["type_name"] = pair.Interface.FullName!,
            ["kind"] = "implemented_by",
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
                ["type_name"] = "ICSharpCode.Decompiler.CSharp.CSharpDecompiler",
                ["kind"] = "implemented_by",
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
                ["type_name"] = "ICSharpCode.Decompiler.CSharp.CSharpDecompiler",
                ["kind"] = "applied_to",
            });

        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        Assert.True(result.IsError,
            $"Expected AppliedTo-on-non-attribute rejection, got: {text}");
        Assert.Contains("System.Attribute", text);
    }

    [Fact]
    public async Task Analyze_ReadBy_OnAProperty_MatchesTheGetterCallers()
    {
        // A property has no usage answer of its own, so analyze routes a property
        // read/assign/use query to the accessor and asks who uses that. Asserting the
        // two routes agree pins the behavior without hard-coding call sites that drift
        // as the upstream package moves.
        string viaProperty = await CallToolText("analyze", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["type_name"] = "ICSharpCode.Decompiler.DecompilerSettings",
            ["member_name"] = "ThrowOnAssemblyResolveErrors",
            ["kind"] = "read_by",
        });
        string viaAccessor = await CallToolText("analyze", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["type_name"] = "ICSharpCode.Decompiler.DecompilerSettings",
            ["member_name"] = "get_ThrowOnAssemblyResolveErrors",
            ["kind"] = "used_by",
        });

        Assert.Equal(ResultBody(viaAccessor), ResultBody(viaProperty));
        // The header names the accessor consulted, so the substitution is visible in the
        // output rather than silent — and the accessor route stays discoverable from it.
        Assert.Contains("(via get_ThrowOnAssemblyResolveErrors)", viaProperty);
    }

    [Fact]
    public async Task Analyze_UsedBy_OnAProperty_CoversBothAccessors()
    {
        string text = await CallToolText("analyze", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["type_name"] = "ICSharpCode.Decompiler.DecompilerSettings",
            ["member_name"] = "ThrowOnAssemblyResolveErrors",
            ["kind"] = "used_by",
        });
        Assert.Contains(
            "(via get_ThrowOnAssemblyResolveErrors, set_ThrowOnAssemblyResolveErrors)", text);

        // Every reader must also appear in the union — UsedBy merges the two accessor
        // runs rather than replacing one with the other.
        string readers = await CallToolText("analyze", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["type_name"] = "ICSharpCode.Decompiler.DecompilerSettings",
            ["member_name"] = "ThrowOnAssemblyResolveErrors",
            ["kind"] = "read_by",
        });
        string readerBody = ResultBody(readers);
        if (readerBody.Length > 0)
        {
            foreach (string caller in readerBody.Split('\n'))
                Assert.Contains(caller, text);
        }
    }

    [Fact]
    public async Task Analyze_Uses_OnAProperty_MatchesTheUnionOfItsAccessors()
    {
        // Uses is the one outgoing question, so it stays Uses on the way down to the
        // accessors instead of becoming UsedBy like the incoming kinds. Comparing against
        // the getter run directly pins that without hard-coding call sites that drift.
        string viaProperty = await CallToolText("analyze", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["type_name"] = "ICSharpCode.Decompiler.DecompilerSettings",
            ["member_name"] = "ThrowOnAssemblyResolveErrors",
            ["kind"] = "uses",
        });
        string viaGetter = await CallToolText("analyze", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["type_name"] = "ICSharpCode.Decompiler.DecompilerSettings",
            ["member_name"] = "get_ThrowOnAssemblyResolveErrors",
            ["kind"] = "uses",
        });

        Assert.Contains(
            "(via get_ThrowOnAssemblyResolveErrors, set_ThrowOnAssemblyResolveErrors)", viaProperty);
        string getterBody = ResultBody(viaGetter);
        if (getterBody.Length > 0)
        {
            foreach (string used in getterBody.Split('\n'))
                Assert.Contains(used, viaProperty);
        }
    }

    [Fact]
    public async Task Analyze_Uses_OnAnEvent_ReturnsError()
    {
        // Uses is offered for properties but not events: a field-like event's accessors are
        // compiler-generated, so the answer would be the subscriber-list bookkeeping rather
        // than anything written in source. The error names the kinds that do apply.
        CallToolResult result = await _server.Client.CallToolAsync("analyze",
            new Dictionary<string, object?>
            {
                ["assembly"] = _server.AssemblyPath,
                ["type_name"] = "ICSharpCode.Decompiler.DecompilerSettings",
                ["member_name"] = "PropertyChanged",
                ["kind"] = "uses",
            });

        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        Assert.True(result.IsError, $"Expected Uses-on-event rejection, got: {text}");
        Assert.Contains("not valid for Event", text);
        Assert.Contains("used_by", text);
    }

    [Fact]
    public async Task Analyze_AssignedBy_OnAReadOnlyProperty_ReturnsError()
    {
        // CSharpDecompiler.TypeSystem declares no setter, so "who assigns it?" is a
        // question about an accessor that does not exist. An empty result would be true
        // but unreadable — indistinguishable from a settable property nobody writes.
        CallToolResult result = await _server.Client.CallToolAsync("analyze",
            new Dictionary<string, object?>
            {
                ["assembly"] = _server.AssemblyPath,
                ["type_name"] = "ICSharpCode.Decompiler.CSharp.CSharpDecompiler",
                ["member_name"] = "TypeSystem",
                ["kind"] = "assigned_by",
            });

        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        Assert.True(result.IsError,
            $"Expected AssignedBy-on-read-only-property rejection, got: {text}");
        Assert.Contains("read-only", text);
        Assert.Contains("setter", text);
    }

    [Fact]
    public async Task Analyze_UsedBy_OnAnEvent_RoutesThroughItsAccessors()
    {
        // DecompilerSettings implements INotifyPropertyChanged, so PropertyChanged is a
        // build-guaranteed event on the test target. An event has no read/assign split —
        // subscribing, unsubscribing and raising are all "use" — so UsedBy unions every
        // accessor it declares. A field-like event has add and remove but no invoke, and
        // its raise sites read the subscriber list rather than calling an accessor, so the
        // backing field is part of the route.
        string text = await CallToolText("analyze", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["type_name"] = "ICSharpCode.Decompiler.DecompilerSettings",
            ["member_name"] = "PropertyChanged",
            ["kind"] = "used_by",
        });
        Assert.Contains(
            "(via add_PropertyChanged, remove_PropertyChanged, the subscriber list)", text);
        // OnPropertyChanged raises the event, so the union must reach past the subscribers.
        Assert.Contains("OnPropertyChanged", text);
        // The add and remove accessors touch the subscriber list by construction, and ILSpyX
        // reports a use inside an accessor under the event that owns it — so without the
        // self-reference filter the event appears in its own result list.
        foreach (string line in ResultBody(text).Split('\n'))
            Assert.NotEqual("ICSharpCode.Decompiler.DecompilerSettings.PropertyChanged", line);
    }

    [Fact]
    public async Task Analyze_OnAFieldLikeEvent_DoesNotReportACrossKindCollision()
    {
        // `public event EventHandler X;` compiles to an event plus a private field of the
        // same name holding the subscriber list. The cross-kind probe in SymbolResolver sees
        // both and would call the name ambiguous, which would leave the ordinary C# event
        // unaddressable by name — including for the OverriddenBy/ImplementedBy kinds that
        // predate the usage routing.
        string text = await CallToolText("analyze", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["type_name"] = "ICSharpCode.Decompiler.DecompilerSettings",
            ["member_name"] = "PropertyChanged",
            ["kind"] = "overridden_by",
        });
        Assert.DoesNotContain("ambiguous", text);
        Assert.Contains("PropertyChanged", text);
    }

    [Fact]
    public async Task ListMembers_AcceptsAScalarWhereTheSchemaDeclaresAnArray()
    {
        // "field" instead of ["field"] is a routine slip, and the SDK's binder answers it
        // with a raw serializer failure naming an internal type. There is only one thing a
        // lone value can mean, so ArgumentValueFilter promotes it — the two calls must be
        // indistinguishable.
        // DecompilerSettings exposes no public or protected field of its own, so widen the
        // accessibility filter — otherwise both calls agree on "no members match" and the
        // comparison proves nothing about which kinds were selected.
        var arguments = new Dictionary<string, object?>
        {
            ["assembly"] = _server.AssemblyPath,
            ["type_name"] = "ICSharpCode.Decompiler.DecompilerSettings",
            ["accessibility"] = "all",
        };
        string scalar = await CallToolText("list_members",
            new(arguments) { ["kinds"] = "field" });
        string array = await CallToolText("list_members",
            new(arguments) { ["kinds"] = new[] { "field" } });

        Assert.Equal(array, scalar);
        // Match the group headers, not the bare words — a field named "anonymousMethods"
        // contains "Methods" and would make a looser check pass for the wrong reason.
        Assert.Matches(@"\n\d+ fields?:", scalar);
        Assert.DoesNotMatch(@"\n\d+ methods?:", scalar);
    }

    [Fact]
    public async Task FindMethods_AcceptsAScalarParameterType()
    {
        // The coercion is driven by each tool's own input schema, not by a list of known
        // parameters, so it has to hold for a string[] on a different tool too.
        var arguments = new Dictionary<string, object?>
        {
            ["assembly"] = _server.AssemblyPath,
            ["type_name"] = "ICSharpCode.Decompiler.DecompilerSettings",
        };
        string scalar = await CallToolText("find_methods",
            new(arguments) { ["parameter_types"] = "string" });
        string array = await CallToolText("find_methods",
            new(arguments) { ["parameter_types"] = new[] { "string" } });

        Assert.Equal(array, scalar);
        // Two identical "no matches" replies would also be equal, so require that the
        // filter actually selected something.
        Assert.DoesNotContain("No methods match", scalar);
    }

    [Fact]
    public async Task DecompileProperty_OnAKnownProperty_ReturnsPropertyDeclaration()
    {
        // ThrowOnAssemblyResolveErrors is the same stable public property that
        // DecompileMethod_OnAPropertyGetter_... targets via the get_ prefix.
        // decompile takes the unprefixed name and returns the whole
        // property declaration (signature plus accessor bodies).
        string text = await CallToolText("decompile", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["type_name"] = "ICSharpCode.Decompiler.DecompilerSettings",
            ["member_name"] = "ThrowOnAssemblyResolveErrors",
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

        string text = await CallToolText("decompile", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["type_name"] = pair.Type.FullName!,
            ["member_name"] = pair.Property.Name,
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
        CallToolResult result = await _server.Client.CallToolAsync("decompile",
            new Dictionary<string, object?>
            {
                ["assembly"] = _server.AssemblyPath,
                ["type_name"] = "ICSharpCode.Decompiler.DecompilerSettings",
                ["member_name"] = "ThisPropertyDoesNotExist",
            });

        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        Assert.True(result.IsError, $"Expected not-found rejection, got: {text}");
        Assert.Contains("not found", text);
    }

    [Fact]
    public async Task DecompileProperty_OnAnIndexerWithMultipleOverloads_ReturnsAmbiguityError()
    {
        // ICSharpCode.Decompiler.dll declares no type with two public indexers, so the target
        // comes from CoreLib.
        var (type, _) = CoreLibTypeWithTwoIndexers();

        CallToolResult result = await _server.Client.CallToolAsync("decompile",
            new Dictionary<string, object?>
            {
                ["assembly"] = _server.CoreLibPath,
                ["type_name"] = type.FullName!,
                ["member_name"] = "Item",
            });

        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        Assert.True(result.IsError,
            $"Expected indexer-ambiguity rejection, got: {text}");
        Assert.Contains("overloads", text);
        Assert.Contains("name its accessor instead, get_Item or set_Item, with parameter_types", text);
    }

    [Fact]
    public async Task Analyze_OnAnIndexerNameSeveralIndexersShare_ListsThemInsteadOfAnsweringForOne()
    {
        // Answering for the first indexer alone would leave out the readers of the others,
        // with nothing in the answer to show that it did.
        var (type, indexers) = CoreLibTypeWithTwoIndexers();

        var (result, text) = await CallToolExpectingError("analyze", new()
        {
            ["assembly"] = _server.CoreLibPath,
            ["type_name"] = type.FullName!,
            ["member_name"] = "Item",
            ["kind"] = "read_by",
        });

        Assert.True(result.IsError, $"Expected indexer-ambiguity rejection, got: {text}");
        Assert.Contains("name its accessor instead, get_Item or set_Item, with parameter_types", text);
        foreach (var indexer in indexers)
        {
            string indices = string.Join(", ", indexer.GetIndexParameters().Select(p => RenderTypeReference(p.ParameterType)));
            Assert.Contains($"{type.FullName}.Item[{indices}]", text);
        }
    }

    [Fact]
    public async Task DecompileAndAnalyze_OnAnIndexer_NameItWithItsIndices()
    {
        // An indexer's name is shared by every indexer of its type, so an answer naming one
        // says which by its index types. String's only indexer is renamed Chars by
        // [IndexerName], so the indices follow whatever name the indexer carries.
        var indexer = typeof(string).GetProperties(PublicDeclared)
            .Single(p => p.GetIndexParameters().Length > 0);
        string indices = string.Join(", ",
            indexer.GetIndexParameters().Select(p => RenderTypeReference(p.ParameterType)));
        string expected = $"System.String.{indexer.Name}[{indices}]";

        string decompiled = await CallToolText("decompile", new()
        {
            ["assembly"] = _server.CoreLibPath,
            ["type_name"] = "System.String",
            ["member_name"] = indexer.Name,
        });
        Assert.Equal($"// {expected}", decompiled.Split('\n')[0]);

        string analyzed = await CallToolText("analyze", new()
        {
            ["assembly"] = _server.CoreLibPath,
            ["type_name"] = "System.String",
            ["member_name"] = indexer.Name,
            ["kind"] = "read_by",
        });
        Assert.Matches(
            $@"^(\d+ readers?|No readers) of {System.Text.RegularExpressions.Regex.Escape(expected)} ",
            analyzed);
    }

    [Fact]
    public async Task DecompileEvent_OnAKnownEvent_ReturnsEventDeclaration()
    {
        // Reflection-discover a public event declared on a type in the test target.
        // If the upstream package exposes no public events, the test bails out —
        // decompiling an event stays unverified until one lands.
        var assembly = typeof(ICSharpCode.Decompiler.CSharp.CSharpDecompiler).Assembly;
        var pair = assembly.GetExportedTypes()
            .Where(t => !t.IsGenericType && !t.IsNested)
            .SelectMany(t => t.GetEvents(PublicDeclared)
                .Select(e => new { Type = t, Event = e }))
            .FirstOrDefault();

        if (pair == null)
            return;

        string text = await CallToolText("decompile", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["type_name"] = pair.Type.FullName!,
            ["member_name"] = pair.Event.Name,
        });
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.Contains(pair.Event.Name, text);
        // Anchor on the `event` keyword followed by whitespace — the decompiler
        // emits this for both field-like (`event T Foo;`) and custom-accessor
        // (`event T Foo { add { ... } }`) forms.
        Assert.Matches(@"\bevent\s+", text);
    }

    [Fact]
    public async Task DecompileMethod_WithParameterTypesMatchingNoOverload_ReturnsError()
    {
        // The regression that matters most: a disambiguation hint is a filter that must be
        // satisfied, not a tie-breaker only consulted when several candidates survive.
        // DisambiguateMethod used to return early on a single candidate, so asking
        // Shapes.Circle for Equals(object) — which does not exist — handed back Equals(Circle)
        // and read as proof of value equality: the exact opposite of the truth.
        // A single-overload method is therefore the essential shape to test.
        var method = SingleOverloadMethod(excludingArity: ImpossibleArity);

        var (result, text) = await CallToolExpectingError("decompile", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["type_name"] = method.DeclaringType!.FullName!,
            ["member_name"] = method.Name,
            ["parameter_types"] = Enumerable.Repeat("object", ImpossibleArity).ToArray(),
        });

        Assert.True(result.IsError,
            $"Expected a no-matching-overload rejection rather than a substituted overload, got: {text}");
        Assert.Contains("No overload", text);
        // The error must name what *does* exist — an accurate negative is the result the
        // caller was after, so it has to carry enough to act on.
        Assert.Contains("Available", text);
        Assert.Contains(method.Name, text);
    }

    [Fact]
    public async Task DecompileMethod_WithParameterCountMatchingNoOverload_ReturnsError()
    {
        // Companion to the test above: parameter_count is the other hint that was skipped
        // for a lone candidate, and it takes a separate branch in DisambiguateMethod.
        var method = SingleOverloadMethod(excludingArity: ImpossibleArity);

        var (result, text) = await CallToolExpectingError("decompile", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["type_name"] = method.DeclaringType!.FullName!,
            ["member_name"] = method.Name,
            ["parameter_count"] = ImpossibleArity,
        });

        Assert.True(result.IsError,
            $"Expected a no-matching-overload rejection rather than a substituted overload, got: {text}");
        Assert.Contains("No overload", text);
        Assert.Contains("Available", text);
    }

    [Fact]
    public async Task DecompileMethod_WithHintMatchingOnlyABaseOverload_ResolvesToTheBaseMethod()
    {
        // C# overload resolution spans the inheritance chain. When the requested type declares
        // Bar(int) and a base declares Bar(string), asking for Bar(string) must not stop at the
        // first level that has the *name*: resolution keeps walking to the base, so the base-only
        // overload resolves and decompiles, tagged with its inherited origin.
        var (derived, name, baseOverload) = MethodWithBaseOnlyOverload();
        var paramTypes = baseOverload.GetParameters()
            .Select(p => CSharpKeyword(p.ParameterType.Name) ?? p.ParameterType.Name)
            .ToArray();

        string text = await CallToolText("decompile", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["type_name"] = derived.FullName!,
            ["member_name"] = name,
            ["parameter_types"] = paramTypes,
        });

        // A body brace proves resolution reached a real method instead of erroring.
        Assert.Contains("{", text);
        // The header anchors on the requested type but discloses the inherited origin — the
        // base type where the resolved overload actually lives.
        Assert.Contains($"// {derived.FullName}.{name}(", text);
        Assert.Contains($"[inherited from {baseOverload.DeclaringType!.FullName}]", text);
    }

    [Fact]
    public async Task DecompileMethod_Header_DisclosesTheResolvedSignature()
    {
        // Defense in depth for the regression above: a bare "// Type.Method" header cannot
        // distinguish one overload from another, so a caller has no way to notice a
        // substitution. The header carries the full resolved signature instead.
        var method = SingleOverloadMethod(excludingArity: ImpossibleArity);

        string text = await CallToolText("decompile", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["type_name"] = method.DeclaringType!.FullName!,
            ["member_name"] = method.Name,
        });

        // Parameter list and return arrow, not just the bare name.
        Assert.Contains($"// {method.DeclaringType!.FullName}.{method.Name}(", text);
        Assert.Contains("→", text);
    }

    private const string HintNeedsMember =
        "parameter_count, parameter_types and return_type select a method overload, so they need member_name.";

    [Theory]
    [InlineData("parameter_name", "count", "parameter_name and non_default_only apply only to kind 'call_sites'.")]
    [InlineData("non_default_only", true, "parameter_name and non_default_only apply only to kind 'call_sites'.")]
    [InlineData("parameter_count", 0, HintNeedsMember)]
    [InlineData("return_type", "bool", HintNeedsMember)]
    public async Task Analyze_WithAParameterTheQuestionCannotUse_ReturnsError(string parameter, object value, string expected)
    {
        // Dropped silently, the parameter would leave the answer to a broader question looking
        // like the answer to the one asked.
        var (result, text) = await CallToolExpectingError("analyze", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["type_name"] = "ICSharpCode.Decompiler.DecompilerSettings",
            ["kind"] = "used_by",
            [parameter] = value,
        });

        Assert.True(result.IsError, $"Expected an error, got: {text}");
        Assert.Contains(expected, text);
    }

    [Theory]
    [InlineData("parameter_count", 0)]
    [InlineData("return_type", "bool")]
    public async Task Decompile_WithAnOverloadHintButNoMember_ReturnsError(string parameter, object value)
    {
        // A hint selects a method overload. Without member_name it would be dropped silently
        // and the whole type returned as if that were the answer.
        var (result, text) = await CallToolExpectingError("decompile", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["type_name"] = "ICSharpCode.Decompiler.DecompilerSettings",
            [parameter] = value,
        });

        Assert.True(result.IsError, $"Expected an error, got: {text}");
        Assert.Contains(HintNeedsMember, text);
    }

    [Fact]
    public async Task FindMethods_WithANamespaceThatDoesNotExist_ReturnsError()
    {
        // An exact namespace asserts it exists; answering a misspelling with "No methods"
        // would read as a namespace without such methods.
        var (result, text) = await CallToolExpectingError("find_methods", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["namespace"] = "ICSharpCode.Decompiler.TypeSystm",
        });

        Assert.True(result.IsError, $"Expected an error, got: {text}");
        Assert.Contains("Namespace not found: ICSharpCode.Decompiler.TypeSystm.", text);
    }

    [Fact]
    public async Task FindMethods_WithUnknownArgument_ReturnsErrorNamingValidArguments()
    {
        // Names find_methods does not take: typeName in camelCase, and method_name, which no
        // tool takes. The SDK drops unrecognized keys, and because every find_methods filter is
        // optional, such a call would run unfiltered and return the whole assembly — a wrong
        // answer wearing the shape of a right one.
        var (result, text) = await CallToolExpectingError("find_methods", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["typeName"] = "ICSharpCode.Decompiler.DecompilerSettings",
            ["method_name"] = "Equals",
        });

        Assert.True(result.IsError,
            $"Expected unrecognized arguments to be rejected rather than ignored, got: {text}");
        Assert.Contains("Unknown argument", text);
        Assert.Contains("typeName", text);
        Assert.Contains("method_name", text);
        // Naming the valid set is what turns this into a one-round-trip fix.
        Assert.Contains("name_contains", text);
        Assert.Contains("type_name", text);
    }

    [Fact]
    public async Task FindMethods_ByExactDeclaringType_ReturnsOnlyThatTypesMethods()
    {
        var type = TypeWithSeveralPlainMethods();

        string text = await CallToolText("find_methods", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["type_name"] = type.FullName!,
            ["accessibility"] = "all",
            ["limit"] = 200,
        });

        var resultLines = FindMethodsResultLines(text);
        Assert.NotEmpty(resultLines);
        Assert.All(resultLines, line => Assert.StartsWith($"{type.FullName}.", line));
    }

    [Fact]
    public async Task FindMethods_WithNonexistentDeclaringType_ReturnsError()
    {
        // An exact name is a claim that the type exists. Answering a typo with
        // "No methods match" reads as "that type has no such methods".
        var (result, text) = await CallToolExpectingError("find_methods", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["type_name"] = "ICSharpCode.Decompiler.ThisTypeDoesNotExist",
        });

        Assert.True(result.IsError, $"Expected a type-not-found rejection, got: {text}");
        Assert.Contains("Type not found", text);
    }

    [Fact]
    public async Task FindMethods_WithBothDeclaringTypeAndPattern_ReturnsError()
    {
        var (result, text) = await CallToolExpectingError("find_methods", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["type_name"] = "ICSharpCode.Decompiler.DecompilerSettings",
            ["type_name_contains"] = "Decompiler",
        });

        Assert.True(result.IsError, $"Expected a mutually-exclusive rejection, got: {text}");
        Assert.Contains("mutually exclusive", text);
    }

    [Fact]
    public async Task TypeNameParameters_OnANestedType_AcceptReflectionAndDottedForm()
    {
        // .NET reflection names a nested type Outer+Inner, ILens prints Outer.Inner. Both
        // name the same type, so both must resolve to the same answer.
        string reflectionForm = NestedTypeWithMethods().FullName!;
        string dottedForm = reflectionForm.Replace('+', '.');

        foreach (var (tool, parameter) in new[] { ("list_members", "type_name"), ("find_methods", "type_name") })
        {
            string viaDotted = await CallToolText(tool, new()
            {
                ["assembly"] = _server.AssemblyPath,
                [parameter] = dottedForm,
                ["accessibility"] = "all",
            });
            string viaReflection = await CallToolText(tool, new()
            {
                ["assembly"] = _server.AssemblyPath,
                [parameter] = reflectionForm,
                ["accessibility"] = "all",
            });

            Assert.Contains(dottedForm, viaDotted);
            Assert.Equal(viaDotted, viaReflection);
        }
    }

    [Fact]
    public async Task FindMethods_WithANestedReturnType_MatchesReflectionAndDottedForm()
    {
        // A type pattern may be a full name, so a nested one comes in either form too.
        var method = MethodReturningANestedType();
        string reflectionForm = method.ReturnType.FullName!;

        var results = new List<string>();
        foreach (string pattern in new[] { reflectionForm.Replace('+', '.'), reflectionForm })
        {
            results.Add(await CallToolText("find_methods", new()
            {
                ["assembly"] = _server.AssemblyPath,
                ["name_contains"] = method.Name,
                ["return_type"] = pattern,
                ["accessibility"] = "all",
            }));
        }

        Assert.Contains($".{method.Name}(", results[0]);
        Assert.Equal(results[0], results[1]);
    }

    [Fact]
    public async Task TypeName_SharedWithAGenericType_ResolvesByArity()
    {
        // Like Task and Task<TResult>, the two types share a name apart from their arity.
        // Without type parameters the name means the non-generic type, as in C# and
        // reflection; every spelling of the arity reaches the generic one.
        var (plain, generic) = NonGenericTypeWithAGenericNamesake();
        string bare = plain.FullName!;
        string unbound = UnboundName(generic);

        Assert.Equal(bare, DeclaredTypeName(await ListAllMembers(bare)));

        string viaUnbound = await ListAllMembers(unbound);
        string parameterNames = string.Join(", ", generic.GetGenericArguments().Select(a => a.Name));
        Assert.Equal($"{bare}<{parameterNames}>", DeclaredTypeName(viaUnbound));
        foreach (string spelling in new[] { generic.FullName!, $"{bare}<{parameterNames}>" })
            Assert.Equal(viaUnbound, await ListAllMembers(spelling));
    }

    [Fact]
    public async Task TypeName_SharedOnlyByGenericTypes_IsAnErrorListingThem()
    {
        // As with System.Func, no type of that name is non-generic, so the name alone
        // cannot say which one is meant.
        var group = SharedNameGroups().FirstOrDefault(g => g.All(t => t.IsGenericTypeDefinition))
            ?? throw new InvalidOperationException(
                "No name shared only by generic types found in ICSharpCode.Decompiler.dll — " +
                "the test target's API drifted; pick a different assembly or test shape.");

        var (result, text) = await CallToolExpectingError("list_members", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["type_name"] = group[0].FullName!.Split('`')[0],
        });

        Assert.True(result.IsError, $"Expected an ambiguity rejection, got: {text}");
        Assert.All(group, type => Assert.Contains(UnboundName(type), text));
    }

    [Fact]
    public async Task FindTypes_OnASharedName_ListsEachTypeByItsArity()
    {
        var (plain, generic) = NonGenericTypeWithAGenericNamesake();

        string text = await CallToolText("find_types", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["name_contains"] = plain.Name,
            ["accessibility"] = "all",
        });

        var lines = text.Split('\n');
        Assert.Contains(plain.FullName!, lines);
        Assert.Contains(UnboundName(generic), lines);
    }

    [Fact]
    public async Task FindMethods_WithADeclaringTypeThatSharesItsName_ListsOnlyThatType()
    {
        var (plain, generic) = NonGenericTypeWithAGenericNamesake();

        foreach (string declaringType in new[] { plain.FullName!, UnboundName(generic) })
        {
            string text = await CallToolText("find_methods", new()
            {
                ["assembly"] = _server.AssemblyPath,
                ["type_name"] = declaringType,
                ["accessibility"] = "all",
            });

            var resultLines = FindMethodsResultLines(text);
            Assert.NotEmpty(resultLines);
            Assert.All(resultLines, line => Assert.StartsWith($"{declaringType}.", line));
        }
    }

    [Fact]
    public async Task FindMethods_WithAnArgumentListPattern_ComparesTheArguments()
    {
        // A parameter type written as ILens prints it must find the method it came from, a
        // different argument must not, and the unbound form matches any arguments.
        var method = MethodWithOneConstructedGenericParameter();
        var parameterType = method.GetParameters()[0].ParameterType;
        string genericName = parameterType.Name.Split('`')[0];
        var arguments = parameterType.GetGenericArguments();

        async Task<string> FindWith(string pattern) => await CallToolText("find_methods", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["name_contains"] = method.Name,
            ["parameter_types"] = new[] { pattern },
            ["accessibility"] = "all",
        });

        Assert.Contains($".{method.Name}(", await FindWith(RenderTypeReference(parameterType)));
        Assert.Contains($".{method.Name}(",
            await FindWith($"{genericName}<{new string(',', arguments.Length - 1)}>"));
        Assert.DoesNotContain($".{method.Name}(", await FindWith(
            $"{genericName}<{string.Join(", ", arguments.Select(_ => "ThisTypeDoesNotExist"))}>"));
    }

    [Fact]
    public async Task FindMethods_WithAParameterTypeNestedInAGenericType_FindsItAsPrinted()
    {
        // A type nested in a generic type takes that type's arguments, as
        // Dictionary<TKey, TValue>.Entry does. ILens writes each argument on the segment that
        // declares it, as C# does, and the printed type finds the method it came from.
        var method = MethodWithOneParameterNestedInAGenericType();
        string printed = RenderTypeReference(method.GetParameters()[0].ParameterType);

        async Task<string> FindMethods(Dictionary<string, object?> filters) =>
            await CallToolText("find_methods", new(filters)
            {
                ["assembly"] = _server.AssemblyPath,
                ["type_name"] = UnboundName(method.DeclaringType!),
                ["name_contains"] = method.Name,
                ["accessibility"] = "all",
            });

        string listing = await FindMethods(new());
        Assert.Contains($".{method.Name}({printed})", listing);
        string line = listing.Split('\n').First(l => l.Contains($".{method.Name}({printed})"));

        string found = await FindMethods(new() { ["parameter_types"] = new[] { printed } });
        Assert.Contains(line, found.Split('\n'));
    }

    [Fact]
    public async Task FindMethods_WithAFullyQualifiedPatternOfATypeFromAnotherAssembly_FindsTheMethod()
    {
        // ILens reads an assembly without the ones it references, so a generic type from
        // another assembly, such as ImmutableArray<T>, is known only by the reference to it,
        // and in code with nullable annotations a reference type also carries its annotation.
        // Its full name states the arity all the same, in each of the three forms.
        foreach (var method in MethodsWithOneImmutableCollectionParameter())
        {
            var parameterType = method.GetParameters()[0].ParameterType;
            string fullName = parameterType.GetGenericTypeDefinition().FullName!.Split('`')[0];
            var arguments = parameterType.GetGenericArguments();

            foreach (string pattern in new[]
            {
                $"{fullName}<{string.Join(", ", arguments.Select(RenderTypeReference))}>",
                $"{fullName}<{new string(',', arguments.Length - 1)}>",
                $"{fullName}`{arguments.Length}",
            })
            {
                string text = await CallToolText("find_methods", new()
                {
                    ["assembly"] = _server.AssemblyPath,
                    ["type_name"] = method.DeclaringType!.FullName,
                    ["name_contains"] = method.Name,
                    ["parameter_types"] = new[] { pattern },
                    ["accessibility"] = "all",
                });
                Assert.Contains($".{method.Name}(", text);
            }
        }
    }

    [Fact]
    public async Task Analyze_ImplementedBy_OnInterfacesSharingAName_KeepsTheirImplementersApart()
    {
        // Like IComparable and IComparable<T>, the interfaces share a name apart from their
        // arity, and implementing one of them is not implementing the others.
        var sameName = SharedNameGroups().FirstOrDefault(g => g.Count(t => t.IsInterface) > 1)
            ?? throw new InvalidOperationException(
                "No name shared by several interfaces found in ICSharpCode.Decompiler.dll — " +
                "the test target's API drifted; pick a different assembly or test shape.");
        var interfaces = sameName.Where(t => t.IsInterface).ToArray();
        static bool Implements(Type type, Type iface) => type.GetInterfaces()
            .Any(i => (i.IsGenericType ? i.GetGenericTypeDefinition() : i) == iface);
        // The types of that name, and every type implementing one of the interfaces.
        var candidates = typeof(ICSharpCode.Decompiler.CSharp.CSharpDecompiler).Assembly.GetTypes()
            .Where(t => !t.IsNested && !t.Name.StartsWith('<')
                && (sameName.Contains(t) || interfaces.Any(i => Implements(t, i))))
            .ToList();

        int implementersChecked = 0;
        foreach (var iface in interfaces)
        {
            string text = await CallToolText("analyze", new()
            {
                ["assembly"] = _server.AssemblyPath,
                ["type_name"] = UnboundName(iface),
                ["kind"] = "implemented_by",
                ["limit"] = 10_000,
            });

            var listed = ResultBody(text).Split('\n');
            // The candidates include the interface itself, which does not implement itself.
            foreach (var type in candidates)
            {
                bool implements = Implements(type, iface);
                bool isListed = listed.Contains(UnboundName(type));
                Assert.True(isListed == implements, $"{UnboundName(type)} is {(isListed ? "" : "not ")}" +
                    $"listed among the implementers of {UnboundName(iface)}.");
                if (implements)
                    implementersChecked++;
            }
        }
        Assert.True(implementersChecked > 0, "Found no implementer of the interfaces to check.");
    }

    [Fact]
    public async Task FindMethods_OnATupleTypedParameter_PrintsAndMatchesItsElementTypes()
    {
        // CoreLib defines System.ValueTuple, so ILSpy reads a tuple in its signatures as a tuple
        // type, where in an assembly that only references ValueTuple it stays a constructed
        // ValueTuple<...>. Both print alike, with the element types, and the printed type finds
        // the method again.
        var method = CoreLibMethodWithOneParameter("tuple-typed", IsOrContainsTuple);
        string printed = RenderTypeReference(method.GetParameters()[0].ParameterType);

        Assert.Contains($".{method.Name}({printed})", await FindCoreLibMethods(method, new()));
        Assert.Contains($".{method.Name}(",
            await FindCoreLibMethods(method, new() { ["parameter_types"] = new[] { printed } }));
    }

    [Fact]
    public async Task FindMethods_OnAFunctionPointerParameter_PrintsAndMatchesItsSignature()
    {
        // A function pointer's type is its signature, so it prints as C# writes it.
        var method = CoreLibMethodWithOneParameter("function-pointer", type => type.IsFunctionPointer
            && !type.GetFunctionPointerParameterTypes().Append(type.GetFunctionPointerReturnType()).Any(t => t.IsByRef));
        string printed = RenderFunctionPointer(method.GetParameters()[0]);

        Assert.Contains($".{method.Name}({printed})", await FindCoreLibMethods(method, new()));
        Assert.Contains($".{method.Name}(",
            await FindCoreLibMethods(method, new() { ["parameter_types"] = new[] { printed } }));
    }

    [Fact]
    public async Task FindMethods_OnAPointerParameter_PrintsAndMatchesItWithTheKeyword()
    {
        var method = CoreLibMethodWithOneParameter("void*", type => type.IsPointer && type.GetElementType() == typeof(void));

        Assert.Contains($".{method.Name}(void*)", await FindCoreLibMethods(method, new()));
        Assert.Contains($".{method.Name}(",
            await FindCoreLibMethods(method, new() { ["parameter_types"] = new[] { "void*" } }));
    }

    [Theory]
    [InlineData(typeof(IntPtr), "nint", "nint")]
    [InlineData(typeof(IntPtr), "nint", "IntPtr")]
    [InlineData(typeof(IntPtr), "nint", "System.IntPtr")]
    [InlineData(typeof(UIntPtr), "nuint", "nuint")]
    [InlineData(typeof(UIntPtr), "nuint", "UIntPtr")]
    [InlineData(typeof(UIntPtr), "nuint", "System.UIntPtr")]
    public async Task FindMethods_OnANativeIntegerParameter_PrintsTheKeywordAndMatchesEveryName(
        Type type, string keyword, string pattern)
    {
        // C# 11 made nint and nuint the keywords for System.IntPtr and System.UIntPtr. ILSpy
        // reads either as a type named after the keyword, and the .NET names find it all the same.
        var method = CoreLibMethodWithOneParameter(keyword, parameterType => parameterType == type);

        Assert.Contains($".{method.Name}({keyword})",
            await FindCoreLibMethods(method, new() { ["parameter_types"] = new[] { pattern } }));
    }

    [Fact]
    public async Task DecompileMethod_OnAnIndexerAccessorWithAHint_ResolvesTheIndexerItNames()
    {
        // Every indexer's getter is get_Item and every setter set_Item, so the hint tells them
        // apart as it tells any overloads apart. A setter takes the value after the indices.
        var (type, indexers) = CoreLibTypeWithTwoIndexers();
        foreach (var indexer in indexers)
        {
            string[] indices = indexer.GetIndexParameters().Select(p => RenderTypeReference(p.ParameterType)).ToArray();
            string getter = $"// {type.FullName}.get_Item({string.Join(", ", indices)}) → ";
            Assert.StartsWith(getter, await DecompileCoreLibMethod(type, "get_Item", new() { ["parameter_types"] = indices }));
            Assert.StartsWith(getter, await DecompileCoreLibMethod(type, "get_Item", new() { ["parameter_count"] = indices.Length }));

            string[] setterParameters = [.. indices, RenderTypeReference(indexer.PropertyType)];
            Assert.StartsWith($"// {type.FullName}.set_Item({string.Join(", ", setterParameters)}) → ",
                await DecompileCoreLibMethod(type, "set_Item", new() { ["parameter_types"] = setterParameters }));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("ThisTypeDoesNotExist")]
    public async Task DecompileMethod_OnAnIndexerAccessorWithoutAMatchingHint_ListsEveryIndexer(string? parameterType)
    {
        // Without a hint, or with one no indexer satisfies, the answer names every getter
        // rather than returning whichever indexer comes first.
        var (type, indexers) = CoreLibTypeWithTwoIndexers();
        var arguments = new Dictionary<string, object?>
        {
            ["assembly"] = _server.CoreLibPath,
            ["type_name"] = type.FullName!,
            ["member_name"] = "get_Item",
        };
        if (parameterType is not null)
            arguments["parameter_types"] = new[] { parameterType };

        var (result, text) = await CallToolExpectingError("decompile", arguments);

        Assert.True(result.IsError, $"Expected an error naming every indexer, got: {text}");
        foreach (var indexer in indexers)
        {
            string indices = string.Join(", ", indexer.GetIndexParameters().Select(p => RenderTypeReference(p.ParameterType)));
            Assert.Contains($"{type.FullName}.get_Item({indices})", text);
        }
    }

    [Fact]
    public async Task FindCallSites_OnAnIndexerGetterWithoutAHint_CoversEveryIndexer()
    {
        var (type, indexers) = CoreLibTypeWithTwoIndexers();

        string text = await CallToolText("analyze", new()
        {
            ["assembly"] = _server.CoreLibPath,
            ["type_name"] = type.FullName!,
            ["member_name"] = "get_Item",
            ["kind"] = "call_sites",
        });

        Assert.Contains($"{type.FullName}.get_Item ({indexers.Length} overloads)", text.Split('\n')[0]);
    }

    [Fact]
    public async Task ListMembers_OnATypeWithSeveralIndexers_ListsEachWithItsIndices()
    {
        // Every indexer of a type shares one name, so only the indices tell them apart, both in
        // which members are listed and in how each is printed.
        var (type, indexers) = CoreLibTypeWithTwoIndexers();

        string text = await CallToolText("list_members", new()
        {
            ["assembly"] = _server.CoreLibPath,
            ["type_name"] = type.FullName!,
            ["kinds"] = new[] { "property" },
            ["name_contains"] = indexers[0].Name,
        });

        Assert.Equal(indexers.Length, text.Split('\n').Count(line => line.Contains($" {indexers[0].Name}[")));
        foreach (var indexer in indexers)
        {
            string indices = string.Join(", ", indexer.GetIndexParameters().Select(p => $"{RenderTypeReference(p.ParameterType)} {p.Name}"));
            Assert.Contains($" {RenderTypeReference(indexer.PropertyType)} {indexer.Name}[{indices}] {{ get; set; }}", text);
        }
    }

    [Fact]
    public async Task ListMembers_OnAVolatileFieldOfAGenericType_PrintsItsTypeArguments()
    {
        // The compiler marks a volatile field's type with a required modifier, which ILSpy reads
        // as a type wrapped around the field's type. The wrapped type keeps its arguments.
        var (type, field) = CoreLibVolatileFieldOfAGenericType();

        string text = await CallToolText("list_members", new()
        {
            ["assembly"] = _server.CoreLibPath,
            ["type_name"] = UnboundName(type),
            ["kinds"] = new[] { "field" },
            ["accessibility"] = "all",
        });

        string line = text.Split('\n').Single(l => l.EndsWith($" {field.Name}"));
        Assert.Contains($" {RenderTypeReference(field.FieldType)} ", line);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListMembers_WithInherited_MarksWhereEachMemberIsDeclaredAndListsAnOverrideOnce(bool genericBase)
    {
        // An inherited member carries the base class that declares it, and the type's own
        // members carry nothing. An override hides the declaration it overrides, also on a
        // generic base, which declares the method in its type parameters where the override
        // takes the type arguments the class gives them.
        var (type, own, overrides, inherited) = ClassWithInheritedMembers(genericBase);

        string text = await CallToolText("list_members", new()
        {
            ["assembly"] = type.Assembly == typeof(object).Assembly ? _server.CoreLibPath : _server.AssemblyPath,
            ["type_name"] = type.FullName!,
            ["accessibility"] = "all",
            ["include_inherited"] = true,
            ["limit"] = 10000,
        });

        // A member's name follows a space and precedes its parameters, its accessors, a
        // constant's value, the origin mark or the end of the line.
        var lines = text.Split('\n');
        string LineOf(MemberInfo member)
        {
            string name = " " + member.Name;
            var matching = lines.Where(line => line.EndsWith(name, StringComparison.Ordinal)
                || new[] { "(", " {", " = ", "  [from " }.Any(next => line.Contains(name + next))).ToArray();
            Assert.True(matching.Length == 1,
                $"Expected one line for {member.DeclaringType}.{member.Name}, found {matching.Length}:\n{string.Join("\n", matching)}");
            return matching[0];
        }
        foreach (var member in own.Concat(overrides))
            Assert.DoesNotContain("[from ", LineOf(member));
        foreach (var member in inherited)
            Assert.EndsWith($"  [from {UnboundName(member.DeclaringType!)}]", LineOf(member));
    }

    /// <summary>
    /// find_methods on CoreLib for the methods named like <paramref name="method"/> on its
    /// declaring type, with <paramref name="filters"/> added.
    /// </summary>
    private Task<string> FindCoreLibMethods(MethodInfo method, Dictionary<string, object?> filters) =>
        CallToolText("find_methods", new(filters)
        {
            ["assembly"] = _server.CoreLibPath,
            ["type_name"] = method.DeclaringType!.FullName,
            ["name_contains"] = method.Name,
            ["accessibility"] = "all",
        });

    /// <summary>
    /// decompile on CoreLib for <paramref name="methodName"/> on <paramref name="type"/>,
    /// with the overload hint in <paramref name="hint"/>.
    /// </summary>
    /// <summary>
    /// The method lines of a find_methods answer, without the header, the truncation line or
    /// the static modifier, so each starts with the method's declaring type.
    /// </summary>
    [Fact]
    public async Task FindMethods_CutByLimit_CountsEveryMethodAndNamesHowManyAreNotShown()
    {
        var type = TypeWithSeveralPlainMethods();
        var arguments = new Dictionary<string, object?>
        {
            ["assembly"] = _server.AssemblyPath,
            ["type_name"] = type.FullName!,
            ["accessibility"] = "all",
        };
        string all = await CallToolText("find_methods", arguments);
        int total = FindMethodsResultLines(all).Count;
        Assert.True(total > 1, $"Expected several methods on {type.FullName}, got: {all}");

        string cut = await CallToolText("find_methods", new(arguments) { ["limit"] = 1 });

        var lines = cut.Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.Equal($"{total} methods:", lines[0]);
        Assert.Equal($"... ({total - 1} more; raise limit to see all)", lines[2]);
    }

    private static List<string> FindMethodsResultLines(string text) =>
        text.Split('\n')
            .Skip(1)
            .Where(line => !line.StartsWith("... ("))
            .Select(line => line.StartsWith("static ") ? line["static ".Length..] : line)
            .ToList();

    private Task<string> DecompileCoreLibMethod(Type type, string methodName, Dictionary<string, object?> hint) =>
        CallToolText("decompile", new(hint)
        {
            ["assembly"] = _server.CoreLibPath,
            ["type_name"] = type.FullName!,
            ["member_name"] = methodName,
        });

    /// <summary>
    /// A top-level, non-generic type in CoreLib with two public read-write indexers that take
    /// different numbers of indices, and those indexers, fewest indices first.
    /// </summary>
    private static (Type Type, PropertyInfo[] Indexers) CoreLibTypeWithTwoIndexers()
    {
        foreach (var type in typeof(object).Assembly.GetExportedTypes().Where(t => !t.IsGenericType && !t.IsNested))
        {
            var indexers = type.GetProperties(PublicDeclared)
                .Where(p => p.GetIndexParameters().Length > 0
                    && p.GetMethod is { IsPublic: true } && p.SetMethod is { IsPublic: true })
                .OrderBy(p => p.GetIndexParameters().Length)
                .ToArray();
            if (indexers.Length == 2
                && indexers[0].GetIndexParameters().Length != indexers[1].GetIndexParameters().Length)
                return (type, indexers);
        }
        throw new InvalidOperationException(
            "No type with two public read-write indexers of different arity found in System.Private.CoreLib — " +
            "the runtime's API drifted; pick a different assembly or test shape.");
    }

    /// <summary>
    /// A method in CoreLib, declared on a top-level, non-generic type and not generic itself, with
    /// one parameter, not passed by reference, whose type <paramref name="parameterType"/> accepts.
    /// </summary>
    private static MethodInfo CoreLibMethodWithOneParameter(string shape, Func<Type, bool> parameterType) =>
        typeof(object).Assembly.GetTypes()
            .Where(t => !t.IsGenericType && !t.IsNested && !t.Name.StartsWith('<'))
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .FirstOrDefault(m => !m.IsSpecialName && !m.IsGenericMethod && !m.Name.StartsWith('<')
                && m.GetParameters() is [{ ParameterType: { IsByRef: false } p }] && parameterType(p))
            ?? throw new InvalidOperationException(
                $"No method with one {shape} parameter found in System.Private.CoreLib — " +
                "the runtime's API drifted; pick a different assembly or test shape.");

    /// <summary>A System.ValueTuple type, or a type built from one, such as a list of pairs.</summary>
    private static bool IsOrContainsTuple(Type type) =>
        type.HasElementType
            ? IsOrContainsTuple(type.GetElementType()!)
            : type.IsConstructedGenericType
              && (type.GetGenericTypeDefinition().FullName!.StartsWith("System.ValueTuple`", StringComparison.Ordinal)
                  || type.GetGenericArguments().Any(IsOrContainsTuple));

    /// <summary>
    /// A volatile field in CoreLib whose type is generic, declared on a top-level generic type.
    /// </summary>
    private static (Type Type, FieldInfo Field) CoreLibVolatileFieldOfAGenericType()
    {
        const BindingFlags declared = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var type in typeof(object).Assembly.GetTypes()
                     .Where(t => t.IsGenericTypeDefinition && !t.IsNested && !t.Name.StartsWith('<')))
        {
            var field = type.GetFields(declared).FirstOrDefault(f => f.FieldType.IsGenericType
                && f.GetRequiredCustomModifiers().Contains(typeof(System.Runtime.CompilerServices.IsVolatile)));
            if (field != null)
                return (type, field);
        }
        throw new InvalidOperationException(
            "No volatile field of a generic type found in System.Private.CoreLib — " +
            "the runtime's API drifted; pick a different assembly or test shape.");
    }

    /// <summary>
    /// A function pointer the way ILens prints it, as C# writes it, parameter types first and the
    /// return type last: <c>delegate* unmanaged[Cdecl]&lt;int, void&gt;</c>.
    /// </summary>
    private static string RenderFunctionPointer(ParameterInfo parameter)
    {
        var type = parameter.ParameterType;
        var conventions = parameter.GetModifiedParameterType().GetFunctionPointerCallingConventions()
            .Select(convention => convention.Name["CallConv".Length..])
            .ToList();
        string callingConvention = !type.IsUnmanagedFunctionPointer ? ""
            : conventions.Count == 0 ? " unmanaged"
            : $" unmanaged[{string.Join(", ", conventions)}]";
        var components = type.GetFunctionPointerParameterTypes().Append(type.GetFunctionPointerReturnType());
        return $"delegate*{callingConvention}<{string.Join(", ", components.Select(RenderTypeReference))}>";
    }

    /// <summary>
    /// The name of the type a list_members answer is about, as its header's declaration writes
    /// it: the word before the base types, or before the colon when there are none.
    /// </summary>
    private static string DeclaredTypeName(string listMembersOutput)
    {
        string header = listMembersOutput.Split('\n')[0].TrimEnd(':');
        string declaration = header[(header.IndexOf(" of ", StringComparison.Ordinal) + 4)..];
        int bases = declaration.IndexOf(" : ", StringComparison.Ordinal);
        if (bases >= 0)
            declaration = declaration[..bases];
        return declaration[(declaration.LastIndexOf(' ') + 1)..];
    }

    private Task<string> ListAllMembers(string typeName) => CallToolText("list_members", new()
    {
        ["assembly"] = _server.AssemblyPath,
        ["type_name"] = typeName,
        ["accessibility"] = "all",
    });

    /// <summary>
    /// An arity no discovered test method will have, so a hint built from it is
    /// guaranteed to match nothing.
    /// </summary>
    private const int ImpossibleArity = 7;

    /// <summary>
    /// Find a public method in the test target that is the only overload of its name on its
    /// declaring type — the shape in which a disambiguation hint used to be skipped. Special
    /// names (accessors, operators) are excluded: accessors resolve through
    /// <c>SymbolResolver</c>'s accessor-name fallback, which bypasses overload matching by
    /// design. Discovered rather than hardcoded so the test survives upstream API drift.
    /// </summary>
    private static MethodInfo SingleOverloadMethod(int excludingArity)
    {
        var assembly = typeof(ICSharpCode.Decompiler.CSharp.CSharpDecompiler).Assembly;
        return assembly.GetExportedTypes()
            .Where(t => !t.IsGenericType && !t.IsNested)
            .SelectMany(t => t.GetMethods(PublicDeclared)
                .Where(m => !m.IsGenericMethod && !m.IsSpecialName)
                .GroupBy(m => m.Name)
                .Where(g => g.Count() == 1)
                .Select(g => g.Single()))
            .FirstOrDefault(m => m.GetParameters().Length != excludingArity)
            ?? throw new InvalidOperationException(
                "No single-overload public method found in ICSharpCode.Decompiler.dll — " +
                "the test target's API drifted; pick a different assembly or test shape.");
    }

    /// <summary>
    /// Find a type in the test target declaring several plain (non-accessor) public methods,
    /// so a declaring-type filter has something to return and to exclude.
    /// </summary>
    private static Type TypeWithSeveralPlainMethods()
    {
        var assembly = typeof(ICSharpCode.Decompiler.CSharp.CSharpDecompiler).Assembly;
        return assembly.GetExportedTypes()
            .Where(t => !t.IsGenericType && !t.IsNested)
            .FirstOrDefault(t => t.GetMethods(PublicDeclared)
                .Count(m => !m.IsSpecialName) >= 2)
            ?? throw new InvalidOperationException(
                "No type with several plain public methods found in ICSharpCode.Decompiler.dll — " +
                "the test target's API drifted; pick a different assembly or test shape.");
    }

    /// <summary>
    /// Find a public nested type in the test target that declares a plain public method, so
    /// both a member listing and a declaring-type filter have something to return.
    /// </summary>
    private static Type NestedTypeWithMethods()
    {
        var assembly = typeof(ICSharpCode.Decompiler.CSharp.CSharpDecompiler).Assembly;
        return assembly.GetExportedTypes()
            .Where(IsPlainNestedType)
            .FirstOrDefault(t => t.GetMethods(PublicDeclared).Any(m => !m.IsSpecialName))
            ?? throw new InvalidOperationException(
                "No public nested type with a plain public method found in ICSharpCode.Decompiler.dll — " +
                "the test target's API drifted; pick a different assembly or test shape.");
    }

    /// <summary>
    /// Find a public method in the test target whose return type is a public nested type of
    /// the same assembly, so a return-type pattern can name it.
    /// </summary>
    private static MethodInfo MethodReturningANestedType()
    {
        var assembly = typeof(ICSharpCode.Decompiler.CSharp.CSharpDecompiler).Assembly;
        return assembly.GetExportedTypes()
            .Where(t => !t.IsGenericType)
            .SelectMany(t => t.GetMethods(PublicDeclared))
            .FirstOrDefault(m => !m.IsSpecialName && !m.IsGenericMethod
                && m.ReturnType.Assembly == assembly && IsPlainNestedType(m.ReturnType))
            ?? throw new InvalidOperationException(
                "No public method returning a public nested type found in ICSharpCode.Decompiler.dll — " +
                "the test target's API drifted; pick a different assembly or test shape.");
    }

    /// <summary>
    /// A type nested one level deep in a non-generic type and not generic itself, so its
    /// reflection name differs from its dotted name only in the '+'.
    /// </summary>
    private static bool IsPlainNestedType(Type type) =>
        type.IsNested && !type.IsGenericType
        && type.DeclaringType is { IsGenericType: false, IsNested: false };

    /// <summary>
    /// Groups of top-level types in the test target that share a name apart from their
    /// generic arity, each ordered by arity, longest name first so a substring search on it
    /// stays short. Discovered rather than hardcoded so the tests survive upstream API drift.
    /// </summary>
    private static List<Type[]> SharedNameGroups()
    {
        var assembly = typeof(ICSharpCode.Decompiler.CSharp.CSharpDecompiler).Assembly;
        var groups = assembly.GetTypes()
            .Where(t => !t.IsNested && !t.Name.StartsWith('<'))
            .GroupBy(t => t.FullName!.Split('`')[0])
            .Where(g => g.Count() > 1)
            .OrderByDescending(g => g.Key.Length)
            .Select(g => g.OrderBy(t => t.GetGenericArguments().Length).ToArray())
            .ToList();
        return groups.Count > 0 ? groups : throw new InvalidOperationException(
            "No types sharing a name apart from their arity found in ICSharpCode.Decompiler.dll — " +
            "the test target's API drifted; pick a different assembly or test shape.");
    }

    /// <summary>
    /// A non-generic type and a generic type of the same name, both declaring a method of
    /// their own, so a member listing and a declaring-type filter have something to show.
    /// </summary>
    private static (Type Plain, Type Generic) NonGenericTypeWithAGenericNamesake()
    {
        const BindingFlags declared = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        static bool DeclaresAMethod(Type t) => t.GetMethods(declared).Any(m => !m.IsSpecialName);

        var group = SharedNameGroups().FirstOrDefault(g =>
                !g[0].IsGenericTypeDefinition && DeclaresAMethod(g[0]) && DeclaresAMethod(g[1]))
            ?? throw new InvalidOperationException(
                "No non-generic type with a generic namesake found in ICSharpCode.Decompiler.dll — " +
                "the test target's API drifted; pick a different assembly or test shape.");
        return (group[0], group[1]);
    }

    /// <summary>
    /// Find a method in the test target whose one parameter is a constructed generic type,
    /// preferring one whose arguments are generic themselves, the shape of the
    /// <c>List&lt;ValueTuple&lt;ILInstruction, KnownTypeCode&gt;&gt;</c> parameters ILens prints.
    /// </summary>
    private static MethodInfo MethodWithOneConstructedGenericParameter()
    {
        var assembly = typeof(ICSharpCode.Decompiler.CSharp.CSharpDecompiler).Assembly;
        var candidates = assembly.GetTypes()
            .Where(t => !t.IsGenericType && !t.Name.StartsWith('<'))
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(m => !m.IsSpecialName && !m.IsGenericMethod && !m.Name.StartsWith('<')
                && m.GetParameters() is [{ ParameterType: { IsConstructedGenericType: true } p }]
                && !p.ContainsGenericParameters)
            .ToList();
        return candidates.FirstOrDefault(m => m.GetParameters()[0].ParameterType
                   .GetGenericArguments().Any(a => a.IsConstructedGenericType))
            ?? candidates.FirstOrDefault()
            ?? throw new InvalidOperationException(
                "No method with a constructed generic parameter found in ICSharpCode.Decompiler.dll — " +
                "the test target's API drifted; pick a different assembly or test shape.");
    }

    /// <summary>
    /// A method in the test target with one parameter, whose type is nested in a top-level
    /// generic type and declares no type parameters of its own, the shape of
    /// <c>Dictionary&lt;TKey, TValue&gt;.Entry</c>.
    /// </summary>
    private static MethodInfo MethodWithOneParameterNestedInAGenericType()
    {
        var assembly = typeof(ICSharpCode.Decompiler.CSharp.CSharpDecompiler).Assembly;
        return assembly.GetTypes()
            .Where(t => !t.IsNested && !t.Name.StartsWith('<'))
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .FirstOrDefault(m => !m.IsSpecialName && !m.IsGenericMethod && !m.Name.StartsWith('<')
                && m.GetParameters() is [{ ParameterType: { IsNested: true } p }]
                && p.DeclaringType is { IsGenericType: true, IsNested: false } enclosing
                && p.GetGenericArguments().Length == enclosing.GetGenericArguments().Length)
            ?? throw new InvalidOperationException(
                "No method with a parameter nested in a generic type found in ICSharpCode.Decompiler.dll — " +
                "the test target's API drifted; pick a different assembly or test shape.");
    }

    /// <summary>
    /// Two methods in the test target with one parameter each, a constructed type from
    /// System.Collections.Immutable, an assembly the test target references: one a value
    /// type, the other a reference type with a nullable annotation.
    /// </summary>
    private static MethodInfo[] MethodsWithOneImmutableCollectionParameter()
    {
        var assembly = typeof(ICSharpCode.Decompiler.CSharp.CSharpDecompiler).Assembly;
        var candidates = assembly.GetTypes()
            .Where(t => !t.IsGenericType && !t.Name.StartsWith('<'))
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(m => !m.IsSpecialName && !m.IsGenericMethod && !m.Name.StartsWith('<')
                && m.GetParameters() is [{ ParameterType: { IsConstructedGenericType: true, IsNested: false } p }]
                && p.Namespace == "System.Collections.Immutable"
                && !p.ContainsGenericParameters)
            .ToList();
        var nullability = new NullabilityInfoContext();
        var valueType = candidates.FirstOrDefault(m => m.GetParameters()[0].ParameterType.IsValueType);
        var annotated = candidates.FirstOrDefault(m => !m.GetParameters()[0].ParameterType.IsValueType
            && nullability.Create(m.GetParameters()[0]).ReadState != NullabilityState.Unknown);
        return valueType != null && annotated != null ? [valueType, annotated] : throw new InvalidOperationException(
            "No value-type and annotated reference-type System.Collections.Immutable parameters found in " +
            "ICSharpCode.Decompiler.dll — the test target's API drifted; pick a different assembly or test shape.");
    }

    /// <summary>A generic type definition's name as ILens prints it, e.g. <c>System.Func&lt;,&gt;</c>.</summary>
    private static string UnboundName(Type type)
    {
        int arity = type.GetGenericArguments().Length;
        string name = type.FullName!.Split('`')[0];
        return arity == 0 ? name : $"{name}<{new string(',', arity - 1)}>";
    }

    /// <summary>
    /// A type reference the way ILens prints parameter types: short names, C# keywords for
    /// the framework primitives, generic arguments spelled out, pointers marked with '*', and a
    /// type nested in a top-level generic type, without type parameters of its own, written
    /// with that type: <c>Dictionary&lt;TKey, TValue&gt;.Entry</c>.
    /// </summary>
    private static string RenderTypeReference(Type type)
    {
        if (type.IsPointer)
            return RenderTypeReference(type.GetElementType()!) + "*";
        if (type.IsArray)
            return RenderTypeReference(type.GetElementType()!) + "[]";
        if (!type.IsGenericType)
            return type.Namespace == "System" && CSharpKeyword(type.Name) is { } keyword ? keyword : type.Name;
        string arguments = string.Join(", ", type.GetGenericArguments().Select(RenderTypeReference));
        return type.IsNested && type.DeclaringType!.IsGenericType
            ? $"{type.DeclaringType.Name.Split('`')[0]}<{arguments}>.{type.Name}"
            : $"{type.Name.Split('`')[0]}<{arguments}>";
    }

    /// <summary>
    /// Find, in the test target, a type that declares a method whose name
    /// also has a <em>different</em> overload declared only on a base type in the same
    /// assembly (so the base overload is decompilable), and whose parameter types are simple
    /// enough to express as a decompile hint. Returns the derived type, the shared
    /// method name, and the base-only overload. Discovered rather than hardcoded so the test
    /// survives upstream API drift.
    /// </summary>
    private static (Type Derived, string Name, MethodInfo BaseOverload) MethodWithBaseOnlyOverload()
    {
        var assembly = typeof(ICSharpCode.Decompiler.CSharp.CSharpDecompiler).Assembly;

        static bool IsSimpleParameterType(Type t) =>
            !t.IsGenericType && !t.IsGenericParameter && !t.IsArray
            && !t.IsByRef && !t.IsPointer && !t.ContainsGenericParameters;

        foreach (var derived in assembly.GetExportedTypes()
                     .Where(t => !t.IsNested && !t.IsGenericType))
        {
            var declaredByName = derived.GetMethods(PublicDeclared)
                .Where(m => !m.IsSpecialName && !m.IsGenericMethod)
                .ToLookup(m => m.Name);

            for (var baseType = derived.BaseType;
                 baseType != null && baseType != typeof(object);
                 baseType = baseType.BaseType)
            {
                // The base overload must live in the same DLL, else decompile can't reach it.
                if (baseType.Assembly != assembly || baseType.IsNested || baseType.IsGenericType)
                    continue;

                foreach (var baseMethod in baseType.GetMethods(PublicDeclared)
                             .Where(m => !m.IsSpecialName && !m.IsGenericMethod
                                         && m.GetParameters().Length > 0
                                         && m.GetParameters().All(p => IsSimpleParameterType(p.ParameterType))))
                {
                    // The derived type must declare the *name*, so resolution has to walk past
                    // a level that has it.
                    var siblings = declaredByName[baseMethod.Name].ToList();
                    if (siblings.Count == 0)
                        continue;

                    // Require every derived overload of this name to differ in arity from the
                    // base overload. decompile narrows by exact parameter count first, so
                    // a differing arity guarantees the hint cannot match a derived overload and
                    // resolution must walk to the base — it also implies the base signature isn't
                    // redeclared. This tests an arity-differentiated variant of the shape rather
                    // than a same-arity one, which keeps the discovered target
                    // robust: a same-arity derived overload could share a parameter's short name
                    // with the base and match the loose hint, resolving to the wrong level.
                    var baseArity = baseMethod.GetParameters().Length;
                    if (siblings.Any(s => s.GetParameters().Length == baseArity))
                        continue;

                    return (derived, baseMethod.Name, baseMethod);
                }
            }
        }

        throw new InvalidOperationException(
            "No type declaring a method whose different-signature overload is base-only was found " +
            "in ICSharpCode.Decompiler.dll — the test target's API drifted; pick a different shape.");
    }

    /// <summary>
    /// A top-level, non-generic class, with the members of it and of its base classes in its own
    /// assembly short of System.Object, the chain list_members walks, that a line names
    /// unambiguously: each is the only ordinary member of its name in the chain, apart from a
    /// method override and the base declaration it overrides. The class has own members,
    /// overrides and inherited members. With <paramref name="genericBase"/>, its base class is
    /// generic and an override takes one of that class's type parameters; otherwise no class in
    /// the chain is generic, and the class inherits members of every kind. The test target is
    /// searched first, then CoreLib: no class in the test target inherits an event from a class
    /// in it.
    /// </summary>
    private static (Type Type, MemberInfo[] Own, MethodInfo[] Overrides, MemberInfo[] Inherited)
        ClassWithInheritedMembers(bool genericBase)
    {
        var assemblies = new[] { typeof(ICSharpCode.Decompiler.CSharp.CSharpDecompiler).Assembly, typeof(object).Assembly };
        foreach (var type in assemblies.SelectMany(a => a.GetTypes()).Where(t => t.IsClass && !t.IsNested && !t.IsGenericType))
        {
            var baseTypes = new List<Type>();
            for (var baseType = type.BaseType;
                 baseType != null && baseType != typeof(object) && baseType.Assembly == type.Assembly;
                 baseType = baseType.BaseType)
                baseTypes.Add(baseType.IsGenericType ? baseType.GetGenericTypeDefinition() : baseType);
            if (baseTypes.Count == 0 || baseTypes.Any(b => b.IsNested)
                || (genericBase ? !baseTypes[0].IsGenericTypeDefinition : baseTypes.Any(b => b.IsGenericTypeDefinition)))
                continue;

            var named = baseTypes.Prepend(type).SelectMany(OrdinaryMembers).ToLookup(m => m.Name);
            var own = OrdinaryMembers(type).Where(m => named[m.Name].Count() == 1).ToArray();
            var overrides = OrdinaryMembers(type).OfType<MethodInfo>()
                .Where(m => named[m.Name].Count() == 2 && baseTypes.Contains(BaseDeclaration(m).DeclaringType))
                .ToArray();
            var inherited = baseTypes.SelectMany(OrdinaryMembers).Where(m => named[m.Name].Count() == 1).ToArray();

            bool shaped = genericBase
                ? inherited.Length > 0 && overrides.Any(m =>
                    BaseDeclaration(m).GetParameters().Any(p => p.ParameterType.ContainsGenericParameters))
                : new[] { MemberTypes.Method, MemberTypes.Property, MemberTypes.Field, MemberTypes.Event }
                    .All(kind => inherited.Any(m => m.MemberType == kind));
            if (own.Length > 0 && overrides.Length > 0 && shaped)
                return (type, own, overrides, inherited);
        }

        throw new InvalidOperationException(
            $"No class with a {(genericBase ? "generic" : "non-generic")} base class it overrides and " +
            "inherits members from was found in ICSharpCode.Decompiler.dll or System.Private.CoreLib — " +
            "their API drifted; pick a different shape.");
    }

    /// <summary>
    /// The members <paramref name="type"/> declares that list_members prints on a line a test
    /// can find by name: no accessor or operator, no generic method, whose type parameters follow
    /// its name, no indexer, no compiler-generated member and no explicit interface implementation.
    /// </summary>
    private static IEnumerable<MemberInfo> OrdinaryMembers(Type type)
    {
        const BindingFlags declared = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        MemberInfo[] members =
        [
            .. type.GetMethods(declared).Where(m => !m.IsSpecialName && !m.IsGenericMethodDefinition),
            .. type.GetProperties(declared).Where(p => p.GetIndexParameters().Length == 0),
            .. type.GetFields(declared),
            .. type.GetEvents(declared),
        ];
        return members.Where(m => !m.Name.StartsWith('<') && !m.Name.Contains('.'));
    }

    /// <summary>
    /// The declaration <paramref name="method"/> overrides, as its class declares it, which on a
    /// generic class is in the class's type parameters. The method itself if it overrides nothing.
    /// </summary>
    private static MethodInfo BaseDeclaration(MethodInfo method)
    {
        var root = method.GetBaseDefinition();
        return (MethodInfo)root.Module.ResolveMethod(root.MetadataToken)!;
    }

    /// <summary>
    /// Calls an ILens MCP tool that is expected to fail, returning the raw result alongside
    /// its text so a test can assert on both. Unlike <see cref="CallToolText"/> this does not
    /// fail on an error result — the error <em>is</em> the behavior under test.
    /// </summary>
    private async Task<(CallToolResult Result, string Text)> CallToolExpectingError(
        string toolName, Dictionary<string, object?> arguments)
    {
        CallToolResult result = await _server.Client.CallToolAsync(toolName, arguments);
        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        return (result, text);
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
    /// The result lines of an <c>analyze</c> response, with its header line dropped, and empty
    /// for an empty answer, which is the header line alone. Lets two routes to the same
    /// cross-references be compared without the headers — which name the symbol asked about,
    /// and so differ by construction — forcing a mismatch.
    /// </summary>
    private static string ResultBody(string analyzeOutput)
    {
        int firstBreak = analyzeOutput.IndexOf('\n');
        return firstBreak < 0 ? "" : analyzeOutput[(firstBreak + 1)..];
    }

    /// <summary>
    /// Reflection short-name → C# keyword, for the framework types ILens renders via
    /// <c>ReferenceFormatter.TypeKeyword</c>. Returns <c>null</c> for any other name.
    /// Duplicated here (a few rows) for test isolation rather than reaching into the
    /// production map.
    /// </summary>
    private static string? CSharpKeyword(string reflectionName) => reflectionName switch
    {
        "Boolean" => "bool",    "Byte"    => "byte",    "SByte"   => "sbyte",
        "Char"    => "char",    "Decimal" => "decimal", "Double"  => "double",
        "Single"  => "float",   "Int32"   => "int",     "UInt32"  => "uint",
        "Int64"   => "long",    "UInt64"  => "ulong",   "Int16"   => "short",
        "UInt16"  => "ushort",  "IntPtr"  => "nint",    "UIntPtr" => "nuint",
        "Object"  => "object",  "String"  => "string",  "Void"    => "void",
        _         => null,
    };
}
