using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for decompile of a nested type, which answers the nested type's own
/// declaration under a comment that names it, not the outermost type around it. The SearchText
/// fixture holds nested classes of its own, <c>Outer.Inner</c>, <c>Outer.Hidden</c> and
/// <c>Documented.Part</c>, and the classes the compiler makes for the lambdas and the iterator of
/// <c>Shapes</c>, which the decompiler writes into the methods of <c>Shapes</c>; BaseShift2 holds
/// <c>Arity.Reader&lt;T&gt;.Cursor</c>, a class nested in a generic class, with <c>Mark</c> nested in it.
/// </summary>
public sealed class NestedTypeTests : IClassFixture<ILensServerFixture>
{
    private readonly ILensServerFixture _server;

    public NestedTypeTests(ILensServerFixture server)
    {
        _server = server;
    }

    // A nested class, the one marked as generated included, comes alone, without the outermost
    // class that ILSpy's DecompileType decompiles for a nested type's name; so does one in a
    // generic class and one nested two deep, which BaseShift2 holds.
    [Theory]
    [InlineData("SearchText.dll", "SearchText.Outer.Inner", "public class Inner", "Marker.Hit(\"inner\");", "public class Outer")]
    [InlineData("SearchText.dll", "SearchText.Outer.Hidden", "private class Hidden", "Marker.Hit(\"hidden\");", "public class Outer")]
    [InlineData("SearchText.dll", "SearchText.Documented.Part", "public class Part : IComparable", "return 0;", "public class Documented")]
    [InlineData("BaseShift2.dll", "BaseShift.Arity.Reader<>.Cursor", "public class Cursor", "public class Mark", "public class Reader")]
    [InlineData("BaseShift2.dll", "BaseShift.Arity.Reader<>.Cursor.Mark", "public class Mark", "public int Line;", "public class Cursor")]
    public async Task ANestedType_IsDecompiledOnItsOwn(
        string assembly, string typeName, string declaration, string body, string outer)
    {
        string code = await Decompile(typeName, assembly);

        Assert.StartsWith($"// {typeName}\n", code);
        Assert.Contains(declaration, code);
        Assert.Contains(body, code);
        Assert.DoesNotContain(outer, code);
        Assert.DoesNotContain("namespace ", code);
    }

    // The classes the compiler makes for a lambda that captures a variable, for the lambdas that
    // capture nothing and for an iterator come as the compiler generated them, with the methods
    // that hold the code the decompiler writes into the methods of Shapes.
    [Theory]
    [InlineData("SearchText.Shapes.<>c__DisplayClass1_0", "private sealed class <>c__DisplayClass1_0",
        "internal void <InCapturingLambda>b__0()", "Marker.Hit(where + \" captured\");")]
    [InlineData("SearchText.Shapes.<>c", "private sealed class <>c",
        "internal void <InLambda>b__0_0()", "Marker.Hit(\"lambda\");")]
    [InlineData("SearchText.Shapes.<InIterator>d__3", "private sealed class <InIterator>d__3 : IEnumerable<int>",
        "private bool MoveNext()", "Marker.Hit(\"iterator\");")]
    public async Task ATypeTheDecompilerFoldsIntoAMethod_IsDecompiledAsTheCompilerGeneratedIt(
        string typeName, string declaration, string method, string body)
    {
        string code = await Decompile(typeName);

        Assert.StartsWith($"// {typeName}\n", code);
        Assert.Contains(declaration, code);
        Assert.Contains(method, code);
        Assert.Contains(body, code);
        Assert.DoesNotContain("public class Shapes", code);
    }

    // The decompiler writes no declaration of these top-level classes in the decompiled assembly:
    // the helper class that holds a constant array's data, an anonymous type and a <Module>
    // without members. Each comes as the compiler generated it, alone under a comment that names
    // it, rather than as an empty text.
    [Theory]
    [InlineData("<PrivateImplementationDetails>", "internal sealed class <PrivateImplementationDetails>",
        "internal struct __StaticArrayInitTypeSize=12")]
    [InlineData("<>f__AnonymousType0<,>", "internal sealed class <>f__AnonymousType0<<Left>j__TPar, <Right>j__TPar>",
        "public <Left>j__TPar Left => <Left>i__Field;")]
    [InlineData("<Module>", "internal class <Module>", "{")]
    public async Task AHiddenTopLevelType_IsDecompiledAsTheCompilerGeneratedIt(
        string typeName, string declaration, string body)
    {
        string code = await Decompile(typeName);

        Assert.StartsWith($"// {typeName}\n", code);
        Assert.Contains(declaration, code);
        Assert.Contains(body, code);
        Assert.DoesNotContain("namespace ", code);
        Assert.DoesNotContain("public class Generated", code);
    }

    // A top-level delegate is a declaration the decompiler writes, though no class or struct, so
    // it keeps its namespace and comes without a comment.
    [Fact]
    public async Task ATopLevelDelegate_ComesWithItsNamespace()
    {
        string code = await Decompile("BaseShift.Variance.Handler<>", "BaseShift.dll");

        Assert.StartsWith("namespace BaseShift.Variance;", code);
        Assert.Contains("delegate ", code);
    }

    // The outermost class still holds its nested classes, with its namespace and no comment.
    [Fact]
    public async Task AnOutermostType_HoldsItsNestedTypes()
    {
        string code = await Decompile("SearchText.Outer");

        Assert.False(code.StartsWith("//", StringComparison.Ordinal), code);
        Assert.Contains("namespace SearchText;", code);
        Assert.Contains("public class Outer", code);
        Assert.Contains("public class Inner", code);
        Assert.Contains("private class Hidden", code);
    }

    private async Task<string> Decompile(string typeName, string assembly = "SearchText.dll")
    {
        CallToolResult result = await _server.Client.CallToolAsync("decompile",
            new Dictionary<string, object?> { ["assembly"] = FixturePath(assembly), ["type_name"] = typeName });
        string text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.False(result.IsError == true, $"decompile returned an error: {text}");
        return text;
    }

    /// <summary>
    /// The path of a fixture assembly, which Tests.csproj's ProjectReference puts beside the
    /// shared inspection target.
    /// </summary>
    private string FixturePath(string fileName)
    {
        string path = Path.Combine(Path.GetDirectoryName(_server.AssemblyPath)!, fileName);
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Fixture not found: {path}. Its project under Tests/Fixtures/ is expected to land here via " +
                "Tests.csproj's ProjectReference.");
        return path;
    }
}
