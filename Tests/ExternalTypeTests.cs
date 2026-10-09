using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for naming a type the inspected assembly uses but does not define. The
/// HarmonyMod fixture references HarmonyLib, and the ExternalUser fixture references
/// ExternalLib. Both referenced assemblies land only in the References folder of the test
/// output, which the shared server does not search, so their types are ones ILens knows only
/// from the fixtures' references to them. The tests on a reference whose assembly row lies past
/// the end of the assembly table write such an assembly through
/// <see cref="DanglingReferenceAssembly"/>.
/// </summary>
public sealed class ExternalTypeTests : IClassFixture<ILensServerFixture>
{
    private const string Origin = "0Harmony, Version=";

    private const string NotReferenced =
        "This assembly neither defines nor references a type of that name.";

    private readonly ILensServerFixture _server;
    private readonly string _harmonyModPath;
    private readonly string _externalUserPath;

    public ExternalTypeTests(ILensServerFixture server)
    {
        _server = server;
        _harmonyModPath = FixturePath("HarmonyMod.dll", "Tests/Fixtures/HarmonyMod/");
        _externalUserPath = FixturePath("ExternalUser.dll", "Tests/Fixtures/ExternalUser/");

        string FixturePath(string fileName, string source)
        {
            string path = Path.Combine(Path.GetDirectoryName(_server.AssemblyPath)!, fileName);
            if (!File.Exists(path))
                throw new FileNotFoundException(
                    $"Fixture not found: {path}. {source} is expected to land here via Tests.csproj's " +
                    "ProjectReference.");
            return path;
        }
    }

    // Every tool that takes a type name in one assembly, with the parameter that names the
    // type and, where the tool requires one, a member name. The member is never looked up:
    // the type is resolved first.
    public static TheoryData<string, string, string?> ToolsNamingAType => new()
    {
        { "decompile", "type_name", null },
        { "list_members", "type_name", null },
        { "find_methods", "type_name", null },
        { "decompile", "type_name", "member_name" },
    };

    [Theory]
    [MemberData(nameof(ToolsNamingAType))]
    public async Task NamingAReferencedType_SaysWhereItComesFrom(
        string toolName, string typeParameter, string? memberParameter)
    {
        // "Type not found" reads like a typo. The type exists; its definition is in the
        // assembly the reference names, which is where the caller has to look.
        var arguments = new Dictionary<string, object?>
        {
            ["assembly"] = _harmonyModPath,
            [typeParameter] = "HarmonyLib.AccessTools",
        };
        if (memberParameter is not null)
            arguments[memberParameter] = "Method";

        var (result, text) = await CallToolExpectingError(toolName, arguments);

        Assert.True(result.IsError, $"Expected an error, got: {text}");
        Assert.Contains($"HarmonyLib.AccessTools is not defined in this assembly; it comes from {Origin}", text);
    }

    [Theory]
    [InlineData("HarmonyLib.AccessTools.FieldRef<,>")]
    [InlineData("HarmonyLib.AccessTools+FieldRef`2")]
    [InlineData("HarmonyLib.AccessTools.FieldRef")]
    public async Task NamingAReferencedNestedGenericType_NamesItAsILensPrintsIt(string typeName)
    {
        // The fixture uses AccessTools.FieldRef<T, F>, a generic type nested in another
        // assembly's type. Each spelling a caller may use for it reaches the same reference.
        var (_, text) = await CallToolExpectingError("list_members", new()
        {
            ["assembly"] = _harmonyModPath,
            ["type_name"] = typeName,
        });

        Assert.Contains($"HarmonyLib.AccessTools.FieldRef<,> is not defined in this assembly; it comes from {Origin}", text);
    }

    [Fact]
    public async Task NamingAReferencedTypeWithTheWrongArity_ListsItAmongTypesOfThatName()
    {
        var (_, text) = await CallToolExpectingError("list_members", new()
        {
            ["assembly"] = _harmonyModPath,
            ["type_name"] = "HarmonyLib.AccessTools<>",
        });

        Assert.Contains("Type not found: HarmonyLib.AccessTools<>. Types of that name: HarmonyLib.AccessTools.", text);
    }

    [Fact]
    public async Task NamingATypeNothingReferences_IsStillNotFound()
    {
        // The control: a name the assembly neither defines nor references gets no origin.
        var (result, text) = await CallToolExpectingError("list_members", new()
        {
            ["assembly"] = _harmonyModPath,
            ["type_name"] = "HarmonyLib.NoSuchType",
        });

        Assert.True(result.IsError, $"Expected an error, got: {text}");
        Assert.Contains("Type not found: HarmonyLib.NoSuchType.", text);
    }

    [Theory]
    [MemberData(nameof(ToolsNamingAType))]
    public async Task NamingAMissingType_WhereAReferenceNamesNoReadableAssembly_IsNotFound(
        string toolName, string typeParameter, string? memberParameter)
    {
        // Some builds of Harmony's own DLL from 2.3 on, 2.4.1's among them, have references that
        // name assembly rows past the end of their table. A name the assembly does not define is
        // looked up among its references, and that lookup has to get past such a row.
        var path = DanglingReferenceAssembly.Write();
        try
        {
            var arguments = new Dictionary<string, object?>
            {
                ["assembly"] = path,
                [typeParameter] = "DanglingReference.NoSuchType",
            };
            if (memberParameter is not null)
                arguments[memberParameter] = "Member";

            var (result, text) = await CallToolExpectingError(toolName, arguments);

            Assert.True(result.IsError, $"Expected an error, got: {text}");
            Assert.Contains("Type not found: DanglingReference.NoSuchType.", text);
        }
        finally
        {
            DanglingReferenceAssembly.TryDelete(path);
        }
    }

    [Fact]
    public async Task NamingATypeWhoseReferenceNamesNoReadableAssembly_SaysItIsOnlyReferenced()
    {
        // The reference still names the type, so the type is known; only where it comes from is not.
        var path = DanglingReferenceAssembly.Write();
        try
        {
            var (result, text) = await CallToolExpectingError("list_members", new()
            {
                ["assembly"] = path,
                ["type_name"] = "System.Uri",
            });

            Assert.True(result.IsError, $"Expected an error, got: {text}");
            Assert.Contains("System.Uri is not defined in this assembly, which only references it.", text);
        }
        finally
        {
            DanglingReferenceAssembly.TryDelete(path);
        }
    }

    [Theory]
    [InlineData("System.Text.StringBuilder", "DanglingReference.Holder.UsesBuilder(StringBuilder)")]
    [InlineData("System.Uri", "DanglingReference.Holder.UsesUri(Uri)")]
    public async Task Analyze_WhereAReferenceNamesNoReadableAssembly_AnswersFromTheReferences(
        string typeName, string user)
    {
        // StringBuilder's reference names its assembly and Uri's names a row past the table;
        // both are answered from the references.
        var path = DanglingReferenceAssembly.Write();
        try
        {
            string text = await CallToolText("analyze", new()
            {
                ["assembly"] = path,
                ["type_name"] = typeName,
                ["kind"] = "used_by",
            });

            Assert.Equal($"1 user of {typeName}:", Header(text));
            Assert.Equal(user, ResultLines(text).Single());
        }
        finally
        {
            DanglingReferenceAssembly.TryDelete(path);
        }
    }

    [Fact]
    public async Task Compare_OnATypeBothAssembliesReference_SaysWhereItComesFrom()
    {
        string harmonyMod2Path = Path.Combine(
            Path.GetDirectoryName(_server.AssemblyPath)!,
            "HarmonyMod2.dll");

        var (result, text) = await CallToolExpectingError("compare", new()
        {
            ["assembly_a"] = _harmonyModPath,
            ["assembly_b"] = harmonyMod2Path,
            ["type_name"] = "HarmonyLib.AccessTools",
        });

        Assert.True(result.IsError, $"Expected an error, got: {text}");
        Assert.Contains($"Type 'HarmonyLib.AccessTools' is not defined in either assembly; it comes from {Origin}", text);
    }

    // Every type-level kind, asked about an ExternalLib type, with the header and result lines
    // analyze answers with. Each set is exact, so a member reported under a compiler-generated
    // name, or reported twice, fails as surely as one left out.
    public static TheoryData<string, string, string, string[]> TypeLevelQuestions => new()
    {
        {
            "ExternalLib.Widget", "used_by", "7 users of ExternalLib.Widget:",
            [
                "ExternalUser.DerivedWidget", "ExternalUser.DerivedWidget..ctor()",
                "ExternalUser.Consumer.MakeWidget()", "ExternalUser.Consumer.InLambda()",
                "ExternalUser.WidgetExtensions.Polish(Widget)",
                "ExternalUser.Consumer.Exposed", "ExternalUser.Consumer.hidden",
            ]
        },
        // Named only by a typeof argument, which the compiler writes into the attribute blob.
        { "ExternalLib.OnlyInTypeof", "used_by", "1 user of ExternalLib.OnlyInTypeof:", ["ExternalUser.Consumer"] },
        // Named only as the type of an enum argument of an attribute.
        { "ExternalLib.Shade", "used_by", "1 user of ExternalLib.Shade:", ["ExternalUser.Consumer.Shaded()"] },
        // DerivedWidget's constructor calls the base constructor, which creates nothing.
        {
            "ExternalLib.Widget", "instantiated_by", "2 symbols instantiating ExternalLib.Widget:",
            ["ExternalUser.Consumer.MakeWidget()", "ExternalUser.Consumer.InLambda()"]
        },
        { "ExternalLib.Point", "instantiated_by", "1 symbol instantiating ExternalLib.Point:", ["ExternalUser.Consumer.MakePoint()"] },
        { "ExternalLib.Gadget<>", "instantiated_by", "1 symbol instantiating ExternalLib.Gadget<>:", ["ExternalUser.Consumer.UseGadgets()"] },
        {
            "ExternalLib.Widget", "exposed_by", "2 members exposing ExternalLib.Widget:",
            ["ExternalUser.Consumer.Exposed", "ExternalUser.WidgetExtensions.Polish(Widget)"]
        },
        {
            "ExternalLib.Widget", "extension_methods", "1 extension method for ExternalLib.Widget:",
            ["ExternalUser.WidgetExtensions.Polish(Widget)"]
        },
        {
            "ExternalLib.Gadget", "extension_methods", "1 extension method for ExternalLib.Gadget<>:",
            ["ExternalUser.WidgetExtensions.Fill(Gadget<int>)"]
        },
        {
            "ExternalLib.MarkAttribute", "applied_to", "5 symbols marked with ExternalLib.MarkAttribute:",
            [
                "ExternalUser.Consumer", "ExternalUser.Consumer.Shaded()", "ExternalUser.Marked",
                "ExternalUser.Marked.MarkedMethod()", "ExternalUser.Marked.MarkedParameter(int)",
            ]
        },
    };

    [Theory]
    [MemberData(nameof(TypeLevelQuestions))]
    public async Task Analyze_OnAReferencedType_AnswersFromTheReferences(
        string typeName, string kind, string header, string[] expected)
    {
        string text = await CallToolText("analyze", new()
        {
            ["assembly"] = _externalUserPath,
            ["type_name"] = typeName,
            ["kind"] = kind,
        });

        Assert.Equal(header, text.Split('\n')[0]);
        Assert.Equal(expected.Order(), ResultLines(text).Order());
    }

    [Fact]
    public async Task Analyze_AppliedTo_FindsAnAttributeStoredAsAFlag()
    {
        // [Serializable] is not an attribute row but a flag on the type, and the fixture's
        // assembly has no reference to System.SerializableAttribute at all.
        string text = await CallToolText("analyze", new()
        {
            ["assembly"] = _externalUserPath,
            ["type_name"] = "System.SerializableAttribute",
            ["kind"] = "applied_to",
        });

        Assert.Contains("ExternalUser.SerializableThing", ResultLines(text));
    }

    [Fact]
    public async Task Analyze_ImplementedBy_OnAReferencedInterface_FindsImplementersAndSaysWhatItCannotSee()
    {
        // BigSquare implements IShape through a local interface and SubSquare through a local
        // base class. MyCanvas implements it through ExternalLib's Canvas, which the fixture's
        // metadata does not show, so it is missing and the answer says such types may exist.
        string text = await CallToolText("analyze", new()
        {
            ["assembly"] = _externalUserPath,
            ["type_name"] = "ExternalLib.IShape",
            ["kind"] = "implemented_by",
        });

        string[] lines = ResultLines(text);
        Assert.Equal(
            new[] { "ExternalUser.BigSquare", "ExternalUser.IBigShape", "ExternalUser.Square", "ExternalUser.SubSquare" },
            lines[..^1].Order());
        Assert.StartsWith("Types deriving from a class in another assembly may also implement it", lines[^1]);
    }

    [Fact]
    public async Task Analyze_ImplementedBy_OnAReferencedClass_ReturnsError()
    {
        var (result, text) = await CallToolExpectingError("analyze", new()
        {
            ["assembly"] = _externalUserPath,
            ["type_name"] = "ExternalLib.Widget",
            ["kind"] = "implemented_by",
        });

        Assert.True(result.IsError, $"Expected an error, got: {text}");
        Assert.Contains("ExternalLib.Widget is a class, since ExternalUser.DerivedWidget derives from it", text);
    }

    // Member-level questions about ExternalLib types: the member, the kind, an overload hint,
    // and the header and result lines analyze answers with.
    public static TheoryData<string, string, string, string[]?, string, string[]> MemberQuestions => new()
    {
        {
            "ExternalLib.Widget", "Paint", "used_by", ["int"],
            "1 user of ExternalLib.Widget.Paint(int):", ["ExternalUser.Consumer.MakeWidget()"]
        },
        // Constructors are reached by their IL name; a base constructor call is a call.
        {
            "ExternalLib.Widget", ".ctor", "used_by", ["int"],
            "2 users of ExternalLib.Widget..ctor(int):",
            ["ExternalUser.Consumer.MakeWidget()", "ExternalUser.DerivedWidget..ctor()"]
        },
        {
            "ExternalLib.Widget", "Size", "read_by", null,
            "2 readers of ExternalLib.Widget.Size:",
            ["ExternalUser.Consumer.MakeWidget()", "ExternalUser.Consumer.InLambda()"]
        },
        {
            "ExternalLib.Widget", "Size", "assigned_by", null,
            "1 writer of ExternalLib.Widget.Size:", ["ExternalUser.Consumer.MakeWidget()"]
        },
        {
            "ExternalLib.Widget", "Count", "assigned_by", null,
            "1 writer of ExternalLib.Widget.Count:", ["ExternalUser.Consumer.MakeWidget()"]
        },
        // Fields used only through their address, counted by what each use does, as for a field
        // the assembly defines: a call on a number reads it, an out argument writes it, so does a
        // store to a member, and a call or argument into another assembly is marked as either.
        {
            "ExternalLib.Meter", "Reading", "assigned_by", null,
            "2 writers of ExternalLib.Meter.Reading:",
            [
                "ExternalUser.MeterReader.AddReading(Meter) (may write: passes it by ref to System.Threading.Interlocked.Increment(ref int))",
                "ExternalUser.MeterReader.FillReading(Meter)",
            ]
        },
        {
            "ExternalLib.Meter", "Reading", "read_by", null,
            "2 readers of ExternalLib.Meter.Reading:",
            [
                "ExternalUser.MeterReader.AddReading(Meter) (may read: passes it by ref to System.Threading.Interlocked.Increment(ref int))",
                "ExternalUser.MeterReader.ReadingText(Meter)",
            ]
        },
        {
            "ExternalLib.Meter", "Place", "assigned_by", null,
            "2 writers of ExternalLib.Meter.Place:",
            [
                "ExternalUser.MeterReader.MovePlace(Meter) (may write: calls ExternalLib.Spot.Move() on it)",
                "ExternalUser.MeterReader.SetPlace(Meter)",
            ]
        },
        {
            "ExternalLib.Meter", "Place", "read_by", null,
            "1 reader of ExternalLib.Meter.Place:",
            ["ExternalUser.MeterReader.MovePlace(Meter) (may read: calls ExternalLib.Spot.Move() on it)"]
        },
        {
            "ExternalLib.Widget", "Label", "used_by", null,
            "1 user of ExternalLib.Widget.Label (via get_Label, set_Label):", ["ExternalUser.Consumer.MakeWidget()"]
        },
        {
            "ExternalLib.Widget", "Label", "read_by", null,
            "1 reader of ExternalLib.Widget.Label (via get_Label):", ["ExternalUser.Consumer.MakeWidget()"]
        },
        // Only subscribing is visible here: raising the event happens inside ExternalLib.
        {
            "ExternalLib.Widget", "Changed", "used_by", null,
            "1 user of ExternalLib.Widget.Changed (via add_Changed):", ["ExternalUser.Consumer.MakeWidget()"]
        },
        // Gadget<int>.Add and Gadget<string>.Add are one member; its parameter is the type's
        // first type parameter, whose name only ExternalLib knows.
        {
            "ExternalLib.Gadget<>", "Add", "used_by", null,
            "1 user of ExternalLib.Gadget<>.Add(!0):", ["ExternalUser.Consumer.UseGadgets()"]
        },
    };

    [Theory]
    [MemberData(nameof(MemberQuestions))]
    public async Task Analyze_OnAMemberOfAReferencedType_AnswersFromTheReferences(
        string typeName, string memberName, string kind, string[]? parameterTypes, string header, string[] expected)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["assembly"] = _externalUserPath,
            ["type_name"] = typeName,
            ["member_name"] = memberName,
            ["kind"] = kind,
        };
        if (parameterTypes is not null)
            arguments["parameter_types"] = parameterTypes;

        string text = await CallToolText("analyze", arguments);

        Assert.Equal(header, text.Split('\n')[0]);
        Assert.Equal(expected.Order(), ResultLines(text).Order());
    }

    [Theory]
    [InlineData(null, "'Paint' on ExternalLib.Widget has 2 matching overloads; specify parameter_count or parameter_types to disambiguate: ExternalLib.Widget.Paint(), ExternalLib.Widget.Paint(int).")]
    [InlineData("string", "No overload of 'Paint' on ExternalLib.Widget matches parameter types (string). Available: ExternalLib.Widget.Paint(), ExternalLib.Widget.Paint(int).")]
    public async Task Analyze_OnAnOverloadedReferencedMethod_NeedsAHintThatSelectsOne(
        string? parameterType, string expected)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["assembly"] = _externalUserPath,
            ["type_name"] = "ExternalLib.Widget",
            ["member_name"] = "Paint",
            ["kind"] = "used_by",
        };
        if (parameterType is not null)
            arguments["parameter_types"] = new[] { parameterType };

        var (result, text) = await CallToolExpectingError("analyze", arguments);

        Assert.True(result.IsError, $"Expected an error, got: {text}");
        Assert.Contains(expected, text);
    }

    [Theory]
    [InlineData("nint", "nint")]
    [InlineData("IntPtr", "nint")]
    [InlineData("System.IntPtr", "nint")]
    [InlineData("nuint", "nuint")]
    [InlineData("UIntPtr", "nuint")]
    [InlineData("System.UIntPtr", "nuint")]
    public async Task Analyze_OnANativeIntegerOverloadOfAReferencedMethod_SelectsItByEveryName(
        string parameterType, string keyword)
    {
        // ILens decodes a referenced member's signature itself, which yields System.IntPtr and
        // System.UIntPtr where ILSpy's own reading yields nint and nuint. Both print as the
        // keyword, and each name selects its own overload only.
        string text = await CallToolText("analyze", new()
        {
            ["assembly"] = _externalUserPath,
            ["type_name"] = "ExternalLib.Native",
            ["member_name"] = "Release",
            ["kind"] = "used_by",
            ["parameter_types"] = new[] { parameterType },
        });

        Assert.Equal($"1 user of ExternalLib.Native.Release({keyword}):", text.Split('\n')[0]);
        Assert.Equal("ExternalUser.Consumer.ReleaseHandles()", ResultLines(text).Single());
    }

    [Theory]
    [InlineData("Paint", "uses", "reads the member's body, which is in the assembly that defines ExternalLib.Widget")]
    [InlineData("Draw", "overridden_by", "leaves no reference to the member it overrides or implements")]
    [InlineData("Paint", "call_sites", "which this assembly does not define; 'used_by' lists the methods that call it")]
    // A type-level kind is refused even for a member nothing references, whose empty answer
    // would otherwise pass for an answer to a question no member can take.
    [InlineData("NoSuchMember", "instantiated_by", "Analysis kind 'instantiated_by' is valid only for a type")]
    public async Task Analyze_KindsTheReferencesCannotAnswer_SayWhy(string memberName, string kind, string expected)
    {
        // DerivedWidget overrides Draw, but an override leaves no reference to Widget.Draw.
        var (result, text) = await CallToolExpectingError("analyze", new()
        {
            ["assembly"] = _externalUserPath,
            ["type_name"] = "ExternalLib.Widget",
            ["member_name"] = memberName,
            ["kind"] = kind,
        });

        Assert.True(result.IsError, $"Expected an error, got: {text}");
        Assert.Contains(expected, text);
    }

    [Fact]
    public async Task Analyze_WithAnOverloadHintOnAField_SaysTheNameIsNotAMethod()
    {
        // A hint selects a method overload. Size is referenced only as a field, so answering
        // "no member Size" would contradict the list of referenced members that follows it.
        var (result, text) = await CallToolExpectingError("analyze", new()
        {
            ["assembly"] = _externalUserPath,
            ["type_name"] = "ExternalLib.Widget",
            ["member_name"] = "Size",
            ["parameter_count"] = 0,
            ["kind"] = "read_by",
        });

        Assert.True(result.IsError, $"Expected an error, got: {text}");
        Assert.Contains("Member 'Size' on ExternalLib.Widget is referenced here only as a field, not as a method", text);
    }

    [Fact]
    public async Task Analyze_OnAMemberNothingReferences_ListsTheMembersThatAreReferenced()
    {
        // A misspelled or miscased member name would otherwise read as an unused member.
        string text = await CallToolText("analyze", new()
        {
            ["assembly"] = _externalUserPath,
            ["type_name"] = "ExternalLib.Widget",
            ["member_name"] = "Polish",
            ["kind"] = "used_by",
        });

        Assert.Equal("No users of ExternalLib.Widget.Polish", Header(text));
        Assert.Equal(
            "This assembly references no member Polish of ExternalLib.Widget. " +
            "It references: .ctor, Changed, Count, Label, Paint, Size.",
            ResultLines(text).Single());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Member")]
    public async Task Analyze_OnATypeNothingNames_SaysSoInsteadOfFailing(string? memberName)
    {
        // An explicit empty answer, not an error: nothing in the assembly names the type.
        var arguments = new Dictionary<string, object?>
        {
            ["assembly"] = _externalUserPath,
            ["type_name"] = "ExternalLib.NoSuchType",
            ["kind"] = "used_by",
        };
        if (memberName is not null)
            arguments["member_name"] = memberName;

        string text = await CallToolText("analyze", arguments);

        Assert.Equal(
            memberName is null ? "No users of ExternalLib.NoSuchType" : "No users of ExternalLib.NoSuchType.Member",
            Header(text));
        Assert.Equal(NotReferenced, ResultLines(text).Single());
    }

    [Fact]
    public async Task Analyze_UsedBy_OnAnEnumFromHarmony_FindsTheAttributesPassingIt()
    {
        // HarmonyLib.MethodType is passed only as an attribute argument. Decoding such an
        // argument needs the enum's underlying type, which only HarmonyLib's assembly holds.
        string text = await CallToolText("analyze", new()
        {
            ["assembly"] = _harmonyModPath,
            ["type_name"] = "HarmonyLib.MethodType",
            ["kind"] = "used_by",
        });

        Assert.Equal(
            new[]
            {
                "HarmonyMod.ConstructorPatch", "HarmonyMod.EventAdderPatch", "HarmonyMod.EventRemoverPatch",
                "HarmonyMod.ExternalIndexerGetterPatch", "HarmonyMod.ExternalIndexerSetterPatch",
                "HarmonyMod.FinalizeMethodPatch", "HarmonyMod.GenericConstructorPatch", "HarmonyMod.GetterPatch",
                "HarmonyMod.IndexerGetterPatch", "HarmonyMod.IndexerSetterPatch",
                "HarmonyMod.MarkedMethodsPatch.BeforeSetProp()", "HarmonyMod.NestedConstructorPatch",
                "HarmonyMod.OperatorPatch", "HarmonyMod.ParameterlessConstructorPatch",
                "HarmonyMod.RenamedIndexerGetterPatch", "HarmonyMod.RenamedIndexerSetterPatch",
                "HarmonyMod.TextConstructorPatch", "HarmonyMod.TextTargetPatch",
            },
            ResultLines(text).Order());
    }

    /// <summary>The header line of an <c>analyze</c> response.</summary>
    private static string Header(string analyzeOutput) => analyzeOutput.Split('\n')[0];

    /// <summary>The result lines of an <c>analyze</c> response, with its header line dropped.</summary>
    private static string[] ResultLines(string analyzeOutput) =>
        analyzeOutput.Split('\n').Skip(1).ToArray();

    /// <summary>
    /// Calls an ILens MCP tool, fails the test if it returned an error, and returns the
    /// concatenated text content.
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
    /// Calls an ILens MCP tool that is expected to fail, returning the raw result alongside
    /// its text so a test can assert on both.
    /// </summary>
    private async Task<(CallToolResult Result, string Text)> CallToolExpectingError(
        string toolName, Dictionary<string, object?> arguments)
    {
        CallToolResult result = await _server.Client.CallToolAsync(toolName, arguments);
        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        return (result, text);
    }
}
