using System.Reflection;
using System.Reflection.Emit;

namespace Tests;

/// <summary>
/// Writes an assembly named netstandard that defines no class. Beside an assembly built against
/// .NET Standard it stands for a core library without the reflection types that assembly names,
/// as an older System.Runtime is beside an assembly built against .NET 10: the decompiler then
/// cannot rebuild a lambda that becomes an expression tree and writes the tree's construction.
/// </summary>
internal static class EmptyCoreAssembly
{
    /// <summary>Write the assembly as netstandard.dll into <paramref name="folder"/>.</summary>
    public static void Write(string folder)
    {
        var assembly = new PersistedAssemblyBuilder(
            new AssemblyName("netstandard") { Version = new Version(2, 0, 0, 0) }, typeof(object).Assembly);
        assembly.DefineDynamicModule("netstandard");
        assembly.Save(Path.Combine(folder, "netstandard.dll"));
    }
}
