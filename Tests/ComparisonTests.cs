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

    // A type the type list marks metadata ends its member changes with its two declarations, as
    // list_members writes the heading, since no member shows such a change: a base class or an
    // interface, a condition and an in or out. Rebased.Mover's base class gives way to a class of
    // the same name in another namespace, so its declarations are written in full, and so are
    // Rebased.Sealer's, whose base class moves so while it becomes sealed. Gauge, whose accessor
    // bodies alone change, has no such line.
    [Theory]
    [InlineData("BaseShift.InterfaceArgumentMover", new[]
    {
        "No member changes in BaseShift.InterfaceArgumentMover",
        "Declaration: public class BaseShift.InterfaceArgumentMover : IOwner<int> → public class BaseShift.InterfaceArgumentMover : IOwner<string>",
    })]
    [InlineData("BaseShift.BaseArgumentMover", new[]
    {
        "1 member change in BaseShift.BaseArgumentMover:",
        "~ public BaseArgumentMover()",
        "Declaration: public class BaseShift.BaseArgumentMover : Owner<int> → public class BaseShift.BaseArgumentMover : Owner<string>",
    })]
    [InlineData("BaseShift.Rebased.Mover", new[]
    {
        "1 member change in BaseShift.Rebased.Mover:",
        "~ public Mover()",
        "Declaration: public class BaseShift.Rebased.Mover : BaseShift.Rebased.Alpha.Origin → public class BaseShift.Rebased.Mover : BaseShift.Rebased.Beta.Origin",
    })]
    [InlineData("BaseShift.Rebased.Sealer", new[]
    {
        "1 member change in BaseShift.Rebased.Sealer:",
        "~ public Sealer()",
        "Declaration: public class BaseShift.Rebased.Sealer : BaseShift.Rebased.Alpha.Origin → public sealed class BaseShift.Rebased.Sealer : BaseShift.Rebased.Beta.Origin",
    })]
    [InlineData("BaseShift.Declarations.Holder<>", new[]
    {
        "No member changes in BaseShift.Declarations.Holder<>",
        "Declaration: public class BaseShift.Declarations.Holder<T> → public class BaseShift.Declarations.Holder<T> where T : class",
    })]
    [InlineData("BaseShift.Variance.ISource<>", new[]
    {
        "No member changes in BaseShift.Variance.ISource<>",
        "Declaration: public interface BaseShift.Variance.ISource<T> → public interface BaseShift.Variance.ISource<out T>",
    })]
    [InlineData("BaseShift.Gauge", new[]
    {
        "3 member changes in BaseShift.Gauge:",
        "~ public event EventHandler Changed [add_Changed]",
        "~ public int Level { get; set; } [get_Level]",
        "~ public int Limit { get; set; } [set_Limit]",
    })]
    public async Task Compare_MemberChanges_OfATypeWhoseDeclarationChanged_EndWithBothDeclarations(
        string typeName, string[] expected)
    {
        string text = await CallToolText("compare", new()
        {
            ["assembly_a"] = BaseShiftPath("BaseShift.dll"),
            ["assembly_b"] = BaseShiftPath("BaseShift2.dll"),
            ["type_name"] = typeName,
        });

        Assert.Equal(expected, text.Split('\n'));
    }

    [Fact]
    public async Task Compare_MemberChanges_OfATypeWhoseDeclarationChanged_CutByLimit_StillEndWithBothDeclarations()
    {
        string text = await CallToolText("compare", new()
        {
            ["assembly_a"] = BaseShiftPath("BaseShift.dll"),
            ["assembly_b"] = BaseShiftPath("BaseShift2.dll"),
            ["type_name"] = "BaseShift.BaseArgumentMover",
            ["limit"] = 0,
        });

        Assert.Equal(
            new[]
            {
                "1 member change in BaseShift.BaseArgumentMover:",
                "... (1 more; raise limit to see all)",
                "Declaration: public class BaseShift.BaseArgumentMover : Owner<int> → public class BaseShift.BaseArgumentMover : Owner<string>",
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
    public async Task Compare_Bodies_OfAChangedAccessor_ShowsTheChangedLines()
    {
        // The accessor a member change names is what member_name takes to compare its two bodies.
        string text = await CallToolText("compare", new()
        {
            ["assembly_a"] = BaseShiftPath("BaseShift.dll"),
            ["assembly_b"] = BaseShiftPath("BaseShift2.dll"),
            ["type_name"] = "BaseShift.Gauge",
            ["member_name"] = "get_Level",
        });

        var lines = text.Split('\n');
        Assert.StartsWith("--- A: ", lines[0]);
        Assert.Contains("-\treturn _value;", lines);
        Assert.Contains("+\treturn _value + 1;", lines);
    }

    private string BaseShiftPath(string fileName) =>
        Path.Combine(Path.GetDirectoryName(_server.AssemblyPath)!, fileName);

    [Fact]
    public async Task Compare_ChangedTypes_WhenOverloadsPrintAlike_ComparesEachWithItsOwn()
    {
        // Each class in BaseShift.Overloads but Replaced has two overloads whose types ILens's short
        // form prints alike, and BaseShift2 gives the second another body. Matched by the short
        // form, the two would share one key, which ended the comparison of the whole assembly.
        string text = await CallToolText("compare", new()
        {
            ["assembly_a"] = BaseShiftPath("BaseShift.dll"),
            ["assembly_b"] = BaseShiftPath("BaseShift2.dll"),
            ["namespace"] = "BaseShift.Overloads",
        });

        Assert.Equal(
            new[]
            {
                "8 changed types:",
                "~ BaseShift.Overloads.ByArgument [bodies]",
                "~ BaseShift.Overloads.ByIndex [bodies]",
                "~ BaseShift.Overloads.ByNamespace [bodies]",
                "~ BaseShift.Overloads.ByNesting [bodies]",
                "~ BaseShift.Overloads.ByParameter<> [bodies]",
                "~ BaseShift.Overloads.ByRank [bodies]",
                "~ BaseShift.Overloads.ByReturn [bodies]",
                "~ BaseShift.Overloads.Replaced [members,declarations]",
            },
            text.Split('\n'));
    }

    [Theory]
    [InlineData("BaseShift.Overloads.ByNamespace", "~ public int Take(BaseShift.Overloads.Right.Token token)")]
    [InlineData("BaseShift.Overloads.ByNesting", "~ public int Take(BaseShift.Overloads.Right.Crate.Entry entry)")]
    [InlineData("BaseShift.Overloads.ByRank", "~ public int Take(int[,] values)")]
    [InlineData("BaseShift.Overloads.ByReturn",
        "~ public static BaseShift.Overloads.Right.Token op_Implicit(BaseShift.Overloads.ByReturn value)")]
    [InlineData("BaseShift.Overloads.ByIndex", "~ public int Item[BaseShift.Overloads.Right.Token token] { get; } [get_Item]")]
    [InlineData("BaseShift.Overloads.ByArgument",
        "~ public int Take(System.Collections.Generic.List<BaseShift.Overloads.Right.Token> tokens)")]
    [InlineData("BaseShift.Overloads.ByParameter<>", "~ public int Take<T>(T value)")]
    public async Task Compare_MemberChanges_WhenOverloadsPrintAlike_ListsOnlyTheChangedOne(string typeName, string line)
    {
        // The count says that each overload was matched with its own. The line is written in
        // full, since in the short form it would read alike the overload that did not change,
        // which the list does not show; ByRank's and ByParameter's print apart.
        string text = await CallToolText("compare", new()
        {
            ["assembly_a"] = BaseShiftPath("BaseShift.dll"),
            ["assembly_b"] = BaseShiftPath("BaseShift2.dll"),
            ["type_name"] = typeName,
        });

        Assert.Equal(new[] { $"1 member change in {typeName}:", line }, text.Split('\n'));
    }

    [Theory]
    [InlineData("BaseShift.Overloads.ByNamespace", "Right.Token", true)]
    [InlineData("BaseShift.Overloads.ByNamespace", "Left.Token", false)]
    [InlineData("BaseShift.Overloads.ByNesting", "Crate.Entry", true)]
    [InlineData("BaseShift.Overloads.ByNesting", "Shelf.Entry", false)]
    public async Task Compare_Bodies_OfOverloadsThatPrintAlike_ShowWhichOneChanged(
        string typeName, string parameterType, bool changed)
    {
        // A parameter type qualified as far as it differs reaches each overload on its own.
        string text = await CallToolText("compare", new()
        {
            ["assembly_a"] = BaseShiftPath("BaseShift.dll"),
            ["assembly_b"] = BaseShiftPath("BaseShift2.dll"),
            ["type_name"] = typeName,
            ["member_name"] = "Take",
            ["parameter_types"] = new[] { parameterType },
        });

        if (changed)
        {
            Assert.Contains("return 2;", text);
            Assert.Contains("return 3;", text);
        }
        else
            Assert.EndsWith("— bodies are identical in both assemblies.", text);
    }

    [Fact]
    public async Task Compare_MemberChanges_WhenAParameterTypeGivesWayToOneOfTheSameShortName_ListsARemovalAndAnAddition()
    {
        // Hold's parameter type moves from BaseShift.Overloads.Left.Token to Right.Token, which
        // makes another method of it, written in full, since in the short form the two lines
        // would read alike. Pass's type parameter is renamed, which leaves the method the same
        // member and changes its declaration.
        string text = await CallToolText("compare", new()
        {
            ["assembly_a"] = BaseShiftPath("BaseShift.dll"),
            ["assembly_b"] = BaseShiftPath("BaseShift2.dll"),
            ["type_name"] = "BaseShift.Overloads.Replaced",
        });

        Assert.Equal(
            new[]
            {
                "3 member changes in BaseShift.Overloads.Replaced:",
                "- public int Hold(BaseShift.Overloads.Left.Token token)",
                "+ public int Hold(BaseShift.Overloads.Right.Token token)",
                "~ public T Pass<T>(T item) → public TItem Pass<TItem>(TItem item)",
            },
            text.Split('\n'));
    }

    [Theory]
    [InlineData("BaseShift.Signatures.Returns", "Open",
        "BaseShift.Signatures.Returns.Open() → object in A and BaseShift.Signatures.Returns.Open() → string in B")]
    [InlineData("BaseShift.Signatures.Descendant", "Moved",
        "BaseShift.Signatures.Descendant.Moved() → void in A and BaseShift.Signatures.Descendant.Moved() → void " +
        "[inherited from BaseShift.Signatures.Ancestor] in B")]
    [InlineData("BaseShift.Signatures.Extended", "Run",
        "BaseShift.Signatures.Extended.Run() → void in A and BaseShift.Signatures.Extensions.Run(Extended) → void " +
        "[extension method on BaseShift.Signatures.Extended] in B")]
    [InlineData("BaseShift.Signatures.Disposer", "Dispose",
        "BaseShift.Signatures.Disposer.Dispose() → void in A and " +
        "BaseShift.Signatures.Disposer.System.IDisposable.Dispose() → void in B")]
    [InlineData("BaseShift.Overloads.Replaced", "Hold",
        "BaseShift.Overloads.Replaced.Hold(BaseShift.Overloads.Left.Token) → int in A and " +
        "BaseShift.Overloads.Replaced.Hold(BaseShift.Overloads.Right.Token) → int in B")]
    public async Task Compare_Bodies_WhenTheNameFindsAnotherMemberInEachAssembly_SaysTheyAreDifferentMethods(
        string typeName, string memberName, string methods)
    {
        // The member list counts each pair as a removal and an addition, since it matches a member
        // by its declaring type and signature. Asked by name, each assembly finds a method all the
        // same, and the line names both before the verdict on their bodies, which are identical
        // here. Hold's two are written in full, since in the short form they would read alike.
        string text = await CallToolText("compare", new()
        {
            ["assembly_a"] = BaseShiftPath("BaseShift.dll"),
            ["assembly_b"] = BaseShiftPath("BaseShift2.dll"),
            ["type_name"] = typeName,
            ["member_name"] = memberName,
        });

        Assert.Equal($"// {methods} are different methods — bodies are identical in both assemblies.", text);
    }

    [Theory]
    [InlineData("csharp", "return 1L;")]
    [InlineData("il", "conv.i8")]
    public async Task Compare_Bodies_OfDifferentMethodsWhoseBodiesDiffer_NamesBothBeforeTheBodies(string format, string inB)
    {
        // The IL of a body does not show the method's signature, so in either format the line
        // before the bodies is what says that the two are different methods.
        string text = await CallToolText("compare", new()
        {
            ["assembly_a"] = BaseShiftPath("BaseShift.dll"),
            ["assembly_b"] = BaseShiftPath("BaseShift2.dll"),
            ["type_name"] = "BaseShift.Signatures.Returns",
            ["member_name"] = "Count",
            ["format"] = format,
        });

        Assert.StartsWith(
            "// BaseShift.Signatures.Returns.Count() → int in A and BaseShift.Signatures.Returns.Count() → long " +
            "in B are different methods\n\n--- A: ",
            text);
        Assert.Contains(inB, text);
    }

    [Fact]
    public async Task Compare_Bodies_OfAnInheritedMethodWhoseBodyChanged_SaysWhereItWasFoundBeforeTheBodies()
    {
        // The bodies show the method's declaration but not that Descendant inherits it, so the line
        // before them says where it was found.
        string text = await CallToolText("compare", new()
        {
            ["assembly_a"] = BaseShiftPath("BaseShift.dll"),
            ["assembly_b"] = BaseShiftPath("BaseShift2.dll"),
            ["type_name"] = "BaseShift.Signatures.Descendant",
            ["member_name"] = "Reworked",
        });

        Assert.StartsWith(
            "// BaseShift.Signatures.Descendant.Reworked() → int [inherited from BaseShift.Signatures.Ancestor]\n\n" +
            "--- A: ",
            text);
        Assert.Contains("return 2;", text);
    }

    [Theory]
    [InlineData("BaseShift.Signatures.Descendant", "Kept",
        "BaseShift.Signatures.Descendant.Kept() → void [inherited from BaseShift.Signatures.Ancestor]")]
    public async Task Compare_Bodies_WhenTheNameFindsOneMember_SaysOnlyThatTheBodiesAreIdentical(
        string typeName, string memberName, string method)
    {
        // Both assemblies inherit Kept from the base class, which the line says as decompile's
        // header does.
        string text = await CallToolText("compare", new()
        {
            ["assembly_a"] = BaseShiftPath("BaseShift.dll"),
            ["assembly_b"] = BaseShiftPath("BaseShift2.dll"),
            ["type_name"] = typeName,
            ["member_name"] = memberName,
        });

        Assert.Equal($"// {method} — bodies are identical in both assemblies.", text);
    }

    [Fact]
    public async Task Compare_Bodies_OfAnExtensionMethodOnlyOneAssemblyHas_SaysWhereItWasFound()
    {
        // Only BaseShift2 has Added, an extension method on Extended that Extensions declares.
        string text = await CallToolText("compare", new()
        {
            ["assembly_a"] = BaseShiftPath("BaseShift.dll"),
            ["assembly_b"] = BaseShiftPath("BaseShift2.dll"),
            ["type_name"] = "BaseShift.Signatures.Extended",
            ["member_name"] = "Added",
        });

        Assert.StartsWith(
            "// BaseShift.Signatures.Extensions.Added(Extended) → void " +
            "[extension method on BaseShift.Signatures.Extended] — only B has it.\n\n",
            text);
        Assert.Contains("public static void Added(this Extended extended)", text);
    }

    [Fact]
    public async Task Compare_ChangedTypes_WhenConstantsTakeOtherValues_NamesTheValues()
    {
        // In BaseShift.Values, BaseShift2 gives constants and enum members other values. A type whose
        // constants alone changed is listed with values, beside members where a member came or went,
        // and beside bodies where the compiler sets a decimal constant in the static constructor.
        // Width changes its underlying type with its values kept, which shows in its metadata alone,
        // since the field that holds an enum's value counts as generated. Season is only in
        // BaseShift2. Derived inherits Base's constant and declares nothing, and Steady keeps its
        // values.
        string text = await CallToolText("compare", new()
        {
            ["assembly_a"] = BaseShiftPath("BaseShift.dll"),
            ["assembly_b"] = BaseShiftPath("BaseShift2.dll"),
            ["namespace"] = "BaseShift.Values",
        });

        Assert.Equal(
            new[]
            {
                "9 changed types:",
                "~ BaseShift.Values.Base [values]",
                "~ BaseShift.Values.Limits [members,values]",
                "~ BaseShift.Values.Prices [values,bodies]",
                "+ BaseShift.Values.Season",
                "~ BaseShift.Values.Shade [values]",
                "~ BaseShift.Values.Stage [members,values]",
                "~ BaseShift.Values.Tall [values]",
                "~ BaseShift.Values.Token [members,values]",
                "~ BaseShift.Values.Width [metadata]",
            },
            text.Split('\n'));
    }

    [Theory]
    [InlineData("BaseShift.Values.Limits",
        "7 member changes in BaseShift.Values.Limits:\n" +
        "+ public const int Added = 7\n" +
        "~ public const int Changed = 5 → 6\n" +
        "~ public const string Empty = null → \"set\"\n" +
        "~ public const bool Open = false → true\n" +
        "~ public const string Text = \"before\" → \"after\"\n" +
        "- public const int Wide = 5\n" +
        "+ public const long Wide = 5L")]
    [InlineData("BaseShift.Values.Stage",
        "3 member changes in BaseShift.Values.Stage:\n" +
        "+ public const Stage Inserted = 1\n" +
        "~ public const Stage Second = 1 → 2\n" +
        "~ public const Stage Third = 2 → 3")]
    [InlineData("BaseShift.Values.Width",
        "No member changes in BaseShift.Values.Width\n" +
        "Declaration: public enum BaseShift.Values.Width : byte → public enum BaseShift.Values.Width : short")]
    [InlineData("BaseShift.Values.Prices",
        "2 member changes in BaseShift.Values.Prices:\n" +
        "~ static Prices()\n" +
        "~ public const decimal Price = 1.5m → 2.5m")]
    public async Task Compare_MemberChanges_OfConstantsThatTookOtherValues_ListsBothValues(string typeName, string expected)
    {
        // A constant that keeps its name and type is the same member with another value, listed once
        // with both values. The member inserted into Stage moves the members after it up, and each is
        // listed. Width's members keep their values while its underlying type changes, so no member
        // changes and its two headings show the change. Wide changes its type, so it is another
        // member.
        string text = await CallToolText("compare", new()
        {
            ["assembly_a"] = BaseShiftPath("BaseShift.dll"),
            ["assembly_b"] = BaseShiftPath("BaseShift2.dll"),
            ["type_name"] = typeName,
        });

        Assert.Equal(expected, text);
    }

    [Theory]
    [InlineData("BaseShift.Values.Limits", "Changed", "// BaseShift.Values.Limits.Changed — 5 in A, 6 in B.")]
    [InlineData("BaseShift.Values.Limits", "Kept", "// BaseShift.Values.Limits.Kept — 1 in both assemblies.")]
    [InlineData("BaseShift.Values.Limits", "Empty", "// BaseShift.Values.Limits.Empty — null in A, \"set\" in B.")]
    [InlineData("BaseShift.Values.Limits", "Open", "// BaseShift.Values.Limits.Open — false in A, true in B.")]
    [InlineData("BaseShift.Values.Shade", "Green", "// BaseShift.Values.Shade.Green — 2 in A, 3 in B.")]
    [InlineData("BaseShift.Values.Prices", "Price", "// BaseShift.Values.Prices.Price — 1.5m in A, 2.5m in B.")]
    [InlineData("BaseShift.Values.Derived", "Limit",
        "// BaseShift.Values.Derived.Limit [inherited from BaseShift.Values.Base] — 5 in A, 6 in B.")]
    [InlineData("BaseShift.Values.Limits", "Wide",
        "// BaseShift.Values.Limits.Wide → int in A and BaseShift.Values.Limits.Wide → long in B are different " +
        "constants — 5 in A, 5L in B.")]
    [InlineData("BaseShift.Values.Token", "Equals", "// BaseShift.Values.Token.Equals — 1 in A, 2 in B.")]
    [InlineData("BaseShift.Values.Tall", "Height", "// BaseShift.Values.Tall.Height — 2 in A, 3 in B.")]
    public async Task Compare_Bodies_OfAConstant_SaysItsValueInEachAssembly(
        string typeName, string memberName, string expected)
    {
        // A constant has a value where a method has a body. Derived inherits Limit from Base, which
        // the line says as decompile's header does. Wide is an int in A and a long in B, which the
        // member list counts as a removal and an addition, so the line names both with their types.
        // Token's Equals and Tall's Height hide a base class's method of their name, as in C#.
        string text = await CallToolText("compare", new()
        {
            ["assembly_a"] = BaseShiftPath("BaseShift.dll"),
            ["assembly_b"] = BaseShiftPath("BaseShift2.dll"),
            ["type_name"] = typeName,
            ["member_name"] = memberName,
        });

        Assert.Equal(expected, text);
    }

    [Theory]
    [InlineData("BaseShift.dll", "BaseShift2.dll", "B")]
    [InlineData("BaseShift2.dll", "BaseShift.dll", "A")]
    public async Task Compare_Bodies_OfAConstantOnlyOneAssemblyHas_SaysItsValueAndWhichHasIt(
        string assemblyA, string assemblyB, string side)
    {
        string text = await CallToolText("compare", new()
        {
            ["assembly_a"] = BaseShiftPath(assemblyA),
            ["assembly_b"] = BaseShiftPath(assemblyB),
            ["type_name"] = "BaseShift.Values.Limits",
            ["member_name"] = "Added",
        });

        Assert.Equal($"// BaseShift.Values.Limits.Added = 7 — only {side} has it.", text);
    }

    [Fact]
    public async Task Compare_Bodies_WithAHint_ReachesTheMethodAConstantHides()
    {
        // Tall's constant Height hides Shelf's method Height(); a hint asks for a method.
        string text = await CallToolText("compare", new()
        {
            ["assembly_a"] = BaseShiftPath("BaseShift.dll"),
            ["assembly_b"] = BaseShiftPath("BaseShift2.dll"),
            ["type_name"] = "BaseShift.Values.Tall",
            ["member_name"] = "Height",
            ["parameter_count"] = 0,
        });

        Assert.Equal(
            "// BaseShift.Values.Tall.Height() → int [inherited from BaseShift.Values.Shelf] — " +
            "bodies are identical in both assemblies.",
            text);
    }

    [Fact]
    public async Task Compare_Bodies_OfAFieldThatIsNoConstant_ReturnsError()
    {
        // A field that is no constant has neither a body nor a value the comparison reads.
        string text = await CallToolError("compare", new()
        {
            ["assembly_a"] = BaseShiftPath("BaseShift.dll"),
            ["assembly_b"] = BaseShiftPath("BaseShift2.dll"),
            ["type_name"] = "BaseShift.Values.Limits",
            ["member_name"] = "Ordinary",
        });

        Assert.Equal(
            "Error: ArgumentException: BaseShift.Values.Limits.Ordinary is a field, which has no body; " +
            "BaseShift.Values.Limits..cctor() sets it.",
            text);
    }

    [Theory]
    [InlineData("Settings", "Count", "BaseShift.Fields.Settings.Count is a field, which has no body; " +
        "BaseShift.Fields.Settings..cctor() sets it.")]
    [InlineData("Registry", "Name", "BaseShift.Fields.Registry.Name is a field, which has no body; " +
        "BaseShift.Fields.Registry..cctor() sets it.")]
    [InlineData("Gauge", "Size", "BaseShift.Fields.Gauge.Size is a field, which has no body; " +
        "BaseShift.Fields.Gauge..ctor() and BaseShift.Fields.Gauge..ctor(string) set it.")]
    [InlineData("Dial", "Shared", "BaseShift.Fields.Panel.Shared is a field, which has no body; " +
        "BaseShift.Fields.Panel..cctor() sets it.")]
    [InlineData("Store", "Stock", "BaseShift.Fields.Store.Stock is a field, which has no body; " +
        "no constructor of BaseShift.Fields.Store sets it, and assigned_by lists what does.")]
    [InlineData("Late", "Start", "BaseShift.Fields.Late.Start is a field, which has no body; " +
        "in A no constructor of BaseShift.Fields.Late sets it, and in B BaseShift.Fields.Late..cctor() sets it.")]
    [InlineData("Shrinking", "Gone", "BaseShift.Fields.Shrinking.Gone is a field, which has no body, " +
        "and only A has it; BaseShift.Fields.Shrinking..ctor() sets it.")]
    [InlineData("Tally", "Created", "BaseShift.Fields.Tally.Created is a field, which has no body; " +
        "BaseShift.Fields.Tally..ctor() may set it.")]
    [InlineData("Census", "Count", "BaseShift.Fields.Census.Count is a field, which has no body; " +
        "BaseShift.Fields.Census..cctor() sets it, and BaseShift.Fields.Census..ctor() may set it.")]
    [InlineData("Growing", "Fresh", "BaseShift.Fields.Growing.Fresh is a field, which has no body, " +
        "and only B has it; BaseShift.Fields.Growing..ctor() sets it.")]
    [InlineData("Arrival", "Gate", "BaseShift.Fields.Arrival.Gate is a field, which has no body, " +
        "and only B has it; BaseShift.Fields.Arrival..cctor() sets it.")]
    [InlineData("Pool<>", "Capacity", "BaseShift.Fields.Pool<>.Capacity is a field, which has no body; " +
        "BaseShift.Fields.Pool<>..cctor() sets it.")]
    [InlineData("Extent", "Width", "BaseShift.Fields.Extent.Width is a field, which has no body; " +
        "BaseShift.Fields.Extent..ctor(int) sets it.")]
    public async Task Compare_Bodies_OfAField_NamesTheConstructorsThatSetIt(
        string typeName, string memberName, string expected)
    {
        // An initializer compiles into the constructors, a static field's into the static one, and
        // an instance field's into each one that calls no other of its class; a constructor that
        // passes the field by reference to code of another assembly may set it.
        string text = await CallToolError("compare", new()
        {
            ["assembly_a"] = BaseShiftPath("BaseShift.dll"),
            ["assembly_b"] = BaseShiftPath("BaseShift2.dll"),
            ["type_name"] = $"BaseShift.Fields.{typeName}",
            ["member_name"] = memberName,
        });

        Assert.Equal($"Error: ArgumentException: {expected}", text);
    }

    [Fact]
    public async Task Compare_ChangedTypes_WhenADeclarationChanged_NamesTheKindDeclarations()
    {
        // Every member of BaseShift.Declarations keeps its name, parameter types and return type
        // and changes what its declaration line shows. Beside that, Fields' Max changes its value,
        // Kinds loses the static constructor that set Stride, and a body of Reworked, Accessors,
        // Shelf and Conditions changes. Holder's change is the condition on its type parameter,
        // which is the type's metadata, and Patch's override of a method whose condition changes
        // writes no condition, so Patch does not change. Keeper's type parameter is renamed with
        // its condition kept, and the conditions are compared by position, so Keeper does not
        // change either. Motor's abstract members hide Engine's in A and override them in B,
        // which only the word override shows.
        string text = await CallToolText("compare", new()
        {
            ["assembly_a"] = BaseShiftPath("BaseShift.dll"),
            ["assembly_b"] = BaseShiftPath("BaseShift2.dll"),
            ["namespace"] = "BaseShift.Declarations",
        });

        Assert.Equal(
            new[]
            {
                "11 changed types:",
                "~ BaseShift.Declarations.Accessors [declarations,bodies]",
                "~ BaseShift.Declarations.Cabinet [declarations]",
                "~ BaseShift.Declarations.Conditions [declarations,bodies]",
                "~ BaseShift.Declarations.Fields [declarations,values]",
                "~ BaseShift.Declarations.Holder<> [metadata]",
                "~ BaseShift.Declarations.Kinds [members,declarations]",
                "~ BaseShift.Declarations.Methods [declarations]",
                "~ BaseShift.Declarations.Motor [declarations]",
                "~ BaseShift.Declarations.Receivers [declarations]",
                "~ BaseShift.Declarations.Reworked [declarations,bodies]",
                "~ BaseShift.Declarations.Shelf [declarations,bodies]",
            },
            text.Split('\n'));
    }

    [Theory]
    [InlineData("Methods", new[]
    {
        "~ public Methods() → protected Methods()",
        "~ public void Close() → public virtual void Close()",
        "~ public int Count() → public static int Count()",
        "~ public void Fill(int count) → public void Fill(int amount)",
        "~ public int Gather(IEnumerable<int> values) → public int Gather(params IEnumerable<int> values)",
        "~ public void Open() → private void Open()",
        "~ public T Pick<T>(T item) → public TItem Pick<TItem>(TItem item)",
        "~ public int Spread(params IEnumerable<int> values) → public int Spread(IEnumerable<int> values)",
        "~ public int Sum(int[] values) → public int Sum(params int[] values)",
    })]
    [InlineData("Reworked", new[] { "~ public int Total(int a) → internal int Total(int a) [body]" })]
    [InlineData("Fields", new[]
    {
        "~ public int Level → internal int Level",
        "~ public static int Limit → public static readonly int Limit",
        "~ public const int Max = 5 → internal const int Max = 6",
        "~ public static int Ticks → public static volatile int Ticks",
        "~ public int Width → public readonly int Width",
    })]
    [InlineData("Kinds", new[] { "- static Kinds()", "~ public static readonly int Stride → public const int Stride = 4" })]
    [InlineData("Accessors", new[]
    {
        "~ public int Depth { get; } → public int Depth { get; set; } [set_Depth]",
        "~ public string Name { get; set; } → public string Name { get; init; }",
        "~ public event EventHandler Opened → internal event EventHandler Opened",
        "~ public int Size { get; private set; } → public int Size { get; internal set; }",
    })]
    [InlineData("Shelf", new[] { "~ public abstract int Height() → public virtual int Height() [body]" })]
    [InlineData("Cabinet", new[] { "~ public override string Label() → public sealed override string Label()" })]
    [InlineData("Receivers", new[]
    {
        "~ public static void Demote(this Methods methods) → public static void Demote(Methods methods)",
        "~ public static void Promote(Methods methods) → public static void Promote(this Methods methods)",
    })]
    [InlineData("Conditions", new[]
    {
        "~ public virtual void Fit<T>(T item) where T : class → public virtual void Fit<T>(T item) where T : struct",
        "~ public void Gain<T>(T item) → public void Gain<T>(T item) where T : class",
        "~ public void Lose<T>(T item) where T : class → public void Lose<T>(T item)",
        "~ public int Mend<T>(T item) → public int Mend<T>(T item) where T : class [body]",
        "~ public void Retype<T>(T item) where T : IComparable → public void Retype<T>(T item) where T : IComparable<T>",
        "~ public void Shape<T>(T item) where T : BaseShift.Declarations.Alpha.Badge → " +
            "public void Shape<T>(T item) where T : BaseShift.Declarations.Beta.Badge",
        "~ public void Swap<T>(T item) where T : class → public void Swap<T>(T item) where T : struct",
        "~ public void Widen<T>(T item) where T : class → public void Widen<T>(T item) where T : class, new()",
    })]
    public async Task Compare_MemberChanges_OfAChangedDeclaration_WriteBothDeclarations(string typeName, string[] lines)
    {
        // Each member keeps its key, so it is one member, and its line writes its declaration in A
        // and in B. A method whose body changed as well says so, and a property names its
        // accessors whose bodies changed; a constant's declarations hold its values. Two
        // declarations that read alike are written in full, as Shape's, whose condition names a
        // class of the same name in another namespace in each build.
        string text = await CallToolText("compare", new()
        {
            ["assembly_a"] = BaseShiftPath("BaseShift.dll"),
            ["assembly_b"] = BaseShiftPath("BaseShift2.dll"),
            ["type_name"] = $"BaseShift.Declarations.{typeName}",
        });

        var header = lines.Length == 1 ? "1 member change" : $"{lines.Length} member changes";
        Assert.Equal(new[] { $"{header} in BaseShift.Declarations.{typeName}:" }.Concat(lines), text.Split('\n'));
    }

    [Theory]
    [InlineData("BaseShift.Declarations.Methods", "Open", "csharp",
        "// public void Open() → private void Open()\n" +
        "// BaseShift.Declarations.Methods.Open() → void — bodies are identical in both assemblies.")]
    [InlineData("BaseShift.Declarations.Methods", "Open", "il",
        "// public void Open() → private void Open()\n" +
        "// BaseShift.Declarations.Methods.Open() → void — bodies are identical in both assemblies.")]
    // A parameter that gains params on a collection, which the compiler marks with an attribute
    // ILSpy does not read as params.
    [InlineData("BaseShift.Declarations.Methods", "Gather", "csharp",
        "// public int Gather(IEnumerable<int> values) → public int Gather(params IEnumerable<int> values)\n" +
        "// BaseShift.Declarations.Methods.Gather(IEnumerable<int>) → int — bodies are identical in both assemblies.")]
    [InlineData("BaseShift.Declarations.Methods", ".ctor", "csharp",
        "// public Methods() → protected Methods()\n" +
        "// BaseShift.Declarations.Methods..ctor() → void — bodies are identical in both assemblies.")]
    [InlineData("BaseShift.Overloads.Replaced", "Pass", "csharp",
        "// public T Pass<T>(T item) → public TItem Pass<TItem>(TItem item)\n" +
        "// BaseShift.Overloads.Replaced.Pass<T>(T) → T — bodies are identical in both assemblies.")]
    [InlineData("BaseShift.Declarations.Accessors", "set_Name", "il",
        "// public string Name { get; set; } → public string Name { get; init; }\n" +
        "// BaseShift.Declarations.Accessors.set_Name(string) → void — bodies are identical in both assemblies.")]
    [InlineData("BaseShift.Declarations.Accessors", "add_Opened", "csharp",
        "// public event EventHandler Opened → internal event EventHandler Opened\n" +
        "// BaseShift.Declarations.Accessors.add_Opened(EventHandler) → void — bodies are identical in both assemblies.")]
    [InlineData("BaseShift.Declarations.Fields", "Max", "csharp",
        "// public const int Max = 5 → internal const int Max = 6\n" +
        "// BaseShift.Declarations.Fields.Max — 5 in A, 6 in B.")]
    [InlineData("BaseShift.Declarations.Receivers", "Promote", "il",
        "// public static void Promote(Methods methods) → public static void Promote(this Methods methods)\n" +
        "// BaseShift.Declarations.Receivers.Promote(Methods) → void — bodies are identical in both assemblies.")]
    // A condition that changed, and one whose type gave way to a class of the same name in another
    // namespace, whose declarations are written in full since they read alike otherwise.
    [InlineData("BaseShift.Declarations.Conditions", "Gain", "csharp",
        "// public void Gain<T>(T item) → public void Gain<T>(T item) where T : class\n" +
        "// BaseShift.Declarations.Conditions.Gain<T>(T) → void — bodies are identical in both assemblies.")]
    [InlineData("BaseShift.Declarations.Conditions", "Shape", "il",
        "// public void Shape<T>(T item) where T : BaseShift.Declarations.Alpha.Badge → " +
        "public void Shape<T>(T item) where T : BaseShift.Declarations.Beta.Badge\n" +
        "// BaseShift.Declarations.Conditions.Shape<T>(T) → void — bodies are identical in both assemblies.")]
    public async Task Compare_Bodies_OfAChangedDeclaration_NamesBothDeclarationsFirst(
        string typeName, string memberName, string format, string expected)
    {
        // Neither the verdict nor a constant's values show a declaration, so the line before them
        // does, as the member list writes it. An accessor is named by its property's or event's
        // line, which also shows an init accessor and an accessor's own accessibility.
        string text = await CallToolText("compare", new()
        {
            ["assembly_a"] = BaseShiftPath("BaseShift.dll"),
            ["assembly_b"] = BaseShiftPath("BaseShift2.dll"),
            ["type_name"] = typeName,
            ["member_name"] = memberName,
            ["format"] = format,
        });

        Assert.Equal(expected, text);
    }

    [Theory]
    [InlineData("csharp", "+\treturn a + 2;")]
    [InlineData("il", "+IL_0001: ldc.i4.2")]
    public async Task Compare_Bodies_OfAChangedDeclarationWhoseBodyChanged_NamesBothDeclarationsBeforeTheDiff(
        string format, string inB)
    {
        // The IL of a body does not show the declaration, so in either format the line before the
        // diff is what says that it changed.
        string text = await CallToolText("compare", new()
        {
            ["assembly_a"] = BaseShiftPath("BaseShift.dll"),
            ["assembly_b"] = BaseShiftPath("BaseShift2.dll"),
            ["type_name"] = "BaseShift.Declarations.Reworked",
            ["member_name"] = "Total",
            ["format"] = format,
        });

        Assert.StartsWith("// public int Total(int a) → internal int Total(int a)\n\n--- A: ", text);
        Assert.Contains(inB, text.Split('\n'));
    }

    [Theory]
    [InlineData("Width", "BaseShift.Declarations.Fields.Width is a field, which has no body, and its declaration " +
        "changed: public int Width → public readonly int Width; BaseShift.Declarations.Fields..ctor() sets it.")]
    [InlineData("Ticks", "BaseShift.Declarations.Fields.Ticks is a field, which has no body, and its declaration " +
        "changed: public static int Ticks → public static volatile int Ticks; no constructor of " +
        "BaseShift.Declarations.Fields sets it, and assigned_by lists what does.")]
    public async Task Compare_Bodies_OfAFieldWhoseDeclarationChanged_NamesBothDeclarations(
        string memberName, string expected)
    {
        string text = await CallToolError("compare", new()
        {
            ["assembly_a"] = BaseShiftPath("BaseShift.dll"),
            ["assembly_b"] = BaseShiftPath("BaseShift2.dll"),
            ["type_name"] = "BaseShift.Declarations.Fields",
            ["member_name"] = memberName,
        });

        Assert.Equal($"Error: ArgumentException: {expected}", text);
    }

    [Theory]
    [InlineData("BaseShift.Declarations.Kinds", "Stride", "BaseShift.Declarations.Kinds.Stride is a field, which has " +
        "no body, and its declaration changed: public static readonly int Stride → public const int Stride = 4; " +
        "in A BaseShift.Declarations.Kinds..cctor() sets it, and in B it is a constant.")]
    [InlineData("BaseShift.Kinds.Till", "Margin", "BaseShift.Kinds.Till.Margin is a field, which has no body, " +
        "and its declaration changed: public const int Margin = 0 → public static readonly int Margin; " +
        "in A it is a constant, and in B no constructor of BaseShift.Kinds.Till sets it.")]
    public async Task Compare_Bodies_OfAFieldThatIsAConstantInOneBuild_SaysSoForThatBuild(
        string typeName, string memberName, string expected)
    {
        // No constructor sets a constant, whose value is in its declaration, and assigned_by
        // refuses one, so the error says which build has the constant instead.
        string text = await CallToolError("compare", new()
        {
            ["assembly_a"] = BaseShiftPath("BaseShift.dll"),
            ["assembly_b"] = BaseShiftPath("BaseShift2.dll"),
            ["type_name"] = typeName,
            ["member_name"] = memberName,
        });

        Assert.Equal($"Error: ArgumentException: {expected}", text);
    }

    [Theory]
    [InlineData("Limit", "// BaseShift.Kinds.Till.Limit is a field in A and a property in B: " +
        "public int Limit → public int Limit { get; set; }\n\n" +
        "// B: BaseShift.Kinds.Till.Limit\npublic int Limit { get; set; }\n")]
    [InlineData("Open", "// BaseShift.Kinds.Till.Open is a method in A and a property in B: " +
        "public void Open() → public bool Open { get; }\n\n" +
        "// A: BaseShift.Kinds.Till.Open() → void\npublic void Open()\n{\n}\n\n" +
        "// B: BaseShift.Kinds.Till.Open\npublic bool Open => true;\n")]
    [InlineData("Size", "// BaseShift.Kinds.Till.Size is a constant in A and a property in B: " +
        "public const int Size = 4 → public int Size { get; }\n\n" +
        "// B: BaseShift.Kinds.Till.Size\npublic int Size => 4;\n")]
    [InlineData("Ready", "// BaseShift.Kinds.Till.Ready is a property in A and a method in B: " +
        "public bool Ready { get; } → public bool Ready()\n\n" +
        "// A: BaseShift.Kinds.Till.Ready\npublic bool Ready => true;\n\n" +
        "// B: BaseShift.Kinds.Till.Ready() → bool\npublic bool Ready()\n{\n\treturn true;\n}\n")]
    [InlineData("Spare", "// BaseShift.Kinds.Till.Spare is a constant in A and a field in B: " +
        "public const int Spare = 3 → public static readonly long Spare")]
    [InlineData("Changed", "// BaseShift.Kinds.Till.Changed is a field in A and an event in B: " +
        "public EventHandler Changed → public event EventHandler Changed\n\n" +
        "// B: BaseShift.Kinds.Till.Changed\nusing System;\n\npublic event EventHandler Changed;\n")]
    [InlineData("Delay", "// BaseShift.Kinds.Till.Delay is a field in A and a property in B: " +
        "public int Delay → public int Delay { get; set; }\n\n" +
        "// B: BaseShift.Kinds.Till.Delay [inherited from BaseShift.Kinds.Store]\npublic int Delay { get; set; }\n")]
    [InlineData("Width", "// BaseShift.Kinds.Till.Width is a field [inherited from BaseShift.Kinds.Store] in A " +
        "and a property in B: public int Width → public int Width { get; set; }\n\n" +
        "// B: BaseShift.Kinds.Till.Width\npublic int Width { get; set; }\n")]
    [InlineData("IsEmpty", "// BaseShift.Kinds.Till.IsEmpty is a property in A and an extension method in B: " +
        "public bool IsEmpty { get; } → public static bool IsEmpty(this Till till)\n\n" +
        "// A: BaseShift.Kinds.Till.IsEmpty\npublic bool IsEmpty => true;\n\n" +
        "// B: BaseShift.Kinds.TillExtensions.IsEmpty(Till) → bool [extension method on BaseShift.Kinds.Till]\n" +
        "public static bool IsEmpty(this Till till)\n{\n\treturn true;\n}\n")]
    public async Task Compare_Bodies_OfANameOfAnotherKindInEachBuild_NamesBothAndShowsTheirCode(
        string memberName, string expected)
    {
        // Neither member is the whole difference, so the answer names both, with their
        // declarations, and shows the code of each that has code under the build it is in.
        string text = await CallToolText("compare", new()
        {
            ["assembly_a"] = BaseShiftPath("BaseShift.dll"),
            ["assembly_b"] = BaseShiftPath("BaseShift2.dll"),
            ["type_name"] = "BaseShift.Kinds.Till",
            ["member_name"] = memberName,
        });

        Assert.Equal(expected, text.Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task Compare_Bodies_OfANameOfAnotherKindInEachBuild_TheOtherWayRound_NamesAFirst()
    {
        string text = await CallToolText("compare", new()
        {
            ["assembly_a"] = BaseShiftPath("BaseShift2.dll"),
            ["assembly_b"] = BaseShiftPath("BaseShift.dll"),
            ["type_name"] = "BaseShift.Kinds.Till",
            ["member_name"] = "Limit",
        });

        Assert.Equal(
            "// BaseShift.Kinds.Till.Limit is a property in A and a field in B: " +
            "public int Limit { get; set; } → public int Limit\n\n" +
            "// A: BaseShift.Kinds.Till.Limit\npublic int Limit { get; set; }\n",
            text.Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task Compare_Bodies_OfANameOfAnotherKindInEachBuild_WithIlFormat_ShowsTheIlOfEach()
    {
        string text = await CallToolText("compare", new()
        {
            ["assembly_a"] = BaseShiftPath("BaseShift.dll"),
            ["assembly_b"] = BaseShiftPath("BaseShift2.dll"),
            ["type_name"] = "BaseShift.Kinds.Till",
            ["member_name"] = "Open",
            ["format"] = "il",
        });
        text = text.Replace("\r\n", "\n");

        Assert.StartsWith(
            "// BaseShift.Kinds.Till.Open is a method in A and a property in B: " +
            "public void Open() → public bool Open { get; }\n\n// A: BaseShift.Kinds.Till.Open() → void\n", text);
        Assert.Contains("\n\n// B: BaseShift.Kinds.Till.Open\n// BaseShift.Kinds.Till.get_Open() → bool\n", text);
        Assert.Contains("IL_0000: ldc.i4.1", text);
    }

    [Fact]
    public async Task Compare_Bodies_OfANameOfAnotherKindInEachBuild_WithAHint_AnswersForTheMethod()
    {
        // A hint asks for a method, which only A has under the name.
        string text = await CallToolText("compare", new()
        {
            ["assembly_a"] = BaseShiftPath("BaseShift.dll"),
            ["assembly_b"] = BaseShiftPath("BaseShift2.dll"),
            ["type_name"] = "BaseShift.Kinds.Till",
            ["member_name"] = "Open",
            ["parameter_count"] = 0,
        });

        Assert.StartsWith("// BaseShift.Kinds.Till.Open() → void — only A has it.\n\n", text.Replace("\r\n", "\n"));
    }

    [Theory]
    [InlineData("BaseShift.dll", "BaseShift2.dll", "Depth", "BaseShift.Declarations.Accessors.Depth is a property, " +
        "and its declaration changed: public int Depth { get; } → public int Depth { get; set; }; " +
        "compare its accessors, get_Depth, and set_Depth, which only B has.")]
    [InlineData("BaseShift2.dll", "BaseShift.dll", "Depth", "BaseShift.Declarations.Accessors.Depth is a property, " +
        "and its declaration changed: public int Depth { get; set; } → public int Depth { get; }; " +
        "compare its accessors, get_Depth, and set_Depth, which only A has.")]
    [InlineData("BaseShift.dll", "BaseShift2.dll", "Size", "BaseShift.Declarations.Accessors.Size is a property, " +
        "and its declaration changed: public int Size { get; private set; } → public int Size { get; internal set; }; " +
        "compare its accessors, get_Size, set_Size.")]
    [InlineData("BaseShift.dll", "BaseShift2.dll", "Opened", "BaseShift.Declarations.Accessors.Opened is an event, " +
        "and its declaration changed: public event EventHandler Opened → internal event EventHandler Opened; " +
        "compare its accessors, add_Opened, remove_Opened.")]
    public async Task Compare_Bodies_OfAPropertyOrEventWhoseDeclarationChanged_NamesBothAndTheAccessorsOfEach(
        string assemblyA, string assemblyB, string memberName, string expected)
    {
        // A property or event is compared through its accessors, so the error names those of both
        // builds, each that only one has marked, and both declarations, which show what changed.
        string text = await CallToolError("compare", new()
        {
            ["assembly_a"] = BaseShiftPath(assemblyA),
            ["assembly_b"] = BaseShiftPath(assemblyB),
            ["type_name"] = "BaseShift.Declarations.Accessors",
            ["member_name"] = memberName,
        });

        Assert.Equal($"Error: ArgumentException: {expected}", text);
    }

    [Theory]
    [InlineData("BaseShift.dll", "BaseShift2.dll", "Limit", "BaseShift.Retyped.Till.Limit is a field, which has no " +
        "body, and A and B have different fields of that name: public int Limit → public long Limit; " +
        "BaseShift.Retyped.Till..ctor() sets it.")]
    [InlineData("BaseShift.dll", "BaseShift2.dll", "Count", "BaseShift.Retyped.Till.Count is a field, which has no " +
        "body, and A and B have different fields of that name: public static int Count → public static long Count; " +
        "in A no constructor of BaseShift.Retyped.Till sets it, and in B BaseShift.Retyped.Till..cctor() sets it.")]
    [InlineData("BaseShift.dll", "BaseShift2.dll", "Tag", "BaseShift.Retyped.Till.Tag is a field, which has no " +
        "body, and A and B have different fields of that name: public BaseShift.Retyped.One.Tag Tag → " +
        "public BaseShift.Retyped.Two.Tag Tag; no constructor of BaseShift.Retyped.Till sets it, and assigned_by " +
        "lists what does.")]
    [InlineData("BaseShift.dll", "BaseShift2.dll", "Depth", "BaseShift.Retyped.Till.Depth is a property, and A and B " +
        "have different properties of that name: public int Depth { get; } → public long Depth { get; set; }; " +
        "compare its accessors, get_Depth, and set_Depth, which only B has.")]
    [InlineData("BaseShift.dll", "BaseShift2.dll", "Item", "BaseShift.Retyped.Till.Item is a property, and A and B " +
        "have different properties of that name: public int Item[int index] { get; } → " +
        "public long Item[long index] { get; }; compare its accessors, get_Item.")]
    [InlineData("BaseShift.dll", "BaseShift2.dll", "Opened", "BaseShift.Retyped.Till.Opened is an event, and A and B " +
        "have different events of that name: public event EventHandler Opened → public event Action Opened; " +
        "compare its accessors, add_Opened, remove_Opened.")]
    [InlineData("BaseShift.dll", "BaseShift2.dll", "Delay", "BaseShift.Retyped.Till.Delay is a field, which has no " +
        "body, and A and B have different fields of that name: public int Delay → public int Delay " +
        "[inherited from BaseShift.Retyped.Store]; in A BaseShift.Retyped.Till..ctor() sets it, and in B " +
        "BaseShift.Retyped.Store..ctor() sets it.")]
    [InlineData("BaseShift2.dll", "BaseShift.dll", "Delay", "BaseShift.Retyped.Till.Delay is a field, which has no " +
        "body, and A and B have different fields of that name: public int Delay [inherited from " +
        "BaseShift.Retyped.Store] → public int Delay; in A BaseShift.Retyped.Store..ctor() sets it, and in B " +
        "BaseShift.Retyped.Till..ctor() sets it.")]
    [InlineData("BaseShift.dll", "BaseShift2.dll", "Label", "BaseShift.Retyped.Till.Label is a property, and A and B " +
        "have different properties of that name: public virtual string Label { get; } [inherited from " +
        "BaseShift.Retyped.Store] → public override string Label { get; }; compare its accessors, get_Label.")]
    [InlineData("BaseShift2.dll", "BaseShift.dll", "Label", "BaseShift.Retyped.Till.Label is a property, and A and B " +
        "have different properties of that name: public override string Label { get; } → public virtual string " +
        "Label { get; } [inherited from BaseShift.Retyped.Store]; compare its accessors, get_Label.")]
    public async Task Compare_Bodies_OfANameThatIsAnotherMemberOfItsKindInEachBuild_NamesBothDeclarations(
        string assemblyA, string assemblyB, string memberName, string expected)
    {
        // A changed type or a move to another class makes two members, which the member list
        // counts as a removal and an addition, so the error says so and names both declarations,
        // each with where its build found it when that is not the type asked about. The types of
        // Tag's two declarations have one short name in two namespaces, so both are written in
        // full; Delay's read alike in full as well and stay short.
        string text = await CallToolError("compare", new()
        {
            ["assembly_a"] = BaseShiftPath(assemblyA),
            ["assembly_b"] = BaseShiftPath(assemblyB),
            ["type_name"] = "BaseShift.Retyped.Till",
            ["member_name"] = memberName,
        });

        Assert.Equal($"Error: ArgumentException: {expected}", text);
    }

    [Fact]
    public async Task Compare_MemberChanges_WhenMembersShareTheirWholeSignature_PairsThemInOrder()
    {
        // The second Run and the getter of Rate[string] return another value in b. Rate's
        // overloads differ in their parameter, which is part of the key though Rate is no
        // indexer; the two Runs and the two Marks share theirs and pair in the order they stand.
        var a = SameKeyAssembly.Write(second: 1);
        var b = SameKeyAssembly.Write(second: 2);
        try
        {
            string types = await CallToolText("compare", new()
            {
                ["assembly_a"] = a,
                ["assembly_b"] = b,
            });
            Assert.Equal(new[] { "1 changed type:", "~ SameKey.Holder [bodies]" }, types.Split('\n'));

            string members = await CallToolText("compare", new()
            {
                ["assembly_a"] = a,
                ["assembly_b"] = b,
                ["type_name"] = "SameKey.Holder",
            });
            Assert.Equal(
                new[]
                {
                    "2 member changes in SameKey.Holder:",
                    "~ public int Rate[string key] { get; } [get_Rate]",
                    "~ public int Run()",
                },
                members.Split('\n'));
        }
        finally
        {
            SameKeyAssembly.TryDelete(a, b);
        }
    }

    [Fact]
    public async Task Compare_ChangedTypes_WhenMembersShareTheirWholeSignature_AndNothingChanged_ReportsNoChange()
    {
        var a = SameKeyAssembly.Write(second: 1);
        var b = SameKeyAssembly.Write(second: 1);
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
            SameKeyAssembly.TryDelete(a, b);
        }
    }

    [Fact]
    public async Task Compare_MemberChanges_WhenAClassWhoseNameDoesNotCountItsTypeParametersTakesOtherArguments_ListsRemovalsAndAdditions()
    {
        // The F# compiler names the classes it generates without the count of their type
        // parameters, which ILSpy reads from the name. Every member of Shelf but Spare holds,
        // takes or returns a Box or a Notify with int in a and string in b, so each is another
        // member in b; Crate's Instance holds the class itself, with its type parameter in a and
        // without one in b, and Crate itself loses its type parameter, which counts as a change of
        // its metadata. Tray's name counts its type parameter in a alone, which makes two
        // classes of it and another member of Cart's Load.
        var a = UncountedGenericAssembly.Write(second: false);
        var b = UncountedGenericAssembly.Write(second: true);
        try
        {
            string types = await CallToolText("compare", new()
            {
                ["assembly_a"] = a,
                ["assembly_b"] = b,
            });
            Assert.Equal(
                new[]
                {
                    "5 changed types:",
                    "~ Uncounted.Cart [members]",
                    "~ Uncounted.Crate [metadata,members]",
                    "~ Uncounted.Shelf [members]",
                    "+ Uncounted.Tray",
                    "- Uncounted.Tray<>",
                },
                types.Split('\n'));

            string shelf = await CallToolText("compare", new()
            {
                ["assembly_a"] = a,
                ["assembly_b"] = b,
                ["type_name"] = "Uncounted.Shelf",
            });
            Assert.Equal(
                new[]
                {
                    "12 member changes in Uncounted.Shelf:",
                    "- public Shelf(Box<int> box)",
                    "+ public Shelf(Box<string> box)",
                    "- public event Notify<int> Changed",
                    "+ public event Notify<string> Changed",
                    "- public Box<int> Front { get; }",
                    "+ public Box<string> Front { get; }",
                    "- public Box<int> Give()",
                    "+ public Box<string> Give()",
                    "- public Box<int> Slot",
                    "+ public Box<string> Slot",
                    "- public void Take(Box<int> box)",
                    "+ public void Take(Box<string> box)",
                },
                shelf.Split('\n'));

            string crate = await CallToolText("compare", new()
            {
                ["assembly_a"] = a,
                ["assembly_b"] = b,
                ["type_name"] = "Uncounted.Crate",
            });
            Assert.Equal(
                new[]
                {
                    "2 member changes in Uncounted.Crate:",
                    "+ public static Crate Instance",
                    "- public static Crate<T> Instance",
                    "Declaration: public class Uncounted.Crate<T> → public class Uncounted.Crate",
                },
                crate.Split('\n'));
        }
        finally
        {
            UncountedGenericAssembly.TryDelete(a, b);
        }
    }

    [Fact]
    public async Task Compare_MemberChanges_WhenAClassNameStopsCountingItsTypeParameters_ListsAFieldOfItAsAnotherMember()
    {
        // The runtime knows a class by its name, so Tray`1 in a and Tray in b are two classes, and
        // Cart's Load, a Tray<int> in both, is another field in b, though the short form writes
        // both alike. Written in full, b's line leaves out the argument its class's name does not
        // count, as every name written through the full form does.
        var a = UncountedGenericAssembly.Write(second: false);
        var b = UncountedGenericAssembly.Write(second: true);
        try
        {
            string text = await CallToolText("compare", new()
            {
                ["assembly_a"] = a,
                ["assembly_b"] = b,
                ["type_name"] = "Uncounted.Cart",
            });
            Assert.Equal(
                new[]
                {
                    "2 member changes in Uncounted.Cart:",
                    "+ public Uncounted.Tray Load",
                    "- public Uncounted.Tray<int> Load",
                },
                text.Split('\n'));
        }
        finally
        {
            UncountedGenericAssembly.TryDelete(a, b);
        }
    }

    [Theory]
    [InlineData("Uncounted.Shelf", "Slot", "Uncounted.Shelf.Slot is a field, which has no body, and A and B have " +
        "different fields of that name: public Box<int> Slot → public Box<string> Slot; no constructor of " +
        "Uncounted.Shelf sets it, and assigned_by lists what does.")]
    [InlineData("Uncounted.Crate", "Instance", "Uncounted.Crate.Instance is a field, which has no body, and A and B " +
        "have different fields of that name: public static Crate<T> Instance → public static Crate Instance; no " +
        "constructor of Uncounted.Crate sets it, and assigned_by lists what does.")]
    public async Task Compare_Bodies_OfAFieldOfAClassWhoseNameDoesNotCountItsTypeParameters_NamesBothFields(
        string typeName, string memberName, string expected)
    {
        // The field's type takes other arguments in b, or loses the type parameter it had in a,
        // which makes it another field, as the member list counts it.
        var a = UncountedGenericAssembly.Write(second: false);
        var b = UncountedGenericAssembly.Write(second: true);
        try
        {
            string text = await CallToolError("compare", new()
            {
                ["assembly_a"] = a,
                ["assembly_b"] = b,
                ["type_name"] = typeName,
                ["member_name"] = memberName,
            });
            Assert.Equal($"Error: ArgumentException: {expected}", text);
        }
        finally
        {
            UncountedGenericAssembly.TryDelete(a, b);
        }
    }

    [Fact]
    public async Task Compare_Bodies_OfAMethodThatTakesAClassWhoseNameDoesNotCountItsTypeParameters_SaysTheyAreDifferentMethods()
    {
        var a = UncountedGenericAssembly.Write(second: false);
        var b = UncountedGenericAssembly.Write(second: true);
        try
        {
            string text = await CallToolText("compare", new()
            {
                ["assembly_a"] = a,
                ["assembly_b"] = b,
                ["type_name"] = "Uncounted.Shelf",
                ["member_name"] = "Take",
            });
            Assert.Equal(
                "// Uncounted.Shelf.Take(Box<int>) → void in A and Uncounted.Shelf.Take(Box<string>) → void in B " +
                "are different methods — bodies are identical in both assemblies.",
                text);
        }
        finally
        {
            UncountedGenericAssembly.TryDelete(a, b);
        }
    }

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
    public async Task Compare_Bodies_OfAMethodWhoseIteratorChanged_ShowsTheChangedLines()
    {
        string text = await CallToolText("compare", new()
        {
            ["assembly_a"] = BaseShiftPath("BaseShift.dll"),
            ["assembly_b"] = BaseShiftPath("BaseShift2.dll"),
            ["type_name"] = "BaseShift.Generated.ChangedInside",
            ["member_name"] = "InIterator",
        });

        var lines = text.Split('\n');
        Assert.StartsWith("--- A: ", lines[0]);
        Assert.Contains(lines, line => line.StartsWith('-') && line.EndsWith("yield return 2;"));
        Assert.Contains(lines, line => line.StartsWith('+') && line.EndsWith("yield return 3;"));
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
        // method's own, with its number replaced as the comparison reads it. The change lies inside
        // the local function, past the line that names it, so the hunk names it after its @@.
        string text = await CallToolText("compare", new()
        {
            ["assembly_a"] = BaseShiftPath("BaseShift.dll"),
            ["assembly_b"] = BaseShiftPath("BaseShift2.dll"),
            ["type_name"] = "BaseShift.Generated.ChangedInside",
            ["member_name"] = "InLocalFunction",
            ["format"] = "il",
        });

        var lines = text.Split('\n');
        Assert.StartsWith("+++ B: ", lines[1]);
        Assert.Matches(
            @"^@@ -\d+,\d+ \+\d+,\d+ @@ // Generated: BaseShift\.Generated\.ChangedInside\.<InLocalFunction>g__Scale\|#1_0\(int\)$",
            lines[2]);
        Assert.Contains("-IL_0001: ldc.i4.2", lines);
        Assert.Contains("+IL_0001: ldc.i4.3", lines);
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
        Assert.DoesNotContain("--- A: ", text);
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

    [Theory]
    [InlineData("Panel", "Title", "// BaseShift.Sides.Panel.Title — only B has it.\n\n" +
        "public string Title { get; set; }\n")]
    [InlineData("Panel", "Resized", "// BaseShift.Sides.Panel.Resized — only B has it.\n\n" +
        "using System;\n\npublic event EventHandler Resized;\n")]
    [InlineData("Panel", "Item", "// BaseShift.Sides.Panel.Item[int] — only B has it.\n\n" +
        "public int this[int column] => column * 2;\n")]
    [InlineData("Panel", "Caption", "// BaseShift.Sides.Panel.Caption — only A has it.\n\n" +
        "public string Caption { get; set; }\n")]
    [InlineData("Panel", "Closed", "// BaseShift.Sides.Panel.Closed — only A has it.\n\n" +
        "using System;\n\npublic event EventHandler Closed;\n")]
    [InlineData("Panel", "Border", "// BaseShift.Sides.Panel.Border [inherited from BaseShift.Sides.Frame] — " +
        "only B has it.\n\npublic int Border { get; set; }\n")]
    [InlineData("Banner", "Text", "// BaseShift.Sides.Banner.Text — only B has it.\n\n" +
        "public string Text => \"new\";\n")]
    public async Task Compare_Bodies_OfAPropertyOrEventOnlyOneAssemblyHas_ShowsItsCodeAndWhichHasIt(
        string typeName, string memberName, string expected)
    {
        // A property or event only one build has is the whole difference, so its code is shown, as
        // a method's is. Panel inherits Border from Frame, and only B has Banner.
        string text = await CallToolText("compare", new()
        {
            ["assembly_a"] = BaseShiftPath("BaseShift.dll"),
            ["assembly_b"] = BaseShiftPath("BaseShift2.dll"),
            ["type_name"] = $"BaseShift.Sides.{typeName}",
            ["member_name"] = memberName,
        });

        Assert.Equal(expected, text.Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task Compare_Bodies_OfAPropertyOnlyOneAssemblyHas_WithIlFormat_ShowsTheIlOfEachAccessor()
    {
        string text = await CallToolText("compare", new()
        {
            ["assembly_a"] = BaseShiftPath("BaseShift.dll"),
            ["assembly_b"] = BaseShiftPath("BaseShift2.dll"),
            ["type_name"] = "BaseShift.Sides.Panel",
            ["member_name"] = "Title",
            ["format"] = "il",
        });
        text = text.Replace("\r\n", "\n");

        Assert.StartsWith(
            "// BaseShift.Sides.Panel.Title — only B has it.\n\n// BaseShift.Sides.Panel.get_Title() → string\n", text);
        Assert.Contains(
            "IL_0001: ldfld string BaseShift.Sides.Panel::'<Title>k__BackingField'\nIL_0006: ret\n\n" +
            "// BaseShift.Sides.Panel.set_Title(string) → void\n", text);
        Assert.Contains("stfld string BaseShift.Sides.Panel::'<Title>k__BackingField'", text);
    }

    [Theory]
    [InlineData("Level", "BaseShift.Gauge.Level is a property; compare its accessors, get_Level, set_Level.")]
    [InlineData("Changed", "BaseShift.Gauge.Changed is an event; compare its accessors, add_Changed, remove_Changed.")]
    public async Task Compare_Bodies_OfAPropertyOrEventBothAssembliesHave_PointsToItsAccessors(
        string memberName, string expected)
    {
        // Both builds have Gauge's Level and Changed, so each is compared through its accessors.
        string text = await CallToolError("compare", new()
        {
            ["assembly_a"] = BaseShiftPath("BaseShift.dll"),
            ["assembly_b"] = BaseShiftPath("BaseShift2.dll"),
            ["type_name"] = "BaseShift.Gauge",
            ["member_name"] = memberName,
        });

        Assert.Equal($"Error: ArgumentException: {expected}", text);
    }

    [Fact]
    public async Task Compare_Bodies_OfANameNeitherAssemblyHas_SaysItIsNotFound()
    {
        // Neither build has a member of the name, so A's lookup answers, as decompile's would.
        string text = await CallToolError("compare", new()
        {
            ["assembly_a"] = BaseShiftPath("BaseShift.dll"),
            ["assembly_b"] = BaseShiftPath("BaseShift2.dll"),
            ["type_name"] = "BaseShift.Sides.Panel",
            ["member_name"] = "Missing",
        });

        Assert.Equal(
            "Error: ArgumentException: Member 'Missing' not found on BaseShift.Sides.Panel, its base types, " +
            "or as an extension method.", text);
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
        Assert.Contains("--- A: ", text);
        Assert.Contains("ldfld int32 BaseShift.Gauge::_value", text);
        Assert.Contains("ldc.i4.1", text);
    }

    private Task<string> CompareSettle(Dictionary<string, object?>? more = null)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["assembly_a"] = BaseShiftPath("BaseShift.dll"),
            ["assembly_b"] = BaseShiftPath("BaseShift2.dll"),
            ["type_name"] = "BaseShift.Diffs.Ledger",
            ["member_name"] = "Settle",
        };
        if (more is not null)
            foreach (var (name, value) in more)
                arguments[name] = value;
        return CallToolText("compare", arguments);
    }

    // The lines a diff marks, without its two lines that name the assemblies.
    private static List<string> MarkedLines(string diff) =>
        diff.Split('\n').Skip(2).Where(line => line.StartsWith('-') || line.StartsWith('+')).ToList();

    [Fact]
    public async Task Compare_Bodies_OfALongMethodWithSmallChanges_ShowsOnlyTheChangedLinesWithTheirContext()
    {
        // Settle gains a condition in its middle and multiplies by another number in its last
        // statement. The diff shows each change with three unchanged lines on either side, in two
        // hunks, and nothing else of the method.
        string text = await CompareSettle();

        var lines = text.Split('\n');
        Assert.StartsWith("--- A: ", lines[0]);
        Assert.EndsWith("BaseShift.dll", lines[0]);
        Assert.StartsWith("+++ B: ", lines[1]);
        Assert.EndsWith("BaseShift2.dll", lines[1]);
        Assert.Equal(
            new[]
            {
                "@@ -24,6 +24,10 @@",
                " \t_count += amounts.Length;",
                " \tint num6 = num2 - num3;",
                " \tint num7 = ((amounts.Length != 0) ? (num / amounts.Length) : 0);",
                "+\tif (strict && num4 > 0)",
                "+\t{",
                "+\t\treturn -num4;",
                "+\t}",
                " \tint num8 = num6 * 2 + num7;",
                " \tif (num8 > 1000)",
                " \t{",
                "@@ -44,5 +48,5 @@",
                " \t\tnum11 += 10;",
                " \t}",
                " \t_total -= num12;",
                "-\treturn num11 + _count * 2;",
                "+\treturn num11 + _count * 3;",
                " }",
            },
            lines.Skip(2));
    }

    [Fact]
    public async Task Compare_Bodies_WithNoContext_ShowsTheChangedLinesAlone()
    {
        // A range of no lines is written by the line before it, and a range of one line by its
        // number alone, as a unified diff writes them.
        string text = await CompareSettle(new() { ["context"] = 0 });

        Assert.Equal(
            new[]
            {
                "@@ -26,0 +27,4 @@",
                "+\tif (strict && num4 > 0)",
                "+\t{",
                "+\t\treturn -num4;",
                "+\t}",
                "@@ -47 +51 @@",
                "-\treturn num11 + _count * 2;",
                "+\treturn num11 + _count * 3;",
            },
            text.Split('\n').Skip(2));
    }

    [Fact]
    public async Task Compare_Bodies_WithANegativeContext_AnswersAsWithNone()
    {
        // As a negative limit lists as much as a limit of 0, a negative context shows as much as 0.
        Assert.Equal(await CompareSettle(new() { ["context"] = 0 }), await CompareSettle(new() { ["context"] = -2 }));
    }

    [Fact]
    public async Task Compare_Bodies_WithAContextLongerThanTheMethod_GivesBackBothBodiesAsDecompileShowsThem()
    {
        // One hunk holds the whole method: its unchanged and removed lines are A's body, its
        // unchanged and added lines B's, each as decompile prints the method in that assembly.
        string text = await CompareSettle(new() { ["context"] = 1000 });

        var lines = text.Split('\n');
        Assert.Equal("@@ -1,48 +1,52 @@", lines[2]);
        var body = lines.Skip(3).ToList();
        string a = string.Join("\n", body.Where(line => line[0] is ' ' or '-').Select(line => line[1..]));
        string b = string.Join("\n", body.Where(line => line[0] is ' ' or '+').Select(line => line[1..]));
        Assert.Equal(await DecompiledSettle("BaseShift.dll"), a);
        Assert.Equal(await DecompiledSettle("BaseShift2.dll"), b);

        // The diff with the default context is much shorter than the two bodies were in full.
        string diff = await CompareSettle();
        Assert.True(diff.Length < (a.Length + b.Length) / 3,
            $"The diff has {diff.Length} characters, the two bodies {a.Length + b.Length}.");
    }

    // Settle's code as decompile prints it, without its header line and with \n line ends.
    private async Task<string> DecompiledSettle(string fileName)
    {
        string text = await CallToolText("decompile", new()
        {
            ["assembly"] = BaseShiftPath(fileName),
            ["type_name"] = "BaseShift.Diffs.Ledger",
            ["member_name"] = "Settle",
        });
        return string.Join("\n", text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').Skip(1));
    }

    [Fact]
    public async Task Compare_Bodies_WithIlFormat_DoesNotMarkAnInstructionThatOnlyMoved()
    {
        // The condition Settle gains moves every later instruction's offset and the target of every
        // jump over it, and a diff of the IL as printed would mark nearly every line after it. The
        // diff compares the IL without offsets and marks the inserted instructions and those that
        // really changed, among them the local variables Debug code numbers after the new one.
        string text = await CompareSettle(new() { ["format"] = "il" });

        var marked = MarkedLines(text);
        Assert.Contains(marked, line => line.StartsWith('+') && line.EndsWith(": neg"));
        Assert.Contains(marked, line => line.StartsWith('-') && line.EndsWith(": ldc.i4.2"));
        Assert.Contains(marked, line => line.StartsWith('+') && line.EndsWith(": ldc.i4.3"));
        Assert.Matches(@"^ IL_[0-9a-f]{4}: ret$", text.Split('\n')[^1]);
        Assert.True(marked.Count < 50, $"The diff marks {marked.Count} lines:\n{text}");
    }

    [Fact]
    public async Task Compare_Bodies_WithIlFormat_MarksAJumpThatLandsElsewhere()
    {
        // Route's third jump lands on the other label in BaseShift2, while every instruction keeps
        // its place and size. Without its offsets the jump reads the same, so the diff tells it
        // apart by the line each target leads to.
        string text = await CallToolText("compare", new()
        {
            ["assembly_a"] = BaseShiftPath("BaseShift.dll"),
            ["assembly_b"] = BaseShiftPath("BaseShift2.dll"),
            ["type_name"] = "BaseShift.Diffs.Ledger",
            ["member_name"] = "Route",
            ["format"] = "il",
        });

        var marked = MarkedLines(text);
        Assert.Equal(2, marked.Count);
        Assert.Matches(@"^-IL_[0-9a-f]{4}: br\.s IL_[0-9a-f]{4}$", marked[0]);
        Assert.Matches(@"^\+IL_[0-9a-f]{4}: br\.s IL_[0-9a-f]{4}$", marked[1]);
        Assert.Equal(marked[0][1..9], marked[1][1..9]);
        Assert.NotEqual(marked[0], "-" + marked[1][1..]);
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
        Assert.Equal(
            "Error: ArgumentException: Invalid value \"json\" for argument 'format'. Valid values: csharp, il.",
            text);
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
    [InlineData("context", "5", "ICSharpCode.Decompiler.DecompilerSettings", "context applies only with member_name.")]
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

    private async Task<string> CallToolError(string toolName, Dictionary<string, object?> arguments)
    {
        CallToolResult result = await _server.Client.CallToolAsync(toolName, arguments);
        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.True(result.IsError == true, $"Expected a rejection from '{toolName}', got: {text}");
        return text;
    }
}
