using System.Reflection.Emit;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for compare, the cross-assembly comparison tool, at each level of its
/// drill-down. Drives the real ILens server. The shared fixture
/// already loads ICSharpCode.Decompiler.dll as one inspection target; the second
/// target is whatever sibling DLL the test points at under the same allow-root
/// (the test bin directory, which the fixture exposes via AssemblyPath).
/// </summary>
public sealed class ComparisonTests : IClassFixture<ILensServerFixture>
{
    private readonly ILensServerFixture _server;
    private readonly string _secondAssemblyPath;

    public ComparisonTests(ILensServerFixture server)
    {
        _server = server;
        // ModelContextProtocol.dll lands in the test bin via the package reference
        // in Tests.csproj — picked because it shares an allow-root with the
        // decompiler DLL and contains a public API surface distinct from it.
        _secondAssemblyPath = Path.Combine(
            Path.GetDirectoryName(_server.AssemblyPath)!,
            "ModelContextProtocol.dll");
        if (!File.Exists(_secondAssemblyPath))
            throw new FileNotFoundException(
                $"Second comparison target not found: {_secondAssemblyPath}. " +
                "Expected ModelContextProtocol.dll alongside ICSharpCode.Decompiler.dll " +
                "in the test output.");
    }

    [Fact]
    public async Task Compare_ChangedTypes_OnByteIdenticalAssemblies_ReportsNoChanges()
    {
        // Acceptance #1: comparing an assembly against itself (same path → same
        // bytes → byte-identical PE) returns an empty change set.
        string text = await CallToolText("compare", new()
        {
            ["assembly_a"] = _server.AssemblyPath,
            ["assembly_b"] = _server.AssemblyPath,
        });
        Assert.Equal("No changed types", text);
    }

    [Fact]
    public async Task Compare_ChangedTypes_AcrossDifferentAssemblies_ReportsChanges()
    {
        // Two unrelated assemblies share almost no types, so the result must be
        // non-empty and dominated by types only one of them has. Shape-only — exact
        // counts drift as either package upstream version moves.
        // Sorted by name, one assembly's types fill the first lines, so the limit is raised to
        // reach the other's.
        string text = await CallToolText("compare", new()
        {
            ["assembly_a"] = _server.AssemblyPath,
            ["assembly_b"] = _secondAssemblyPath,
            ["limit"] = 100000,
        });
        Assert.NotEqual("No changed types", text);
        // Both directions must show up — Decompiler-only types with -, MCP-only
        // types with +.
        Assert.Contains(text.Split('\n'), line => line.StartsWith("+ "));
        Assert.Contains(text.Split('\n'), line => line.StartsWith("- "));
    }

    [Fact]
    public async Task Compare_ChangedTypes_WithNamespaceFilter_ScopesTheWalk()
    {
        // Filtering to a namespace that exists in only one assembly should still
        // surface its types, but exclude everything outside it.
        string text = await CallToolText("compare", new()
        {
            ["assembly_a"] = _server.AssemblyPath,
            ["assembly_b"] = _secondAssemblyPath,
            ["namespace"] = "ICSharpCode.Decompiler.TypeSystem",
        });
        // Decompiler-only namespace — every type from it shows up with -, and no
        // types from MCP's namespaces leak in.
        Assert.Contains(text.Split('\n'), line => line.StartsWith("- "));
        Assert.DoesNotContain("ModelContextProtocol", text);
    }

    [Fact]
    public async Task Compare_ChangedTypes_WhenABaseTypeOrInterfaceChanges_ReportsAMetadataChange()
    {
        // BaseShift2.dll is BaseShift.dll's source compiled with SHIFTED, which gives each
        // Mover another base class or interface: a same-named one of another arity, or the
        // same one with another type argument. A class's constructor calls its base
        // constructor, so a new base class changes that body as well. Gauge differs only in
        // accessor bodies, which ILSpy does not list among the type's methods. The namespace
        // leaves out the types in BaseShift.Generated, whose changes the tests below cover.
        string text = await CallToolText("compare", new()
        {
            ["assembly_a"] = BaseShiftPath("BaseShift.dll"),
            ["assembly_b"] = BaseShiftPath("BaseShift2.dll"),
            ["namespace"] = "BaseShift",
        });

        Assert.Equal(
            new[]
            {
                "5 changed types:",
                "~ BaseShift.BaseArgumentMover [metadata,bodies]",
                "~ BaseShift.BaseArityMover [metadata,bodies]",
                "~ BaseShift.Gauge [bodies]",
                "~ BaseShift.InterfaceArgumentMover [metadata]",
                "~ BaseShift.InterfaceArityMover [metadata]",
            },
            text.Split('\n'));
    }

    [Fact]
    public async Task Compare_MemberChanges_WhenOnlyAccessorBodiesChange_NamesTheChangedAccessors()
    {
        // In BaseShift2, Gauge's Level getter, Limit setter and Changed add accessor differ.
        // Each property or event is marked changed and names the accessor to compare next;
        // its unchanged accessors and the unchanged Steady are not named.
        string text = await CallToolText("compare", new()
        {
            ["assembly_a"] = BaseShiftPath("BaseShift.dll"),
            ["assembly_b"] = BaseShiftPath("BaseShift2.dll"),
            ["type_name"] = "BaseShift.Gauge",
        });

        Assert.Equal(
            new[]
            {
                "3 member changes in BaseShift.Gauge:",
                "~ public event EventHandler Changed [add_Changed]",
                "~ public int Level { get; set; } [get_Level]",
                "~ public int Limit { get; set; } [set_Limit]",
            },
            text.Split('\n'));
    }

    [Fact]
    public async Task Compare_Bodies_OfAChangedAccessor_ShowsBothBodies()
    {
        // The accessor a member change names is what member_name takes to show its two bodies.
        string text = await CallToolText("compare", new()
        {
            ["assembly_a"] = BaseShiftPath("BaseShift.dll"),
            ["assembly_b"] = BaseShiftPath("BaseShift2.dll"),
            ["type_name"] = "BaseShift.Gauge",
            ["member_name"] = "get_Level",
        });

        Assert.Contains("// === A: ", text);
        Assert.Contains("return _value;", text);
        Assert.Contains("return _value + 1;", text);
    }

    private string BaseShiftPath(string fileName) =>
        Path.Combine(Path.GetDirectoryName(_server.AssemblyPath)!, fileName);

    [Fact]
    public async Task Compare_ChangedTypes_WhenGeneratedNamesShift_ReportsOnlyTheRealChanges()
    {
        // In BaseShift2, Renumbered gains a method declared before its others, which shifts the
        // number in every name the compiler generated for them while their code stays the same,
        // and ChangedInside changes only inside a lambda, a local function, an iterator and an
        // async method, whose code the compiler moves into generated classes and methods.
        string text = await CallToolText("compare", new()
        {
            ["assembly_a"] = BaseShiftPath("BaseShift.dll"),
            ["assembly_b"] = BaseShiftPath("BaseShift2.dll"),
            ["namespace"] = "BaseShift.Generated",
        });

        Assert.Equal(
            new[]
            {
                "2 changed types:",
                "~ BaseShift.Generated.ChangedInside [bodies]",
                "~ BaseShift.Generated.Renumbered [members]",
            },
            text.Split('\n'));
    }

    [Fact]
    public async Task Compare_MemberChanges_WhenGeneratedNamesShift_ListsOnlyTheAddedMethod()
    {
        // Every generated name in Renumbered shifts: those of a lambda, a static lambda, a
        // closure, a local function, an iterator, an async method, a cached method group, a
        // base call from generated code, an anonymous type, a lambda with a default parameter
        // and the lambdas and method groups of generic methods. None of it is a change.
        string text = await CallToolText("compare", new()
        {
            ["assembly_a"] = BaseShiftPath("BaseShift.dll"),
            ["assembly_b"] = BaseShiftPath("BaseShift2.dll"),
            ["type_name"] = "BaseShift.Generated.Renumbered",
        });

        Assert.Equal(
            new[]
            {
                "1 member change in BaseShift.Generated.Renumbered:",
                "+ public long Inserted(int seed)",
            },
            text.Split('\n'));
    }

    [Fact]
    public async Task Compare_MemberChanges_WhenCodeInsideGeneratedCodeChanges_NamesTheMethodItIsWrittenIn()
    {
        // Unchanged has a lambda as well, whose code stays the same.
        string text = await CallToolText("compare", new()
        {
            ["assembly_a"] = BaseShiftPath("BaseShift.dll"),
            ["assembly_b"] = BaseShiftPath("BaseShift2.dll"),
            ["type_name"] = "BaseShift.Generated.ChangedInside",
        });

        Assert.Equal(
            new[]
            {
                "4 member changes in BaseShift.Generated.ChangedInside:",
                "~ public Task<int> InAsync()",
                "~ public IEnumerable<int> InIterator()",
                "~ public int InLambda(int seed)",
                "~ public int InLocalFunction(int seed)",
            },
            text.Split('\n'));
    }

    [Fact]
    public async Task Compare_MemberChanges_WithCompilerGenerated_ListsGeneratedMembersUnderTheirOwnNames()
    {
        // The lambda in CapturesThis is a method of Renumbered itself, so with generated members
        // included it is listed under its name, which the shifted number turns into a removal
        // and an addition. CapturesThis still counts as unchanged.
        string text = await CallToolText("compare", new()
        {
            ["assembly_a"] = BaseShiftPath("BaseShift.dll"),
            ["assembly_b"] = BaseShiftPath("BaseShift2.dll"),
            ["type_name"] = "BaseShift.Generated.Renumbered",
            ["include_compiler_generated"] = true,
        });

        var lines = text.Split('\n');
        Assert.Contains(lines, line => line.StartsWith("- ") && line.Contains("<CapturesThis>b__"));
        Assert.Contains(lines, line => line.StartsWith("+ ") && line.Contains("<CapturesThis>b__"));
        Assert.Contains("+ public long Inserted(int seed)", lines);
        Assert.DoesNotContain(lines, line => line.Contains("CapturesThis()"));
    }

    [Fact]
    public async Task Compare_Bodies_OfAMethodWhoseIteratorChanged_ShowsBothBodies()
    {
        string text = await CallToolText("compare", new()
        {
            ["assembly_a"] = BaseShiftPath("BaseShift.dll"),
            ["assembly_b"] = BaseShiftPath("BaseShift2.dll"),
            ["type_name"] = "BaseShift.Generated.ChangedInside",
            ["member_name"] = "InIterator",
        });

        Assert.Contains("// === A: ", text);
        Assert.Contains("yield return 2;", text);
        Assert.Contains("yield return 3;", text);
    }

    [Theory]
    [InlineData("csharp")]
    [InlineData("il")]
    public async Task Compare_Bodies_OfAMethodWhoseGeneratedNamesShifted_SaysOnlyThatTheBodiesAreIdentical(string format)
    {
        // Bodies that print alike are answered by the verdict alone, in either format.
        string text = await CallToolText("compare", new()
        {
            ["assembly_a"] = BaseShiftPath("BaseShift.dll"),
            ["assembly_b"] = BaseShiftPath("BaseShift2.dll"),
            ["type_name"] = "BaseShift.Generated.Renumbered",
            ["member_name"] = "LocalFunctionCapturing",
            ["format"] = format,
        });

        Assert.Equal(
            "// BaseShift.Generated.Renumbered.LocalFunctionCapturing(int) → int — bodies are identical in both assemblies.",
            text);
    }

    [Fact]
    public async Task Compare_Bodies_WithIlFormat_ShowsTheGeneratedCodeWithItsNumbersReplaced()
    {
        // The local function is generated as a method of the type, so the IL shows it after the
        // method's own, with its number replaced as the comparison reads it.
        string text = await CallToolText("compare", new()
        {
            ["assembly_a"] = BaseShiftPath("BaseShift.dll"),
            ["assembly_b"] = BaseShiftPath("BaseShift2.dll"),
            ["type_name"] = "BaseShift.Generated.ChangedInside",
            ["member_name"] = "InLocalFunction",
            ["format"] = "il",
        });

        Assert.Contains("// === B: ", text);
        Assert.Contains("// Generated: BaseShift.Generated.ChangedInside.<InLocalFunction>g__Scale|#1_0(int)", text);
        Assert.Contains("ldc.i4.2", text);
        Assert.Contains("ldc.i4.3", text);
    }

    [Theory]
    [InlineData("BaseShift.dll", "BaseShift2.dll", "B")]
    [InlineData("BaseShift2.dll", "BaseShift.dll", "A")]
    public async Task Compare_Bodies_OfAMethodOnlyOneAssemblyHas_ShowsItsCodeAndWhichHasIt(
        string assemblyA, string assemblyB, string side)
    {
        // Inserted is the method the member list shows as added, and its code is the whole difference.
        string text = await CallToolText("compare", new()
        {
            ["assembly_a"] = BaseShiftPath(assemblyA),
            ["assembly_b"] = BaseShiftPath(assemblyB),
            ["type_name"] = "BaseShift.Generated.Renumbered",
            ["member_name"] = "Inserted",
        });

        Assert.StartsWith($"// BaseShift.Generated.Renumbered.Inserted(int) → long — only {side} has it.\n\n", text);
        Assert.Contains("public long Inserted(int seed)", text);
        Assert.DoesNotContain("// === ", text);
    }

    [Fact]
    public async Task Compare_Bodies_OfAMethodOnlyOneAssemblyHas_WithIlFormat_ShowsItsIl()
    {
        string text = await CallToolText("compare", new()
        {
            ["assembly_a"] = BaseShiftPath("BaseShift.dll"),
            ["assembly_b"] = BaseShiftPath("BaseShift2.dll"),
            ["type_name"] = "BaseShift.Generated.Renumbered",
            ["member_name"] = "Inserted",
            ["format"] = "il",
        });

        Assert.StartsWith("// BaseShift.Generated.Renumbered.Inserted(int) → long — only B has it.\n\n", text);
        Assert.Contains("ret", text);
    }

    [Fact]
    public async Task Compare_Bodies_OfAMethodOfATypeOnlyOneAssemblyHas_ShowsItsCode()
    {
        // CSharpDecompiler is in the decompiler's assembly and not in ModelContextProtocol's.
        string text = await CallToolText("compare", new()
        {
            ["assembly_a"] = _server.AssemblyPath,
            ["assembly_b"] = _secondAssemblyPath,
            ["type_name"] = "ICSharpCode.Decompiler.CSharp.CSharpDecompiler",
            ["member_name"] = "GetAstTransforms",
        });

        Assert.StartsWith(
            "// ICSharpCode.Decompiler.CSharp.CSharpDecompiler.GetAstTransforms() → List<IAstTransform> — only A has it.\n\n",
            text);
    }

    [Fact]
    public async Task Compare_Bodies_WithAHintNeitherAssemblySatisfies_ListsTheOverloads()
    {
        // Both builds have Unchanged(int) and neither an overload taking a string, so the hint
        // selects nothing and the error says what there is.
        CallToolResult result = await _server.Client.CallToolAsync("compare",
            new Dictionary<string, object?>
            {
                ["assembly_a"] = BaseShiftPath("BaseShift.dll"),
                ["assembly_b"] = BaseShiftPath("BaseShift2.dll"),
                ["type_name"] = "BaseShift.Generated.ChangedInside",
                ["member_name"] = "Unchanged",
                ["parameter_types"] = new[] { "string" },
            });

        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        Assert.True(result.IsError, $"Expected a rejection, got: {text}");
        Assert.Contains(
            "No overload of 'Unchanged' on BaseShift.Generated.ChangedInside matches parameter types (string). " +
            "Available: BaseShift.Generated.ChangedInside.Unchanged(int).", text);
    }

    [Fact]
    public async Task Compare_ChangedTypes_WhenOnlyBurstsCounterShifts_ReportsNoChange()
    {
        // Burst numbers its wrappers across the assembly, so a method it compiles, added anywhere,
        // renames the wrappers of every method after it. That alone is no change.
        var a = BurstShapedAssembly.Write("0000ABCD", BurstShapedAssembly.Returns(1));
        var b = BurstShapedAssembly.Write("0000ABCE", BurstShapedAssembly.Returns(1));
        try
        {
            string text = await CallToolText("compare", new()
            {
                ["assembly_a"] = a,
                ["assembly_b"] = b,
            });
            Assert.Equal("No changed types", text);
        }
        finally
        {
            BurstShapedAssembly.TryDelete(a, b);
        }
    }

    [Fact]
    public async Task Compare_MemberChanges_WhenCodeBurstCompilesChanges_NamesTheMethodWrittenInSource()
    {
        // The code Burst compiles sits in Compute$BurstManaged, and Compute only calls into Burst's
        // wrapper, so a change to the copy is a change of Compute. Neither the copy nor the method
        // that starts the wrapper is listed.
        var a = BurstShapedAssembly.Write("0000ABCD", BurstShapedAssembly.Returns(1));
        var b = BurstShapedAssembly.Write("0000ABCE", BurstShapedAssembly.Returns(2));
        try
        {
            string text = await CallToolText("compare", new()
            {
                ["assembly_a"] = a,
                ["assembly_b"] = b,
                ["type_name"] = "BurstShaped.Container",
            });
            Assert.Equal(
                new[]
                {
                    "1 member change in BurstShaped.Container:",
                    "~ public static int Compute()",
                },
                text.Split('\n'));
        }
        finally
        {
            BurstShapedAssembly.TryDelete(a, b);
        }
    }

    [Fact]
    public async Task Compare_Bodies_OfAMethodBurstCompiles_ShowsTheCodeBurstCompiles()
    {
        var a = BurstShapedAssembly.Write("0000ABCD", BurstShapedAssembly.Returns(1));
        var b = BurstShapedAssembly.Write("0000ABCE", BurstShapedAssembly.Returns(2));
        try
        {
            string text = await CallToolText("compare", new()
            {
                ["assembly_a"] = a,
                ["assembly_b"] = b,
                ["type_name"] = "BurstShaped.Container",
                ["member_name"] = "Compute",
            });
            Assert.StartsWith("// BurstShaped.Container.Compute() → int (via Compute$BurstManaged)\n", text);
            Assert.Contains("return 1;", text);
            Assert.Contains("return 2;", text);
        }
        finally
        {
            BurstShapedAssembly.TryDelete(a, b);
        }
    }

    [Fact]
    public async Task Compare_Bodies_WhenOnlyTheIlDiffers_SaysTheBodiesDecompileAlike()
    {
        // ldc.i4.s 1 and ldc.i4 1 load the same number, so both bodies decompile to return 1.
        var a = BurstShapedAssembly.Write("0000ABCD", BurstShapedAssembly.Returns(1));
        var b = BurstShapedAssembly.Write("0000ABCD", il =>
        {
            il.Emit(OpCodes.Ldc_I4, 1);
            il.Emit(OpCodes.Ret);
        });
        try
        {
            string text = await CallToolText("compare", new()
            {
                ["assembly_a"] = a,
                ["assembly_b"] = b,
                ["type_name"] = "BurstShaped.Container",
                ["member_name"] = "Compute",
            });
            Assert.Equal(
                "// BurstShaped.Container.Compute() → int (via Compute$BurstManaged) — bodies decompile alike " +
                "in both assemblies, but their IL differs; format il shows where.",
                text);
        }
        finally
        {
            BurstShapedAssembly.TryDelete(a, b);
        }
    }

    [Fact]
    public async Task Compare_MemberChanges_OnByteIdenticalAssemblies_ReportsIdentical()
    {
        // Acceptance #2: a structured diff on a type that exists identically in
        // both assemblies returns the empty-diff message and does not invoke
        // the C# decompiler (asserting "no decompile" indirectly via the
        // identical-message path).
        string text = await CallToolText("compare", new()
        {
            ["assembly_a"] = _server.AssemblyPath,
            ["assembly_b"] = _server.AssemblyPath,
            ["type_name"] = "ICSharpCode.Decompiler.CSharp.CSharpDecompiler",
        });
        Assert.Equal("No member changes in ICSharpCode.Decompiler.CSharp.CSharpDecompiler", text);
    }

    [Fact]
    public async Task Compare_MemberChanges_OnTypePresentOnlyInOneAssembly_LabelsSide()
    {
        // A type that lives in A but not B says so in the header and lists every
        // member of that type as removed.
        string text = await CallToolText("compare", new()
        {
            ["assembly_a"] = _server.AssemblyPath,
            ["assembly_b"] = _secondAssemblyPath,
            ["type_name"] = "ICSharpCode.Decompiler.CSharp.CSharpDecompiler",
        });
        var lines = text.Split('\n');
        Assert.Matches(
            @"^\d+ member changes in ICSharpCode\.Decompiler\.CSharp\.CSharpDecompiler, which only A has:$", lines[0]);
        Assert.All(lines.Skip(1).Where(line => !line.StartsWith("... (")), line => Assert.StartsWith("- ", line));
    }

    [Fact]
    public async Task Compare_MemberChanges_OnTypeMissingFromBoth_ReturnsError()
    {
        CallToolResult result = await _server.Client.CallToolAsync("compare",
            new Dictionary<string, object?>
            {
                ["assembly_a"] = _server.AssemblyPath,
                ["assembly_b"] = _secondAssemblyPath,
                ["type_name"] = "Some.NameSpace.NoSuchType",
            });

        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        Assert.True(result.IsError, $"Expected not-found rejection, got: {text}");
        Assert.Contains("not found", text);
    }

    [Fact]
    public async Task Compare_MemberChanges_OnANestedType_AcceptsReflectionAndDottedForm()
    {
        // .NET reflection names a nested type Outer+Inner, ILens prints Outer.Inner. The
        // fixture's own nested type keeps the test independent of the upstream API.
        var harmonyMod = Path.Combine(
            Path.GetDirectoryName(_server.AssemblyPath)!, "HarmonyMod.dll");
        foreach (string typeName in new[] { "HarmonyMod.TargetC+Nested", "HarmonyMod.TargetC.Nested" })
        {
            string text = await CallToolText("compare", new()
            {
                ["assembly_a"] = harmonyMod,
                ["assembly_b"] = harmonyMod,
                ["type_name"] = typeName,
            });
            Assert.Equal("No member changes in HarmonyMod.TargetC.Nested", text);
        }
    }

    [Fact]
    public async Task Compare_MemberChanges_OnTypesSharingAName_NamesTheTypeFound()
    {
        // HarmonyMod.TargetD and TargetD<T> share a name apart from their arity. The name
        // alone means the non-generic one; the header names the type found, as ILens prints
        // it, whichever spelling asked for it.
        var harmonyMod = Path.Combine(
            Path.GetDirectoryName(_server.AssemblyPath)!, "HarmonyMod.dll");
        foreach (var (typeName, printed) in new[]
        {
            ("HarmonyMod.TargetD", "HarmonyMod.TargetD"),
            ("HarmonyMod.TargetD`1", "HarmonyMod.TargetD<>"),
            ("HarmonyMod.TargetD<T>", "HarmonyMod.TargetD<>"),
        })
        {
            string text = await CallToolText("compare", new()
            {
                ["assembly_a"] = harmonyMod,
                ["assembly_b"] = harmonyMod,
                ["type_name"] = typeName,
            });
            Assert.Equal($"No member changes in {printed}", text);
        }
    }

    [Fact]
    public async Task Compare_Bodies_OnByteIdenticalAssemblies_ReportsIdenticalBodies()
    {
        // A self-comparison on a known method is answered by the "identical" line alone.
        string text = await CallToolText("compare", new()
        {
            ["assembly_a"] = _server.AssemblyPath,
            ["assembly_b"] = _server.AssemblyPath,
            ["type_name"] = "ICSharpCode.Decompiler.DecompilerSettings",
            ["member_name"] = "get_ThrowOnAssemblyResolveErrors",
        });
        Assert.EndsWith(" — bodies are identical in both assemblies.", text);
        Assert.DoesNotContain("\n", text);
    }

    [Fact]
    public async Task Compare_Bodies_WithIlFormat_ReturnsIlDisassembly()
    {
        // format il prints each body as the disassembler renders it, tokens resolved to names.
        string text = await CallToolText("compare", new()
        {
            ["assembly_a"] = BaseShiftPath("BaseShift.dll"),
            ["assembly_b"] = BaseShiftPath("BaseShift2.dll"),
            ["type_name"] = "BaseShift.Gauge",
            ["member_name"] = "get_Level",
            ["format"] = "il",
        });
        Assert.Contains("// === A: ", text);
        Assert.Contains("ldfld int32 BaseShift.Gauge::_value", text);
        Assert.Contains("ldc.i4.1", text);
    }

    [Fact]
    public async Task Compare_MemberChanges_OnRvaShiftedTwin_DoesNotFlagIdenticalBodies()
    {
        // HarmonyMod2.dll is HarmonyMod.dll's source plus an earlier-declared
        // Padding class — every method on TargetA/TargetB produces byte-identical
        // IL but lands at a different file RVA. AssemblyHost.DisassembleMethodBody
        // strips the RVA header, so none of them may surface here as a changed
        // body.
        var harmonyMod = Path.Combine(
            Path.GetDirectoryName(_server.AssemblyPath)!, "HarmonyMod.dll");
        var harmonyMod2 = Path.Combine(
            Path.GetDirectoryName(_server.AssemblyPath)!, "HarmonyMod2.dll");
        if (!File.Exists(harmonyMod) || !File.Exists(harmonyMod2))
            throw new FileNotFoundException(
                $"HarmonyMod fixtures not found: {harmonyMod}, {harmonyMod2}. " +
                "Both projects under Tests/Fixtures/ are expected to land here " +
                "via Tests.csproj ProjectReferences.");

        string text = await CallToolText("compare", new()
        {
            ["assembly_a"] = harmonyMod,
            ["assembly_b"] = harmonyMod2,
            ["type_name"] = "HarmonyMod.TargetA",
        });
        // Same TargetA source on both sides → no member-set diff, no body diff.
        // The empty answer is what compare returns only when every member matched
        // and every body-equality check returned true.
        Assert.Equal("No member changes in HarmonyMod.TargetA", text);
    }

    [Fact]
    public async Task Compare_Bodies_WithUnknownFormat_ReturnsError()
    {
        CallToolResult result = await _server.Client.CallToolAsync("compare",
            new Dictionary<string, object?>
            {
                ["assembly_a"] = _server.AssemblyPath,
                ["assembly_b"] = _server.AssemblyPath,
                ["type_name"] = "ICSharpCode.Decompiler.DecompilerSettings",
                ["member_name"] = "get_ThrowOnAssemblyResolveErrors",
                ["format"] = "json",
            });

        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        Assert.True(result.IsError, $"Expected unknown-format rejection, got: {text}");
    }

    [Fact]
    public async Task Compare_ChangedTypes_CutByLimit_CountsEveryType()
    {
        string text = await CallToolText("compare", new()
        {
            ["assembly_a"] = _server.AssemblyPath,
            ["assembly_b"] = _secondAssemblyPath,
            ["limit"] = 1,
        });

        var lines = text.Split('\n');
        Assert.Equal(3, lines.Length);
        int total = int.Parse(lines[0].Split(' ')[0]);
        Assert.Equal($"{total} changed types:", lines[0]);
        Assert.Equal($"... ({total - 1} more; raise limit to see all)", lines[2]);
    }

    [Theory]
    [InlineData("member_name", "get_ThrowOnAssemblyResolveErrors", null, "member_name needs type_name.")]
    [InlineData("namespace", "ICSharpCode.Decompiler", "ICSharpCode.Decompiler.DecompilerSettings",
        "namespace applies only without type_name.")]
    [InlineData("format", "il", "ICSharpCode.Decompiler.DecompilerSettings", "format applies only with member_name.")]
    [InlineData("namespace", "No.Such.Namespace", null, "Namespace not found in either assembly: No.Such.Namespace.")]
    public async Task Compare_WithAParameterTheLevelCannotUse_ReturnsError(
        string parameter, string value, string? typeName, string expected)
    {
        // Each parameter belongs to one level of the drill-down; dropped silently at another, it
        // would leave the answer to a broader question looking like the answer to the one asked.
        var arguments = new Dictionary<string, object?>
        {
            ["assembly_a"] = _server.AssemblyPath,
            ["assembly_b"] = _server.AssemblyPath,
            [parameter] = value,
        };
        if (typeName is not null)
            arguments["type_name"] = typeName;

        CallToolResult result = await _server.Client.CallToolAsync("compare", arguments);

        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        Assert.True(result.IsError, $"Expected a rejection, got: {text}");
        Assert.Contains(expected, text);
    }

    [Fact]
    public async Task Compare_Bodies_WithCompilerGenerated_ReturnsError()
    {
        // The two bodies of a method already include the code generated for it, so the
        // parameter has nothing to add there.
        CallToolResult result = await _server.Client.CallToolAsync("compare",
            new Dictionary<string, object?>
            {
                ["assembly_a"] = _server.AssemblyPath,
                ["assembly_b"] = _server.AssemblyPath,
                ["type_name"] = "ICSharpCode.Decompiler.DecompilerSettings",
                ["member_name"] = "get_ThrowOnAssemblyResolveErrors",
                ["include_compiler_generated"] = true,
            });

        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        Assert.True(result.IsError, $"Expected a rejection, got: {text}");
        Assert.Contains("include_compiler_generated applies only without member_name.", text);
    }

    [Fact]
    public async Task Compare_Bodies_OfAProperty_PointsToItsAccessors()
    {
        // A property has no body of its own; its accessors do.
        CallToolResult result = await _server.Client.CallToolAsync("compare",
            new Dictionary<string, object?>
            {
                ["assembly_a"] = _server.AssemblyPath,
                ["assembly_b"] = _server.AssemblyPath,
                ["type_name"] = "ICSharpCode.Decompiler.DecompilerSettings",
                ["member_name"] = "ThrowOnAssemblyResolveErrors",
            });

        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        Assert.True(result.IsError, $"Expected a rejection, got: {text}");
        Assert.Contains(
            "ICSharpCode.Decompiler.DecompilerSettings.ThrowOnAssemblyResolveErrors is a property; compare its " +
            "accessors, get_ThrowOnAssemblyResolveErrors, set_ThrowOnAssemblyResolveErrors.", text);
    }

    private async Task<string> CallToolText(string toolName, Dictionary<string, object?> arguments)
    {
        CallToolResult result = await _server.Client.CallToolAsync(toolName, arguments);
        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.True(result.IsError != true, $"Tool '{toolName}' returned an error: {text}");
        return text;
    }
}
