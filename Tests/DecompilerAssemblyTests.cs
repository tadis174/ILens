using System.Reflection;
using System.Reflection.Emit;
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
    public async Task SearchTypes_DefaultsToHidingRoslynClosureClasses()
    {
        // <>c is the canonical Roslyn name for the per-method cached-delegate
        // host. Filtered by default — searching for it should return "No types
        // match". With the flag off, the same search hits real entries.
        string filtered = await CallToolText("search_types", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["pattern"] = "<>c",
        });
        Assert.Contains("No types match", filtered);

        string unfiltered = await CallToolText("search_types", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["pattern"] = "<>c",
            ["excludeCompilerGenerated"] = false,
        });
        Assert.DoesNotContain("No types match", unfiltered);
    }

    [Fact]
    public async Task SearchTypes_FiltersBurstDirectCallWrapperNestedType()
    {
        // TASK-22 regression. Burst's source generator emits nested helper types
        // whose names carry `$BurstDirectCall` / `$PostfixBurstDelegate` suffixes
        // (the outer type is a regular user class, so the chain-walk in
        // CompilerGeneratedFilter only catches them via the inner name).
        // '$' isn't a valid C# identifier character, so we can't reach this
        // shape via a C# fixture project — synthesize a tiny PE on disk via
        // PersistedAssemblyBuilder (System.Reflection.Emit, .NET 9+) and
        // exercise the filter through the live MCP server.
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            $"BurstWrapperFixture_{Guid.NewGuid():N}.dll");
        try
        {
            SynthesizeBurstWrapperFixture(fixturePath);

            // Filter on by default — the nested `$BurstDirectCall` type drops out.
            string filtered = await CallToolText("search_types", new()
            {
                ["assembly"] = fixturePath,
                ["pattern"] = "$BurstDirectCall",
            });
            Assert.Contains("No types match", filtered);

            // Filter off — the same search hits the synthesized nested type.
            string unfiltered = await CallToolText("search_types", new()
            {
                ["assembly"] = fixturePath,
                ["pattern"] = "$BurstDirectCall",
                ["excludeCompilerGenerated"] = false,
            });
            Assert.DoesNotContain("No types match", unfiltered);
        }
        finally
        {
            // Best-effort cleanup. The MCP server keeps the file mmapped while
            // its host stays in the cache, so Delete can fail on Windows; the
            // unique GUID in the path means a leftover doesn't collide.
            try { if (File.Exists(fixturePath)) File.Delete(fixturePath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Build a one-type assembly with a Burst-style nested helper and save it
    /// to disk. The outer type is a regular C# class; the inner type's name
    /// carries the `$BurstDirectCall` suffix Unity's Burst source generator
    /// emits, which is what TASK-22 teaches <see cref="ILens.CompilerGeneratedFilter"/>
    /// to recognize.
    /// </summary>
    private static void SynthesizeBurstWrapperFixture(string path)
    {
        var aname = new AssemblyName("BurstWrapperFixture")
        {
            Version = new Version(1, 0, 0, 0),
        };
        var ab = new PersistedAssemblyBuilder(aname, typeof(object).Assembly);
        var mb = ab.DefineDynamicModule("BurstWrapperFixture");

        var container = mb.DefineType(
            "BurstWrapperFixture.Container",
            TypeAttributes.Public | TypeAttributes.Class);
        container.DefineNestedType(
            "Compute_0000ABCD$BurstDirectCall",
            TypeAttributes.NestedPrivate | TypeAttributes.Class)
            .CreateType();
        container.CreateType();

        ab.Save(path);
    }

    [Fact]
    public async Task ListTypes_DropsCountWhenCompilerGeneratedIsExcluded()
    {
        // Any well-trafficked namespace in ICSharpCode.Decompiler.dll has
        // Roslyn helpers (display classes, anonymous types). The filtered set
        // must be strictly smaller — assert the count drops, without naming
        // a specific generated type (whose presence drifts upstream).
        string filtered = await CallToolText("list_types", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["namespaceName"] = "ICSharpCode.Decompiler.IL",
        });
        string unfiltered = await CallToolText("list_types", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["namespaceName"] = "ICSharpCode.Decompiler.IL",
            ["excludeCompilerGenerated"] = false,
        });

        int filteredCount = ParseLeadingCount(filtered);
        int unfilteredCount = ParseLeadingCount(unfiltered);
        Assert.True(unfilteredCount > filteredCount,
            $"Expected the unfiltered count ({unfilteredCount}) to exceed the " +
            $"filtered count ({filteredCount}); the filter would otherwise be a no-op.");
    }

    /// <summary>
    /// Parses the leading "N types:" from list_types output. The tool's first
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
    public async Task Analyze_ReadBy_OnAProperty_MatchesTheGetterCallers()
    {
        // ILSpyX has no property-level usage analyzer, so analyze routes a property
        // read/assign/use query to the accessor and runs "Used By" there. Asserting the
        // two routes agree pins the behavior without hard-coding call sites that drift
        // as the upstream package moves.
        string viaProperty = await CallToolText("analyze", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["typeName"] = "ICSharpCode.Decompiler.DecompilerSettings",
            ["memberName"] = "ThrowOnAssemblyResolveErrors",
            ["kind"] = "ReadBy",
        });
        string viaAccessor = await CallToolText("analyze", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["typeName"] = "ICSharpCode.Decompiler.DecompilerSettings",
            ["memberName"] = "get_ThrowOnAssemblyResolveErrors",
            ["kind"] = "UsedBy",
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
            ["typeName"] = "ICSharpCode.Decompiler.DecompilerSettings",
            ["memberName"] = "ThrowOnAssemblyResolveErrors",
            ["kind"] = "UsedBy",
        });
        Assert.Contains(
            "(via get_ThrowOnAssemblyResolveErrors, set_ThrowOnAssemblyResolveErrors)", text);

        // Every reader must also appear in the union — UsedBy merges the two accessor
        // runs rather than replacing one with the other.
        string readers = await CallToolText("analyze", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["typeName"] = "ICSharpCode.Decompiler.DecompilerSettings",
            ["memberName"] = "ThrowOnAssemblyResolveErrors",
            ["kind"] = "ReadBy",
        });
        // Skip the empty-result sentinel: were upstream drift to leave the getter with no
        // callers, asserting that "(no results)" appears in a non-empty union would fail
        // for a reason that has nothing to do with the merge being tested.
        string readerBody = ResultBody(readers);
        if (readerBody.Trim() != "(no results)")
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
            ["typeName"] = "ICSharpCode.Decompiler.DecompilerSettings",
            ["memberName"] = "ThrowOnAssemblyResolveErrors",
            ["kind"] = "Uses",
        });
        string viaGetter = await CallToolText("analyze", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["typeName"] = "ICSharpCode.Decompiler.DecompilerSettings",
            ["memberName"] = "get_ThrowOnAssemblyResolveErrors",
            ["kind"] = "Uses",
        });

        Assert.Contains(
            "(via get_ThrowOnAssemblyResolveErrors, set_ThrowOnAssemblyResolveErrors)", viaProperty);
        string getterBody = ResultBody(viaGetter);
        if (getterBody.Trim() != "(no results)")
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
                ["typeName"] = "ICSharpCode.Decompiler.DecompilerSettings",
                ["memberName"] = "PropertyChanged",
                ["kind"] = "Uses",
            });

        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        Assert.True(result.IsError, $"Expected Uses-on-event rejection, got: {text}");
        Assert.Contains("not valid for Event", text);
        Assert.Contains("UsedBy", text);
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
                ["typeName"] = "ICSharpCode.Decompiler.CSharp.CSharpDecompiler",
                ["memberName"] = "TypeSystem",
                ["kind"] = "AssignedBy",
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
            ["typeName"] = "ICSharpCode.Decompiler.DecompilerSettings",
            ["memberName"] = "PropertyChanged",
            ["kind"] = "UsedBy",
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
            ["typeName"] = "ICSharpCode.Decompiler.DecompilerSettings",
            ["memberName"] = "PropertyChanged",
            ["kind"] = "OverriddenBy",
        });
        Assert.DoesNotContain("ambiguous", text);
        Assert.Contains("PropertyChanged", text);
    }

    [Fact]
    public async Task ListMembers_AcceptsAScalarWhereTheSchemaDeclaresAnArray()
    {
        // "Field" instead of ["Field"] is a routine slip, and the SDK's binder answers it
        // with a raw serializer failure naming an internal type. There is only one thing a
        // lone value can mean, so ArrayCoercionFilter promotes it — the two calls must be
        // indistinguishable.
        // DecompilerSettings exposes no public or protected field of its own, so widen the
        // accessibility filter — otherwise both calls agree on "no members match" and the
        // comparison proves nothing about which kinds were selected.
        var arguments = new Dictionary<string, object?>
        {
            ["assembly"] = _server.AssemblyPath,
            ["typeName"] = "ICSharpCode.Decompiler.DecompilerSettings",
            ["accessibility"] = "All",
        };
        string scalar = await CallToolText("list_members",
            new(arguments) { ["kinds"] = "Field" });
        string array = await CallToolText("list_members",
            new(arguments) { ["kinds"] = new[] { "Field" } });

        Assert.Equal(array, scalar);
        // Match the section headers, not the bare words — a field named "anonymousMethods"
        // contains "Methods" and would make a looser check pass for the wrong reason.
        Assert.Contains("\nFields (", scalar);
        Assert.DoesNotContain("\nMethods (", scalar);
    }

    [Fact]
    public async Task FindMethods_AcceptsAScalarParameterType()
    {
        // The coercion is driven by each tool's own input schema, not by a list of known
        // parameters, so it has to hold for a string[] on a different tool too.
        var arguments = new Dictionary<string, object?>
        {
            ["assembly"] = _server.AssemblyPath,
            ["declaringType"] = "ICSharpCode.Decompiler.DecompilerSettings",
        };
        string scalar = await CallToolText("find_methods",
            new(arguments) { ["parameterTypes"] = "string" });
        string array = await CallToolText("find_methods",
            new(arguments) { ["parameterTypes"] = new[] { "string" } });

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

    [Fact]
    public async Task DecompileMethod_WithParameterTypesMatchingNoOverload_ReturnsError()
    {
        // The regression that matters most: a disambiguation hint is a filter that must be
        // satisfied, not a tie-breaker only consulted when several candidates survive.
        // DisambiguateMethod used to return early on a single candidate, so asking
        // Verse.Thing for Equals(object) — which does not exist — handed back Equals(Thing)
        // and read as proof of value equality: the exact opposite of the truth.
        // A single-overload method is therefore the essential shape to test.
        var method = SingleOverloadMethod(excludingArity: ImpossibleArity);

        var (result, text) = await CallToolExpectingError("decompile_method", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["typeName"] = method.DeclaringType!.FullName!,
            ["methodName"] = method.Name,
            ["parameterTypes"] = Enumerable.Repeat("object", ImpossibleArity).ToArray(),
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
        // Companion to the test above: parameterCount is the other hint that was skipped
        // for a lone candidate, and it takes a separate branch in DisambiguateMethod.
        var method = SingleOverloadMethod(excludingArity: ImpossibleArity);

        var (result, text) = await CallToolExpectingError("decompile_method", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["typeName"] = method.DeclaringType!.FullName!,
            ["methodName"] = method.Name,
            ["parameterCount"] = ImpossibleArity,
        });

        Assert.True(result.IsError,
            $"Expected a no-matching-overload rejection rather than a substituted overload, got: {text}");
        Assert.Contains("No overload", text);
        Assert.Contains("Available", text);
    }

    [Fact]
    public async Task DecompileMethod_WithHintMatchingOnlyABaseOverload_ResolvesToTheBaseMethod()
    {
        // TASK-24: C# overload resolution spans the inheritance chain. When the requested type
        // declares Bar(int) and a base declares Bar(string), asking for Bar(string) used to be
        // rejected — TryFindMethod stopped at the first level that had the *name* and matched
        // the hint only against that level. It now keeps walking to the base, so the base-only
        // overload resolves and decompiles, tagged with its inherited origin.
        var (derived, name, baseOverload) = MethodWithBaseOnlyOverload();
        var paramTypes = baseOverload.GetParameters()
            .Select(p => CSharpKeyword(p.ParameterType.Name) ?? p.ParameterType.Name)
            .ToArray();

        string text = await CallToolText("decompile_method", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["typeName"] = derived.FullName!,
            ["methodName"] = name,
            ["parameterTypes"] = paramTypes,
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

        string text = await CallToolText("decompile_method", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["typeName"] = method.DeclaringType!.FullName!,
            ["methodName"] = method.Name,
        });

        // Parameter list and return arrow, not just the bare name.
        Assert.Contains($"// {method.DeclaringType!.FullName}.{method.Name}(", text);
        Assert.Contains("→", text);
    }

    [Fact]
    public async Task FindMethods_WithUnknownArgument_ReturnsErrorNamingValidArguments()
    {
        // The reported call: typeName/methodName are decompile_method's parameters, not
        // find_methods'. The SDK drops unrecognized keys, and because every find_methods
        // filter is optional the call used to run completely unfiltered and return the
        // whole assembly — a wrong answer wearing the shape of a right one.
        var (result, text) = await CallToolExpectingError("find_methods", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["typeName"] = "ICSharpCode.Decompiler.DecompilerSettings",
            ["methodName"] = "Equals",
        });

        Assert.True(result.IsError,
            $"Expected unrecognized arguments to be rejected rather than ignored, got: {text}");
        Assert.Contains("Unknown argument", text);
        Assert.Contains("typeName", text);
        Assert.Contains("methodName", text);
        // Naming the valid set is what turns this into a one-round-trip fix.
        Assert.Contains("namePattern", text);
        Assert.Contains("declaringType", text);
    }

    [Fact]
    public async Task FindMethods_ByExactDeclaringType_ReturnsOnlyThatTypesMethods()
    {
        var type = TypeWithSeveralPlainMethods();

        string text = await CallToolText("find_methods", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["declaringType"] = type.FullName!,
            ["accessibility"] = "All",
            ["limit"] = 200,
        });

        Assert.DoesNotContain("No methods match", text);
        // Every emitted result line is indented; each must belong to the requested type.
        var resultLines = text.Split('\n')
            .Where(line => line.StartsWith("  ") && !line.StartsWith("  ..."))
            .ToList();
        Assert.NotEmpty(resultLines);
        Assert.All(resultLines, line =>
            Assert.Contains($"{type.FullName}.", line));
    }

    [Fact]
    public async Task FindMethods_WithNonexistentDeclaringType_ReturnsError()
    {
        // An exact name is a claim that the type exists. Answering a typo with
        // "No methods match" reads as "that type has no such methods".
        var (result, text) = await CallToolExpectingError("find_methods", new()
        {
            ["assembly"] = _server.AssemblyPath,
            ["declaringType"] = "ICSharpCode.Decompiler.ThisTypeDoesNotExist",
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
            ["declaringType"] = "ICSharpCode.Decompiler.DecompilerSettings",
            ["declaringTypePattern"] = "Decompiler",
        });

        Assert.True(result.IsError, $"Expected a mutually-exclusive rejection, got: {text}");
        Assert.Contains("mutually exclusive", text);
    }

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
    /// Find the TASK-24 shape in the test target: a type that declares a method whose name
    /// also has a <em>different</em> overload declared only on a base type in the same
    /// assembly (so the base overload is decompilable), and whose parameter types are simple
    /// enough to express as a decompile_method hint. Returns the derived type, the shared
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
                // The base overload must live in the same DLL, else decompile_method can't reach it.
                if (baseType.Assembly != assembly || baseType.IsNested || baseType.IsGenericType)
                    continue;

                foreach (var baseMethod in baseType.GetMethods(PublicDeclared)
                             .Where(m => !m.IsSpecialName && !m.IsGenericMethod
                                         && m.GetParameters().Length > 0
                                         && m.GetParameters().All(p => IsSimpleParameterType(p.ParameterType))))
                {
                    // The derived type must declare the *name* — that is the short-circuit
                    // trigger the fix removed.
                    var siblings = declaredByName[baseMethod.Name].ToList();
                    if (siblings.Count == 0)
                        continue;

                    // Require every derived overload of this name to differ in arity from the
                    // base overload. decompile_method narrows by exact parameter count first, so
                    // a differing arity guarantees the hint cannot match a derived overload and
                    // resolution must walk to the base — it also implies the base signature isn't
                    // redeclared. This tests an arity-differentiated variant of the TASK-24 shape
                    // rather than the literal same-arity repro, which keeps the discovered target
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
    /// The result lines of an <c>analyze</c> response, with its header line dropped. Lets
    /// two routes to the same cross-references be compared without the headers — which name
    /// the symbol asked about, and so differ by construction — forcing a mismatch.
    /// </summary>
    private static string ResultBody(string analyzeOutput)
    {
        int firstBreak = analyzeOutput.IndexOf('\n');
        Assert.True(firstBreak >= 0,
            $"Expected an analyze header line followed by results, got: {analyzeOutput}");
        return analyzeOutput[(firstBreak + 1)..];
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
