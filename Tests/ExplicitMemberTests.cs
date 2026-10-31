using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for naming a member that implements an interface member explicitly, and
/// for asking who uses one. C# names such a member after the interface as well,
/// <c>ExplicitMembers.IShape.Area</c>, never binds the plain name to it, and never calls it
/// by name either. The fixture under Tests/Fixtures/ExplicitMembers/
/// compiles to ExplicitMembers.dll alongside the shared inspection target.
/// </summary>
public sealed class ExplicitMemberTests : IClassFixture<ILensServerFixture>
{
    private readonly ILensServerFixture _server;
    private readonly string _explicitMembersPath;

    public ExplicitMemberTests(ILensServerFixture server)
    {
        _server = server;
        _explicitMembersPath = Path.Combine(
            Path.GetDirectoryName(_server.AssemblyPath)!,
            "ExplicitMembers.dll");
        if (!File.Exists(_explicitMembersPath))
            throw new FileNotFoundException(
                $"ExplicitMembers fixture not found: {_explicitMembersPath}. " +
                "Tests/Fixtures/ExplicitMembers/ is expected to land here via Tests.csproj's " +
                "ProjectReference.");
    }

    // The fixture type and the name decompile is asked for, an optional parameter-type hint,
    // and the header line of the answer, which names what was found.
    public static TheoryData<string, string, string?, string> Lookups => new()
    {
        // Implemented only explicitly, so the plain name reaches the implementation.
        { "Circle", "Area", null, "// ExplicitMembers.Circle.ExplicitMembers.IShape.Area() → double" },
        { "Circle", "Name", null, "// ExplicitMembers.Circle.ExplicitMembers.IShape.Name" },
        { "Circle", "Changed", null, "// ExplicitMembers.Circle.ExplicitMembers.IShape.Changed" },
        // An accessor, by its IL name.
        { "Circle", "get_Name", null, "// ExplicitMembers.Circle.ExplicitMembers.IShape.get_Name() → string" },
        {
            "Ring", "Area", null,
            "// ExplicitMembers.Ring.ExplicitMembers.IShape.Area() → double [inherited from ExplicitMembers.Circle]"
        },
        // A hint the ordinary Equals(object) does not satisfy reaches the explicit Equals(Circle).
        {
            "Circle", "Equals", "Circle",
            "// ExplicitMembers.Circle.System.IEquatable<ExplicitMembers.Circle>.Equals(Circle) → bool"
        },
        // An ordinary member of the name wins, as in C#, even with the same parameters.
        { "Circle", "Equals", null, "// ExplicitMembers.Circle.Equals(object) → bool" },
        { "Bag", "GetEnumerator", null, "// ExplicitMembers.Bag.GetEnumerator() → IEnumerator<int>" },
    };

    [Theory]
    [MemberData(nameof(Lookups))]
    public async Task Decompile_ByThePlainName_ReachesAnExplicitImplementationOnlyWhenNothingOrdinaryHasIt(
        string typeName, string memberName, string? parameterType, string header)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["assembly"] = _explicitMembersPath,
            ["type_name"] = $"ExplicitMembers.{typeName}",
            ["member_name"] = memberName,
        };
        if (parameterType is not null)
            arguments["parameter_types"] = new[] { parameterType };

        string text = await CallToolText("decompile", arguments);

        Assert.Equal(header, text.Split('\n')[0]);
    }

    // An analyze question about a member of Circle, and the header line of the answer, which
    // names what was consulted.
    public static TheoryData<string, string, string> AnalyzeQuestions => new()
    {
        { "Area", "used_by", "No users of ExplicitMembers.Circle.ExplicitMembers.IShape.Area() [declared]" },
        {
            "Name", "read_by",
            "No readers of ExplicitMembers.Circle.ExplicitMembers.IShape.Name [declared] (via ExplicitMembers.IShape.get_Name)"
        },
        {
            "Changed", "used_by",
            "No users of ExplicitMembers.Circle.ExplicitMembers.IShape.Changed [declared] " +
            "(via ExplicitMembers.IShape.add_Changed, ExplicitMembers.IShape.remove_Changed)"
        },
        // Size is an ordinary field as well as the explicit ISized.Size. The field wins, rather
        // than the name being reported as two member kinds.
        { "Size", "read_by", "3 readers of ExplicitMembers.Circle.Size [declared]:" },
    };

    [Theory]
    [MemberData(nameof(AnalyzeQuestions))]
    public async Task Analyze_ByThePlainName_ReachesAnExplicitImplementationOnlyWhenNothingOrdinaryHasIt(
        string memberName, string kind, string header)
    {
        string text = await CallToolText("analyze", new()
        {
            ["assembly"] = _explicitMembersPath,
            ["type_name"] = "ExplicitMembers.Circle",
            ["member_name"] = memberName,
            ["kind"] = kind,
        });

        Assert.Equal(header, text.Split('\n')[0]);
    }

    [Fact]
    public async Task Analyze_ExposedBy_OnATypeTheAssemblyDefines_ListsTheMembersThatExposeIt()
    {
        // For a type the assembly defines, the ILSpyX analyzer answers, and an analyzer that
        // is passed over answers like one that found nothing. This pins that it runs.
        string text = await CallToolText("analyze", new()
        {
            ["assembly"] = _explicitMembersPath,
            ["type_name"] = "ExplicitMembers.IShape",
            ["kind"] = "exposed_by",
        });

        Assert.Equal(
            "2 members exposing ExplicitMembers.IShape:\n" +
            "ExplicitMembers.ShapeUser.Label(IShape)\n" +
            "ExplicitMembers.ShapeUser.Measure(IShape)",
            text);
    }

    [Fact]
    public async Task DecompileMethod_ByTheFullNameOfAnExplicitAccessor_ReachesIt()
    {
        // The accessor's name carries the interface in front of its IL name, as analyze prints
        // it when it names the accessors it consulted.
        string text = await CallToolText("decompile", new()
        {
            ["assembly"] = _explicitMembersPath,
            ["type_name"] = "ExplicitMembers.Circle",
            ["member_name"] = "ExplicitMembers.IShape.get_Name",
        });

        Assert.Equal("// ExplicitMembers.Circle.ExplicitMembers.IShape.get_Name() → string", text.Split('\n')[0]);
    }

    [Fact]
    public async Task FindCallSites_ByThePlainName_ReachesAnExplicitImplementation()
    {
        string text = await CallToolText("analyze", new()
        {
            ["assembly"] = _explicitMembersPath,
            ["type_name"] = "ExplicitMembers.Circle",
            ["member_name"] = "Area",
            ["kind"] = "call_sites",
        });

        Assert.Contains("ExplicitMembers.Circle.ExplicitMembers.IShape.Area()", text.Split('\n')[0]);
    }

    [Fact]
    public async Task DecompileMethod_OnANameSeveralExplicitImplementationsShare_ListsTheirFullNames()
    {
        // Vault implements both GetEnumerator methods explicitly and with the same parameters,
        // so only the full name tells them apart, and the full name then finds one.
        var (result, text) = await CallToolExpectingError("decompile", new()
        {
            ["assembly"] = _explicitMembersPath,
            ["type_name"] = "ExplicitMembers.Vault",
            ["member_name"] = "GetEnumerator",
        });

        Assert.True(result.IsError, $"Expected an ambiguity error, got: {text}");
        Assert.Contains(
            "'GetEnumerator' on ExplicitMembers.Vault names 2 explicit interface implementations: " +
            "System.Collections.Generic.IEnumerable<System.Int32>.GetEnumerator(), " +
            "System.Collections.IEnumerable.GetEnumerator(). Pass the full name of one instead.", text);

        string chosen = await CallToolText("decompile", new()
        {
            ["assembly"] = _explicitMembersPath,
            ["type_name"] = "ExplicitMembers.Vault",
            ["member_name"] = "System.Collections.IEnumerable.GetEnumerator",
        });
        Assert.Equal(
            "// ExplicitMembers.Vault.System.Collections.IEnumerable.GetEnumerator() → IEnumerator",
            chosen.Split('\n')[0]);
    }

    [Fact]
    public async Task DecompileMethod_WithAHintNothingSatisfies_ListsTheExplicitImplementationAmongTheAvailable()
    {
        var (result, text) = await CallToolExpectingError("decompile", new()
        {
            ["assembly"] = _explicitMembersPath,
            ["type_name"] = "ExplicitMembers.Circle",
            ["member_name"] = "Equals",
            ["parameter_types"] = new[] { "string" },
        });

        Assert.True(result.IsError, $"Expected a no-matching-overload error, got: {text}");
        Assert.Contains(
            "Available: ExplicitMembers.Circle.Equals(object), " +
            "ExplicitMembers.Circle.System.IEquatable<ExplicitMembers.Circle>.Equals(Circle).", text);
    }

    // A usage question analyze asks about a member of Circle, and the line that names the
    // interface member code can call instead, or null for a member that implements none.
    public static TheoryData<string, string, string?, string?> UsageQuestions => new()
    {
        {
            "Area", "used_by", null,
            "Implementation: code can call ExplicitMembers.IShape.Area() instead; ask about that member for its uses."
        },
        {
            "Name", "read_by", null,
            "Implementation: code can call ExplicitMembers.IShape.Name instead; ask about that member for its uses."
        },
        {
            "Changed", "used_by", null,
            "Implementation: code can call ExplicitMembers.IShape.Changed instead; ask about that member for its uses."
        },
        // The interface is in another assembly; its member is still named.
        {
            "Equals", "used_by", "Circle",
            "Implementation: code can call System.IEquatable<Circle>.Equals(Circle) instead; " +
            "ask about that member for its uses."
        },
        { "Equals", "used_by", "object", null },
        {
            "Area", "call_sites", null,
            "Implementation: code can call ExplicitMembers.IShape.Area() instead; ask about that member for its uses."
        },
        {
            "get_Name", "call_sites", null,
            "Implementation: code can call ExplicitMembers.IShape.get_Name() instead; ask about that member for its uses."
        },
        { "Equals", "call_sites", null, null },
    };

    [Theory]
    [MemberData(nameof(UsageQuestions))]
    public async Task UsageOfAnExplicitImplementation_NamesTheInterfaceMemberCodeReachesItThrough(
        string memberName, string kind, string? parameterType, string? route)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["assembly"] = _explicitMembersPath,
            ["type_name"] = "ExplicitMembers.Circle",
            ["member_name"] = memberName,
            ["kind"] = kind,
        };
        if (parameterType is not null)
            arguments["parameter_types"] = new[] { parameterType };

        string[] lines = (await CallToolText("analyze", arguments)).Split('\n');

        if (route is null)
            Assert.DoesNotContain(lines, line => line.StartsWith("Implementation:", StringComparison.Ordinal));
        else
            Assert.Equal(route, lines[^1]);
    }

    [Fact]
    public async Task UsageOfTheInterfaceMember_FindsTheCallsThatReachAnExplicitImplementation()
    {
        // The member the line names answers the question the implementation cannot.
        string used = await CallToolText("analyze", new()
        {
            ["assembly"] = _explicitMembersPath,
            ["type_name"] = "ExplicitMembers.IShape",
            ["member_name"] = "Area",
            ["kind"] = "used_by",
        });
        Assert.Equal(
            "1 user of ExplicitMembers.IShape.Area() [declared]:\nExplicitMembers.ShapeUser.Measure(IShape)",
            used);

        string calls = await CallToolText("analyze", new()
        {
            ["assembly"] = _explicitMembersPath,
            ["type_name"] = "ExplicitMembers.IShape",
            ["member_name"] = "Area",
            ["kind"] = "call_sites",
        });
        Assert.Equal(
            "1 call to ExplicitMembers.IShape.Area():\nExplicitMembers.ShapeUser.Measure(IShape)\n  shape.Area()",
            calls);
    }

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
