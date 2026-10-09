using System.Reflection;
using System.Reflection.Emit;

namespace Tests;

/// <summary>
/// Writes an assembly in the shape Unity's Burst compiler leaves a class whose method it compiles.
/// Burst names what it adds with '$', which C# cannot, so no fixture project can produce it. The
/// class BurstShaped.Container has a method Compute that only calls
/// Compute_HHHHHHHH$BurstDirectCall.Invoke, which calls Compute$BurstManaged, the copy of Compute's
/// code that Burst keeps; and Initialize$Compute_HHHHHHHH$BurstDirectCall, which starts the
/// wrapper.
/// </summary>
internal static class BurstShapedAssembly
{
    /// <summary>
    /// Write the assembly to a new file in the test output and return its path.
    /// <paramref name="counter"/> is the eight hexadecimal digits Burst numbers its wrappers by, and
    /// <paramref name="emitCode"/> writes the body of Compute$BurstManaged, which returns an int.
    /// </summary>
    public static string Write(string counter, Action<ILGenerator> emitCode)
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"BurstShaped_{Guid.NewGuid():N}.dll");
        var assembly = new PersistedAssemblyBuilder(
            new AssemblyName("BurstShaped") { Version = new Version(1, 0, 0, 0) }, typeof(object).Assembly);
        var module = assembly.DefineDynamicModule("BurstShaped");
        var container = module.DefineType("BurstShaped.Container",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Class);

        var copy = container.DefineMethod("Compute$BurstManaged",
            MethodAttributes.Public | MethodAttributes.Static, typeof(int), Type.EmptyTypes);
        emitCode(copy.GetILGenerator());

        var wrapper = container.DefineNestedType($"Compute_{counter}$BurstDirectCall",
            TypeAttributes.NestedAssembly | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Class);
        var invoke = wrapper.DefineMethod("Invoke",
            MethodAttributes.Public | MethodAttributes.Static, typeof(int), Type.EmptyTypes);
        var il = invoke.GetILGenerator();
        il.Emit(OpCodes.Call, copy);
        il.Emit(OpCodes.Ret);
        var initialize = wrapper.DefineMethod("Initialize",
            MethodAttributes.Public | MethodAttributes.Static, typeof(void), Type.EmptyTypes);
        initialize.GetILGenerator().Emit(OpCodes.Ret);

        var compute = container.DefineMethod("Compute",
            MethodAttributes.Public | MethodAttributes.Static, typeof(int), Type.EmptyTypes);
        il = compute.GetILGenerator();
        il.Emit(OpCodes.Call, invoke);
        il.Emit(OpCodes.Ret);

        var start = container.DefineMethod($"Initialize$Compute_{counter}$BurstDirectCall",
            MethodAttributes.Public | MethodAttributes.Static, typeof(void), Type.EmptyTypes);
        il = start.GetILGenerator();
        il.Emit(OpCodes.Call, initialize);
        il.Emit(OpCodes.Ret);

        wrapper.CreateType();
        container.CreateType();
        assembly.Save(path);
        return path;
    }

    /// <summary>Writes a body that returns <paramref name="value"/> with the short form of ldc.i4.</summary>
    public static Action<ILGenerator> Returns(int value) => il =>
    {
        il.Emit(OpCodes.Ldc_I4_S, (sbyte)value);
        il.Emit(OpCodes.Ret);
    };

    /// <summary>
    /// Best-effort cleanup. The server keeps a file mapped while its host stays in the cache, so
    /// Delete can fail on Windows; the unique name keeps a leftover from colliding.
    /// </summary>
    public static void TryDelete(params string[] paths)
    {
        foreach (var path in paths)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
