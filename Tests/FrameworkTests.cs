using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for the framework the reference search takes for an assembly. Windows' own
/// .NET Framework folder stands for the folder of an old framework, such as a Unity game's
/// library folder: it holds a System.Runtime.dll of version 4.0.0.0 that lacks AssemblyName and
/// BindingFlags, and a netstandard.dll that leads to its mscorlib. ModernUser, built for .NET 10,
/// is read against the installed .NET with such a folder among the allow-roots, and against a
/// folder that holds a .NET runtime of its own where one is; CallShapes, built for .NET
/// Standard, is still read against the old framework. Where a class comes from is read from the
/// error for a member the class declares and an enum of the assembly inherits, which names the
/// file the class was read from.
/// </summary>
public sealed class FrameworkTests : IClassFixture<FrameworkTests.OldFrameworkFixture>
{
    /// <summary>The .NET Framework's own folder, which every Windows ILens runs on holds.</summary>
    private static readonly string OldFramework = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Microsoft.NET", "Framework64", "v4.0.30319");

    private static readonly string ModernUser = Path.Combine(AppContext.BaseDirectory, "ModernUser.dll");
    private static readonly string CallShapes = Path.Combine(AppContext.BaseDirectory, "CallShapes.dll");

    /// <summary>The folder of the .NET runtime the tests run on.</summary>
    private static readonly string Runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)!;

    private readonly OldFrameworkFixture _old;

    public FrameworkTests(OldFrameworkFixture old) => _old = old;

    /// <summary>
    /// One server whose allow-roots are the test output, its References folder, which holds
    /// ExternalLib, and the .NET Framework's folder.
    /// </summary>
    public sealed class OldFrameworkFixture : IAsyncLifetime
    {
        public McpClient Client { get; private set; } = null!;

        public async Task InitializeAsync()
        {
            if (!File.Exists(Path.Combine(OldFramework, "System.Runtime.dll")))
                throw new InvalidOperationException($"No System.Runtime.dll in {OldFramework}, the old framework these tests read against.");
            Client = await StartServer("ILens-old-framework", OldFramework);
        }

        public async Task DisposeAsync()
        {
            if (Client is not null)
                await Client.DisposeAsync();
        }
    }

    [Fact]
    public async Task AnAssemblyForNet_BesideAnOldFramework_IsReadAgainstTheInstalledRuntime()
    {
        string file = await EnumComesFrom(_old.Client, ModernUser, "ModernUser.Shade");

        Assert.StartsWith(Path.GetDirectoryName(Runtime)! + Path.DirectorySeparatorChar, file, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(Path.DirectorySeparatorChar + "System.Private.CoreLib.dll", file, StringComparison.OrdinalIgnoreCase);
    }

    // The old framework's System.Runtime holds no BindingFlags, so code read against it writes the
    // flags as a number, (BindingFlags)60.
    [Fact]
    public async Task Decompile_OfAnAssemblyForNet_BesideAnOldFramework_WritesFlagsByName()
    {
        Assert.Contains(
            "public const BindingFlags Everything = BindingFlags.Instance | BindingFlags.Static | " +
            "BindingFlags.Public | BindingFlags.NonPublic;",
            await Ask(_old.Client, "decompile", new()
            {
                ["assembly"] = ModernUser,
                ["type_name"] = "ModernUser.Lookup",
            }));
    }

    // The interface names AssemblyName through netstandard, which the old framework's file leads
    // to its mscorlib, and the class through System.Runtime, whose old file lacks it: read against
    // the old framework, the two do not match, and the answer names no interface member.
    [Fact]
    public async Task Implements_OfAnAssemblyForNet_BesideAnOldFramework_NamesTheMemberOfALibraryForNetStandard()
    {
        Assert.Equal(
            "1 interface member implemented by ModernUser.VersionLabel.Label(AssemblyName) [declared]:\n" +
            "ExternalLib.Versions.IVersionLabel.Label(AssemblyName)",
            await Ask(_old.Client, "analyze", new()
            {
                ["assembly"] = ModernUser,
                ["type_name"] = "ModernUser.VersionLabel",
                ["member_name"] = "Label",
                ["kind"] = "implements",
            }));
    }

    // A library for .NET Standard runs on either kind of framework, and the search's choice stands.
    [Fact]
    public async Task AnAssemblyForNetStandard_BesideAnOldFramework_IsReadAgainstIt()
    {
        Assert.Equal(
            Path.Combine(OldFramework, "mscorlib.dll"),
            await EnumComesFrom(_old.Client, CallShapes, "CallShapes.Constraints.Hue"),
            ignoreCase: true);
    }

    // A folder that holds a .NET runtime as new as the assembly's, as a game's that runs on .NET
    // does, still supplies its framework.
    [Fact]
    public async Task AnAssemblyForNet_BesideAFolderWithANetRuntime_IsReadAgainstThatRuntime()
    {
        string root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "ILens-runtime-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            foreach (string name in new[] { "System.Private.CoreLib.dll", "System.Runtime.dll" })
                File.Copy(Path.Combine(Runtime, name), Path.Combine(root, name));
            await using var client = await StartServer("ILens-runtime-folder", root);

            Assert.Equal(
                Path.Combine(root, "System.Private.CoreLib.dll"),
                await EnumComesFrom(client, ModernUser, "ModernUser.Shade"),
                ignoreCase: true);
        }
        finally
        {
            await ILensServerFixture.DeleteWhenReleased(root);
        }
    }

    /// <summary>
    /// The file System.Enum was read from for <paramref name="assembly"/>, as the error for
    /// HasFlag on its enum <paramref name="enumName"/> names it.
    /// </summary>
    private static async Task<string> EnumComesFrom(McpClient client, string assembly, string enumName)
    {
        const string Marker = "System.Enum comes from ";
        string text = await Ask(client, "decompile", new()
        {
            ["assembly"] = assembly,
            ["type_name"] = enumName,
            ["member_name"] = "HasFlag",
        });
        int at = text.IndexOf(Marker, StringComparison.Ordinal);
        Assert.True(at >= 0, text);
        return text[(at + Marker.Length)..].TrimEnd('.');
    }

    /// <summary>
    /// A server whose allow-roots are the test output, its References folder and
    /// <paramref name="framework"/>, in that order.
    /// </summary>
    private static async Task<McpClient> StartServer(string name, string framework) =>
        await ILensServerFixture.ConnectAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = name,
            Command = ILensServerFixture.ResolveILensExecutable(),
            Arguments =
            [
                "--allow-root", AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar),
                "--allow-root", Path.Combine(AppContext.BaseDirectory, "References"),
                "--allow-root", framework,
            ],
        }));

    /// <summary>The text of a tool's answer, an error's included.</summary>
    private static async Task<string> Ask(McpClient client, string toolName, Dictionary<string, object?> arguments)
    {
        CallToolResult result = await client.CallToolAsync(toolName, arguments);
        return string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
    }
}
