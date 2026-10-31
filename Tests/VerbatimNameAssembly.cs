using System.Reflection;
using System.Reflection.Emit;

namespace Tests;

/// <summary>
/// Writes an assembly whose class has a field named <c>@_instance</c>, as the F# compiler names
/// the instance field of a closure class, which C# cannot declare, so no fixture project can
/// produce it. The class Verbatim.Closure holds that field beside a field named Count.
/// </summary>
internal static class VerbatimNameAssembly
{
    /// <summary>Write the assembly to a new file in the test output and return its path.</summary>
    public static string Write()
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"Verbatim_{Guid.NewGuid():N}.dll");
        var assembly = new PersistedAssemblyBuilder(
            new AssemblyName("Verbatim") { Version = new Version(1, 0, 0, 0) }, typeof(object).Assembly);
        var module = assembly.DefineDynamicModule("Verbatim");

        var closure = module.DefineType("Verbatim.Closure", TypeAttributes.Public | TypeAttributes.Class);
        closure.DefineDefaultConstructor(MethodAttributes.Public);
        closure.DefineField("@_instance", typeof(int), FieldAttributes.Public | FieldAttributes.Static);
        closure.DefineField("Count", typeof(int), FieldAttributes.Public);

        closure.CreateType();
        assembly.Save(path);
        return path;
    }
}
