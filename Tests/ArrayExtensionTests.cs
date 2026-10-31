using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for an array type named by <c>type_name</c>, which C# lets code call extension
/// methods on. analyze with extension_methods lists the methods whose receiver is exactly that
/// array and those whose receiver is an array of a type parameter the array fits by C#'s rule;
/// decompile, analyze and compare with member_name find through it the extension method C# binds
/// for a call on such an array, after a public instance member of System.Array or System.Object of
/// the name, which is then asked about through the class that declares it, a property only where
/// the question is not one for a method, as call_sites and compare are; and every other
/// question refuses an array type with an error that names those that take one. The fixtures are
/// the namespace CallShapes.Arrays in Tests/Fixtures/CallShapes/Arrays.cs, and ExternalUser's
/// ExternalUser.Arrays over ExternalLib's ExternalLib.Arrays, which the shared server reads without
/// ExternalLib and a second server with it.
/// </summary>
public sealed class ArrayExtensionTests : IClassFixture<ILensServerFixture>, IClassFixture<ReferencesServerFixture>
{
    private const string Extensions = "CallShapes.Arrays.ArrayExtensions";
    private const string Planks = "ExternalUser.Arrays.PlankExtensions";
    private const string Beams = "ExternalLib.Arrays.Beam[]";
    private const string Use =
        "CallShapes.Arrays.Users.Use(byte[], Spool[], Reel[], int[,], int[][], Shingle[], int[], List<int>)";
    private const string NotLoaded = "ILens could not load ExternalLib, Version=1.0.0.0";

    private readonly ILensServerFixture _server;
    private readonly ReferencesServerFixture _references;
    private readonly string _callShapesPath;
    private readonly string _externalUserPath;

    public ArrayExtensionTests(ILensServerFixture server, ReferencesServerFixture references)
    {
        _server = server;
        _references = references;
        _callShapesPath = FixturePath("CallShapes");
        _externalUserPath = FixturePath("ExternalUser");
    }

    private string FixturePath(string fixture)
    {
        string path = Path.Combine(Path.GetDirectoryName(_server.AssemblyPath)!, fixture + ".dll");
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"{fixture} fixture not found: {path}. " +
                $"Tests/Fixtures/{fixture}/ is expected to land here via Tests.csproj's ProjectReference.");
        return path;
    }

    // The array written with a keyword or by its element's full name, of two dimensions, of
    // arrays, of a class, of a struct, of a construction and of a nullable value type. Wind takes
    // an array of Spool and is listed there alone, not under the array of Reel that converts to
    // it; Crest asks for a struct that compares with itself, which byte and int are and Shingle
    // is not.
    [Theory]
    [InlineData("byte[]",
        $"3 extension methods for System.Byte[]:\n{Extensions}.Crest<T>(T[])\n{Extensions}.Hex(byte[])\n" +
        $"{Extensions}.InBounds<T>(T[], int)")]
    [InlineData("System.Byte[]",
        $"3 extension methods for System.Byte[]:\n{Extensions}.Crest<T>(T[])\n{Extensions}.Hex(byte[])\n" +
        $"{Extensions}.InBounds<T>(T[], int)")]
    [InlineData("int[]",
        $"4 extension methods for System.Int32[]:\n{Extensions}.CopyTo(int[], List<int>)\n{Extensions}.Crest<T>(T[])\n" +
        $"{Extensions}.InBounds<T>(T[], int)\n{Extensions}.Reverse(int[])")]
    [InlineData("int[,]", $"2 extension methods for System.Int32[,]:\n{Extensions}.Squares(int[,])\n{Extensions}.Tiles<T>(T[,])")]
    [InlineData("int[][]",
        $"2 extension methods for System.Int32[][]:\n{Extensions}.InBounds<T>(T[], int)\n{Extensions}.Tiers(int[][])")]
    [InlineData("CallShapes.Arrays.Spool[]",
        $"2 extension methods for CallShapes.Arrays.Spool[]:\n{Extensions}.InBounds<T>(T[], int)\n{Extensions}.Wind(Spool[])")]
    [InlineData("CallShapes.Arrays.Reel[]", $"1 extension method for CallShapes.Arrays.Reel[]:\n{Extensions}.InBounds<T>(T[], int)")]
    [InlineData("CallShapes.Arrays.Shingle[]",
        $"1 extension method for CallShapes.Arrays.Shingle[]:\n{Extensions}.InBounds<T>(T[], int)")]
    [InlineData("System.Collections.Generic.List<int>[]",
        $"1 extension method for System.Collections.Generic.List<int>[]:\n{Extensions}.InBounds<T>(T[], int)")]
    [InlineData("int?[]", $"1 extension method for System.Nullable<int>[]:\n{Extensions}.InBounds<T>(T[], int)")]
    public async Task ExtensionMethods_OnAnArrayType_ListsThoseForThatArrayAndForAnArrayOfATypeParameterItFits(
        string typeName, string expected)
    {
        Assert.Equal(
            expected,
            await CallToolText("analyze", new()
            {
                ["assembly"] = _callShapesPath,
                ["type_name"] = typeName,
                ["kind"] = "extension_methods",
            }));
    }

    // ExternalUser's NativeArrays writes its receivers as nint[] and dynamic[], which C# takes for
    // System.IntPtr[] and object[], so each spelling of the array lists the method.
    [Theory]
    [InlineData("nint[]", "1 extension method for System.IntPtr[]:\nExternalUser.Arrays.NativeArrays.Sum(nint[])")]
    [InlineData("System.IntPtr[]", "1 extension method for System.IntPtr[]:\nExternalUser.Arrays.NativeArrays.Sum(nint[])")]
    [InlineData("object[]", "1 extension method for System.Object[]:\nExternalUser.Arrays.NativeArrays.Dyn(dynamic[])")]
    public async Task ExtensionMethods_OnAnArrayTheReceiverSpellsOtherwise_ListsTheMethod(string typeName, string expected)
    {
        Assert.Equal(
            expected,
            await CallToolText("analyze", new()
            {
                ["assembly"] = _externalUserPath,
                ["type_name"] = typeName,
                ["kind"] = "extension_methods",
            }));
    }

    [Fact]
    public async Task ExtensionMethods_OnAnArrayType_InAnAssemblyWithoutAny_AnswersThatThereAreNone()
    {
        Assert.Equal(
            "No extension methods for System.Byte[]",
            await CallToolText("analyze", new()
            {
                ["assembly"] = FixturePath("ExplicitMembers"),
                ["type_name"] = "byte[]",
                ["kind"] = "extension_methods",
            }));
    }

    // The method for the array itself, for an array its element converts to, for an array of a
    // type parameter with and without constraints, also where the type parameter stands for an
    // array, for an interface the array implements, IReadOnlyCollection<T> among them, which the
    // decompiler's own rule leaves out, also through an array its element converts to, for
    // System.Array and for object. Reverse is also the name of a static method of System.Array,
    // which C# does not call on an array.
    [Theory]
    [InlineData("byte[]", "Hex", $"// {Extensions}.Hex(byte[]) → string [extension method on System.Byte[]]")]
    [InlineData("CallShapes.Arrays.Reel[]", "Wind",
        $"// {Extensions}.Wind(Spool[]) → int [extension method on CallShapes.Arrays.Spool[]]")]
    [InlineData("int[]", "Crest",
        $"// {Extensions}.Crest<T>(T[]) → T [extension method on T[] where T : struct, System.IComparable<T>]")]
    [InlineData("int[]", "InBounds", $"// {Extensions}.InBounds<T>(T[], int) → bool [extension method on T[]]")]
    [InlineData("int[][,]", "InBounds", $"// {Extensions}.InBounds<T>(T[], int) → bool [extension method on T[]]")]
    [InlineData("int[,]", "Tiles", $"// {Extensions}.Tiles<T>(T[,]) → int [extension method on T[,]]")]
    [InlineData("byte[]", "Front",
        $"// {Extensions}.Front<T>(IEnumerable<T>) → T [extension method on System.Collections.Generic.IEnumerable<T>]")]
    [InlineData("byte[]", "Tally",
        $"// {Extensions}.Tally<T>(IReadOnlyCollection<T>) → int [extension method on System.Collections.Generic.IReadOnlyCollection<T>]")]
    [InlineData("CallShapes.Arrays.Reel[]", "Gauge",
        $"// {Extensions}.Gauge(IReadOnlyCollection<Spool>) → int [extension method on System.Collections.Generic.IReadOnlyCollection<Spool>]")]
    [InlineData("byte[]", "Breadth", $"// {Extensions}.Breadth(Array) → int [extension method on System.Array]")]
    [InlineData("byte[]", "Caption", $"// {Extensions}.Caption(object) → string [extension method on System.Object]")]
    [InlineData("int[]", "Reverse", $"// {Extensions}.Reverse(int[]) → int[] [extension method on System.Int32[]]")]
    public async Task Decompile_ThroughAnArrayType_FindsTheExtensionMethodCodeCanCallOnIt(
        string typeName, string memberName, string header)
    {
        string code = await CallToolText("decompile", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = typeName,
            ["member_name"] = memberName,
        });

        Assert.Equal(header, code.Split('\n')[0]);
        Assert.Contains("public static", code);
        Assert.DoesNotContain("Not checked", code);
    }

    // CopyTo is a public instance method of System.Array, Length its property and ToString a method
    // of object, which come before an extension method of the name, so the question is the one
    // asked through the class that declares them, which the assembly only references. For an array
    // of Bobbin, Length is an extension method as well, which decompile passes over for the property.
    [Theory]
    [InlineData("int[]", "CopyTo", "System.Array")]
    [InlineData("byte[]", "Length", "System.Array")]
    [InlineData("CallShapes.Arrays.Bobbin[]", "Length", "System.Array")]
    [InlineData("byte[]", "ToString", "System.Object")]
    public async Task Decompile_ThroughAnArrayType_APublicInstanceMemberOfArrayOrObject_IsAskedThroughItsClass(
        string typeName, string memberName, string owner)
    {
        var (isError, text) = await CallTool("decompile", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = typeName,
            ["member_name"] = memberName,
        });

        Assert.True(isError);
        Assert.Equal(
            $"Error: ArgumentException: {owner} is not defined in this assembly; it comes from netstandard, Version=2.0.0.0.",
            text);
        Assert.Equal(
            (isError, text),
            await CallTool("decompile", new()
            {
                ["assembly"] = _callShapesPath,
                ["type_name"] = owner,
                ["member_name"] = memberName,
            }));
    }

    // ExternalUser does not name System.Array, since its code uses arrays through their own
    // instructions alone, so the answer says where the class comes from as it does where the
    // assembly references the class; the library depends on the .NET installed.
    [Fact]
    public async Task ThroughAnArrayType_AMemberOfAClassTheAssemblyDoesNotName_SaysWhereTheClassComesFrom()
    {
        var (isError, text) = await CallTool("decompile", new()
        {
            ["assembly"] = _externalUserPath,
            ["type_name"] = "int[]",
            ["member_name"] = "Length",
        });
        Assert.True(isError);
        Assert.StartsWith("Error: ArgumentException: System.Array is not defined in this assembly; it comes from ", text);

        (isError, text) = await CallTool("compare", new()
        {
            ["assembly_a"] = _externalUserPath,
            ["assembly_b"] = _externalUserPath,
            ["type_name"] = "int[]",
            ["member_name"] = "Length",
        });
        Assert.True(isError);
        Assert.StartsWith("Error: ArgumentException: Type 'System.Array' is not defined in either assembly; it comes from ", text);
    }

    [Fact]
    public async Task Analyze_ThroughAnArrayType_APublicInstanceMemberOfArray_IsAskedThroughItsClass()
    {
        var arguments = new Dictionary<string, object?>
        {
            ["assembly"] = _callShapesPath,
            ["member_name"] = "CopyTo",
            ["kind"] = "used_by",
        };

        string text = await CallToolText("analyze", new(arguments) { ["type_name"] = "int[]" });
        Assert.Equal(
            "No users of System.Array.CopyTo\n" +
            "This assembly references no member CopyTo of System.Array. It references: Empty, GetEnumerator, Length.",
            text);
        Assert.Equal(text, await CallToolText("analyze", new(arguments) { ["type_name"] = "System.Array" }));
    }

    // Parameter types that fit the extension method and no method of System.Array select it.
    [Fact]
    public async Task Decompile_ThroughAnArrayType_ParameterTypesThatFitOnlyTheExtensionMethod_SelectIt()
    {
        string code = await CallToolText("decompile", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = "int[]",
            ["member_name"] = "CopyTo",
            ["parameter_types"] = new[] { "int[]", "List<int>" },
        });

        Assert.Equal(
            $"// {Extensions}.CopyTo(int[], List<int>) → void [extension method on System.Int32[]]",
            code.Split('\n')[0]);
    }

    // Shingle is no IComparable, so Crest does not apply to an array of it; an array of two
    // dimensions is no IReadOnlyCollection<T>; and an array of byte does not convert to a
    // collection of Spool.
    [Theory]
    [InlineData("CallShapes.Arrays.Shingle[]", "Crest", "CallShapes.Arrays.Shingle[]")]
    [InlineData("int[,]", "Tally", "System.Int32[,]")]
    [InlineData("byte[]", "Gauge", "System.Byte[]")]
    public async Task Decompile_ThroughAnArrayType_ANameNothingThereBears_IsNotFound(
        string typeName, string memberName, string display)
    {
        var (isError, text) = await CallTool("decompile", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = typeName,
            ["member_name"] = memberName,
        });

        Assert.True(isError);
        Assert.Equal(
            $"Error: ArgumentException: Member '{memberName}' not found on {display}: no public instance member " +
            "of System.Array or System.Object and no extension method of this assembly that applies to it has that name.",
            text);
    }

    // Each kind is answered as through the class that declares the method; Users.Use calls Wind on
    // an array of Spool and on an array of Reel.
    [Theory]
    [InlineData("byte[]", "Hex", "used_by",
        $"1 user of {Extensions}.Hex(byte[]) [extension method on System.Byte[]]:\n{Use}")]
    [InlineData("CallShapes.Arrays.Reel[]", "Wind", "call_sites",
        $"2 calls to {Extensions}.Wind(Spool[]):\n{Use}\n  ArrayExtensions.Wind(spools)\n  ArrayExtensions.Wind(reels)")]
    [InlineData("byte[]", "Hex", "uses",
        $"1 symbol used by {Extensions}.Hex(byte[]) [extension method on System.Byte[]]:\nSystem.Int32.ToString()")]
    public async Task Analyze_ThroughAnArrayType_AnswersForTheExtensionMethod(
        string typeName, string memberName, string kind, string expected)
    {
        Assert.Equal(
            expected,
            await CallToolText("analyze", new()
            {
                ["assembly"] = _callShapesPath,
                ["type_name"] = typeName,
                ["member_name"] = memberName,
                ["kind"] = kind,
            }));
    }

    [Fact]
    public async Task Compare_ThroughAnArrayType_GivesTheVerdictOnTheExtensionMethod()
    {
        Assert.Equal(
            $"// {Extensions}.Hex(byte[]) → string [extension method on System.Byte[]] — bodies are identical in both assemblies.",
            await CallToolText("compare", new()
            {
                ["assembly_a"] = _callShapesPath,
                ["assembly_b"] = _callShapesPath,
                ["type_name"] = "byte[]",
                ["member_name"] = "Hex",
            }));
    }

    // Length is a property of System.Array and an extension method for an array of Bobbin, which
    // C# calls, since a property that is no delegate cannot be called. A question for a method by
    // name passes over the property, as through a type of another library: call_sites lists the
    // call in Users.Wound, and compare compares the extension method.
    [Fact]
    public async Task Analyze_CallSites_ThroughAnArrayType_PassesOverAPropertyOfArray()
    {
        Assert.Equal(
            $"1 call to {Extensions}.Length(Bobbin[]):\nCallShapes.Arrays.Users.Wound(Bobbin[])\n  ArrayExtensions.Length(bobbins)",
            await CallToolText("analyze", new()
            {
                ["assembly"] = _callShapesPath,
                ["type_name"] = "CallShapes.Arrays.Bobbin[]",
                ["member_name"] = "Length",
                ["kind"] = "call_sites",
            }));
    }

    [Fact]
    public async Task Compare_ThroughAnArrayType_PassesOverAPropertyOfArray()
    {
        Assert.Equal(
            $"// {Extensions}.Length(Bobbin[]) → int [extension method on CallShapes.Arrays.Bobbin[]] — " +
            "bodies are identical in both assemblies.",
            await CallToolText("compare", new()
            {
                ["assembly_a"] = _callShapesPath,
                ["assembly_b"] = _callShapesPath,
                ["type_name"] = "CallShapes.Arrays.Bobbin[]",
                ["member_name"] = "Length",
            }));
    }

    // Every question but those about the extension methods code can call on an array: the tools
    // that need a type's members or code, and decompile, analyze with another kind and compare
    // without member_name.
    public static TheoryData<string, string?, string?> QuestionsThatTakeNoArray => new()
    {
        { "list_members", null, null },
        { "find_methods", null, null },
        { "search_code", "regex", "x" },
        { "decompile", null, null },
        { "analyze", "kind", "used_by" },
        { "compare", null, null },
    };

    [Theory]
    [MemberData(nameof(QuestionsThatTakeNoArray))]
    public async Task AnArrayType_WhereAQuestionTakesNone_IsAnErrorThatNamesTheQuestionsThatDo(
        string toolName, string? parameter, string? value)
    {
        var arguments = toolName == "compare"
            ? new Dictionary<string, object?> { ["assembly_a"] = _callShapesPath, ["assembly_b"] = _callShapesPath }
            : new Dictionary<string, object?> { ["assembly"] = _callShapesPath };
        arguments["type_name"] = "byte[]";
        if (parameter is not null)
            arguments[parameter] = value;

        var (isError, text) = await CallTool(toolName, arguments);

        Assert.True(isError);
        Assert.Equal(
            "Error: ArgumentException: byte[] is an array type, which only analyze with kind 'extension_methods' and " +
            "decompile, analyze and compare with member_name take, to find the extension methods code can call on it.",
            text);
    }

    // An element that names no type, and one that leaves out the type arguments its type takes.
    [Theory]
    [InlineData("System.Prat[]", "Error: ArgumentException: Type not found: System.Prat.")]
    [InlineData("System.Collections.Generic.List[]",
        "Error: ArgumentException: An array type writes its element with its type arguments, as C# does: " +
        "System.Collections.Generic.List names System.Collections.Generic.List<>, which takes 1.")]
    public async Task Decompile_ThroughAnArrayType_AnElementThatNamesNoTypeAsWritten_IsAnError(
        string typeName, string expected)
    {
        var (isError, text) = await CallTool("decompile", new()
        {
            ["assembly"] = _callShapesPath,
            ["type_name"] = typeName,
            ["member_name"] = "InBounds",
        });

        Assert.True(isError);
        Assert.Equal(expected, text);
    }

    // Stack takes an array of Beam itself, which needs nothing read about Beam, so both servers
    // find it alike and neither says that anything was not read.
    [Fact]
    public async Task ThroughAnArrayOfAnotherLibrarysType_TheMethodForThatArray_IsFoundWithOrWithoutTheLibrary()
    {
        var arguments = new Dictionary<string, object?>
        {
            ["assembly"] = _externalUserPath,
            ["type_name"] = Beams,
            ["member_name"] = "Stack",
        };
        foreach (var client in new[] { _server.Client, _references.Client })
        {
            string code = await CallToolText(client, "decompile", arguments);
            Assert.Equal($"// {Planks}.Stack(Beam[]) → int [extension method on {Beams}]", code.Split('\n')[0]);
            Assert.DoesNotContain("Not checked", code);

            Assert.Equal(
                $"1 user of {Planks}.Stack(Beam[]) [extension method on {Beams}]:\nExternalUser.Arrays.Joinery.Build(Beam[])",
                await CallToolText(client, "analyze", new(arguments) { ["kind"] = "used_by" }));
        }
    }

    // With ExternalLib read, an array of Beam converts to an array of its base class Plank, and
    // Beam meets the constraint of Sand. Glue is then the name of two methods that apply.
    [Theory]
    [InlineData("Saw", $"// {Planks}.Saw(Plank[]) → int [extension method on ExternalLib.Arrays.Plank[]]")]
    [InlineData("Sand", $"// {Planks}.Sand<T>(T[]) → int [extension method on T[] where T : ExternalLib.Arrays.Plank]")]
    public async Task Decompile_ThroughAnArrayOfAnotherLibrarysType_FindsWhatItsElementDerivesFrom(
        string memberName, string header)
    {
        string code = await CallToolText(_references.Client, "decompile", new()
        {
            ["assembly"] = _externalUserPath,
            ["type_name"] = Beams,
            ["member_name"] = memberName,
        });

        Assert.Equal(header, code.Split('\n')[0]);
        Assert.DoesNotContain("Not checked", code);
    }

    [Fact]
    public async Task Decompile_ThroughAnArrayOfAnotherLibrarysType_ANameTwoMethodsThatApplyBear_IsAnOverloadError()
    {
        var (isError, text) = await CallTool(_references.Client, "decompile", new()
        {
            ["assembly"] = _externalUserPath,
            ["type_name"] = Beams,
            ["member_name"] = "Glue",
        });

        Assert.True(isError);
        Assert.Equal(
            "Error: ArgumentException: 'Glue' on (extension methods) has 2 matching overloads; specify parameter_count or " +
            $"parameter_types to disambiguate: {Planks}.Glue(Beam[]), {Planks}.Glue(Plank[], int).",
            text);
    }

    [Fact]
    public async Task ExtensionMethods_OnAnArrayOfAnotherLibrarysType_ListsTheMethodsOfATypeParameterItsElementFits()
    {
        Assert.Equal(
            $"3 extension methods for {Beams}:\n{Planks}.Glue(Beam[])\n{Planks}.Sand<T>(T[])\n{Planks}.Stack(Beam[])",
            await CallToolText(_references.Client, "analyze", new()
            {
                ["assembly"] = _externalUserPath,
                ["type_name"] = Beams,
                ["kind"] = "extension_methods",
            }));
    }

    // The shared server cannot load ExternalLib, so what Beam derives from is not known: Saw and
    // Sand are not found, the list leaves Sand out, and the answers say what was not read.
    [Theory]
    [InlineData("Saw")]
    [InlineData("Sand")]
    public async Task Decompile_ThroughAnArrayOfATypeItCannotLoad_AMethodForWhatTheElementDerivesFrom_IsNotFound(
        string memberName)
    {
        var (isError, text) = await CallTool("decompile", new()
        {
            ["assembly"] = _externalUserPath,
            ["type_name"] = Beams,
            ["member_name"] = memberName,
        });

        Assert.True(isError);
        Assert.Equal(
            $"Error: ArgumentException: Member '{memberName}' not found on {Beams}: no public instance member of System.Array " +
            "or System.Object and no extension method of this assembly that applies to it has that name; what " +
            $"ExternalLib.Arrays.Beam derives from was not read, since {NotLoaded}.",
            text);
    }

    [Fact]
    public async Task ExtensionMethods_OnAnArrayOfATypeItCannotLoad_EndsWithWhatItDidNotRead()
    {
        Assert.Equal(
            $"2 extension methods for {Beams}:\n{Planks}.Glue(Beam[])\n{Planks}.Stack(Beam[])\n" +
            $"Not checked: whether more extension methods apply to {Beams} through what ExternalLib.Arrays.Beam " +
            $"derives from; {NotLoaded}",
            await CallToolText("analyze", new()
            {
                ["assembly"] = _externalUserPath,
                ["type_name"] = Beams,
                ["kind"] = "extension_methods",
            }));
    }

    // Glue is also the name of the method for an array of Plank, which the shared server cannot
    // tell applies, so the answer for the method for an array of Beam says so: under the header in
    // decompile, at the end in analyze, and first in compare.
    [Fact]
    public async Task ThroughAnArrayOfATypeItCannotLoad_AMethodWithANamesakeItCannotWeigh_SaysWhatItDidNotRead()
    {
        var arguments = new Dictionary<string, object?>
        {
            ["assembly"] = _externalUserPath,
            ["type_name"] = Beams,
            ["member_name"] = "Glue",
        };
        const string header = $"{Planks}.Glue(Beam[]) → int [extension method on {Beams}]";
        const string notChecked =
            $"Not checked: whether other extension methods named Glue apply to {Beams} through what " +
            $"ExternalLib.Arrays.Beam derives from; {NotLoaded}";

        string[] lines = (await CallToolText("decompile", arguments)).Split('\n');
        Assert.Equal("// " + header, lines[0]);
        Assert.Equal("// " + notChecked, lines[1]);

        Assert.Equal(
            $"1 user of {Planks}.Glue(Beam[]) [extension method on {Beams}]:\nExternalUser.Arrays.Joinery.Build(Beam[])\n" +
            notChecked,
            await CallToolText("analyze", new(arguments) { ["kind"] = "used_by" }));

        Assert.Equal(
            $"// {notChecked}\n// {header} — bodies are identical in both assemblies.",
            await CallToolText("compare", new()
            {
                ["assembly_a"] = _externalUserPath,
                ["assembly_b"] = _externalUserPath,
                ["type_name"] = Beams,
                ["member_name"] = "Glue",
            }));
    }

    private Task<string> CallToolText(string toolName, Dictionary<string, object?> arguments) =>
        CallToolText(_server.Client, toolName, arguments);

    private static async Task<string> CallToolText(
        McpClient client, string toolName, Dictionary<string, object?> arguments)
    {
        var (isError, text) = await CallTool(client, toolName, arguments);
        Assert.True(!isError, $"Tool '{toolName}' returned an error: {text}");
        return text;
    }

    private Task<(bool IsError, string Text)> CallTool(string toolName, Dictionary<string, object?> arguments) =>
        CallTool(_server.Client, toolName, arguments);

    private static async Task<(bool IsError, string Text)> CallTool(
        McpClient client, string toolName, Dictionary<string, object?> arguments)
    {
        CallToolResult result = await client.CallToolAsync(toolName, arguments);
        return (result.IsError == true,
            string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text)));
    }
}
