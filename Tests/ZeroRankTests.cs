using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for answers about an assembly whose code names an array of rank 0, which
/// <see cref="ZeroRankAssembly"/> writes: in the signature of a member reference, and as the type
/// specification that is the class of an array's constructor and Get. The decompiler's type
/// system cannot build such an array, and every pass over the method bodies takes such a row as
/// naming nothing and reads on. The runtime finds no method for such a reference and rejects
/// such an array as a bad image, so the code names nothing there that could run.
/// </summary>
public sealed class ZeroRankTests : IClassFixture<ILensServerFixture>
{
    private readonly ILensServerFixture _server;

    public ZeroRankTests(ILensServerFixture server)
    {
        _server = server;
    }

    // Fill and Get share their names with the methods the rows of rank 0 name, so each row of
    // that name is resolved to see whether it names them, and one that cannot be decoded names
    // neither.
    [Theory]
    [InlineData("Fill", "used_by", "1 user of ZeroRank.User.Fill(int) [declared]:\nZeroRank.User.Pour()")]
    [InlineData("Fill", "call_sites", "1 call to ZeroRank.User.Fill(int):\nZeroRank.User.Pour()\n  Fill(1)")]
    [InlineData("Get", "used_by", "1 user of ZeroRank.User.Get(int) [declared]:\nZeroRank.User.Peek()")]
    [InlineData("Get", "call_sites", "1 call to ZeroRank.User.Get(int):\nZeroRank.User.Peek()\n  Get(1)")]
    public async Task AMethodNamedLikeOneThatARowOfRankZeroNames_IsAnsweredWithoutThatRow(
        string memberName, string kind, string expected)
    {
        var path = ZeroRankAssembly.Write();
        try
        {
            Assert.Equal(expected, await CallToolText("analyze", new()
            {
                ["assembly"] = path,
                ["type_name"] = "ZeroRank.User",
                ["member_name"] = memberName,
                ["kind"] = kind,
            }));
        }
        finally
        {
            ZeroRankAssembly.TryDelete(path);
        }
    }

    // Use calls Fill through the reference of rank 0, and Make the array's constructor and Get,
    // whose class is the specification of rank 0; both call Helper as well.
    [Theory]
    [InlineData("Use")]
    [InlineData("Make")]
    public async Task WhatAMethodUses_LeavesOutARowOfRankZero(string memberName)
    {
        var path = ZeroRankAssembly.Write();
        try
        {
            Assert.Equal(
                $"1 symbol used by ZeroRank.User.{memberName}() [declared]:\nZeroRank.User.Helper()",
                await CallToolText("analyze", new()
                {
                    ["assembly"] = path,
                    ["type_name"] = "ZeroRank.User",
                    ["member_name"] = memberName,
                    ["kind"] = "uses",
                }));
        }
        finally
        {
            ZeroRankAssembly.TryDelete(path);
        }
    }

    // The decompiler's own catches take BadImageFormatException alone, and its type provider
    // throws ArgumentOutOfRangeException for an array of rank 0, so decompiling code that names
    // one fails as a whole. A decompiler that reads such a row turns this test red; the answer it
    // gives then is to be pinned here instead.
    [Theory]
    [InlineData("Use")]
    [InlineData("Make")]
    public async Task DecompilingAMethodWhoseCodeNamesARowOfRankZero_IsTheDecompilersError(string memberName)
    {
        var path = ZeroRankAssembly.Write();
        try
        {
            string text = await CallToolError("decompile", new()
            {
                ["assembly"] = path,
                ["type_name"] = "ZeroRank.User",
                ["member_name"] = memberName,
            });
            Assert.StartsWith("Error: ArgumentOutOfRangeException: dimensions must be positive", text);
        }
        finally
        {
            ZeroRankAssembly.TryDelete(path);
        }
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
        Assert.True(result.IsError, $"Expected an error from '{toolName}', got: {text}");
        return text;
    }
}
