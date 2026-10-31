using System.Reflection;
using System.Reflection.Emit;

namespace Tests;

/// <summary>
/// Writes an assembly whose class SameKey.Holder declares members C# refuses: a property Rate
/// overloaded on its parameter without being the class's default member, as VB declares one, and
/// two fields Mark of one type and two methods Run of one signature, as an obfuscator can leave
/// them. No fixture project can produce them.
/// </summary>
internal static class SameKeyAssembly
{
    /// <summary>
    /// Write the assembly to a new file in the test output and return its path. Every getter and
    /// method returns 1, except the second Run and the getter of Rate[string], which return
    /// <paramref name="second"/>, so two writes with other values differ in those two bodies alone.
    /// </summary>
    public static string Write(int second)
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"SameKey_{Guid.NewGuid():N}.dll");
        var assembly = new PersistedAssemblyBuilder(
            new AssemblyName("SameKey") { Version = new Version(1, 0, 0, 0) }, typeof(object).Assembly);
        var module = assembly.DefineDynamicModule("SameKey");
        var holder = module.DefineType("SameKey.Holder", TypeAttributes.Public | TypeAttributes.Class);
        holder.DefineDefaultConstructor(MethodAttributes.Public);

        DefineRate(holder, typeof(int), 1);
        DefineRate(holder, typeof(string), second);

        holder.DefineField("Mark", typeof(int), FieldAttributes.Public);
        holder.DefineField("Mark", typeof(int), FieldAttributes.Public);

        Returns(holder.DefineMethod("Run", MethodAttributes.Public, typeof(int), Type.EmptyTypes), 1);
        Returns(holder.DefineMethod("Run", MethodAttributes.Public, typeof(int), Type.EmptyTypes), second);

        holder.CreateType();
        assembly.Save(path);
        return path;
    }

    private static void DefineRate(TypeBuilder holder, Type key, int value)
    {
        var getter = holder.DefineMethod("get_Rate",
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig, typeof(int), [key]);
        getter.DefineParameter(1, ParameterAttributes.None, "key");
        Returns(getter, value);
        holder.DefineProperty("Rate", PropertyAttributes.None, typeof(int), [key]).SetGetMethod(getter);
    }

    private static void Returns(MethodBuilder method, int value)
    {
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldc_I4, value);
        il.Emit(OpCodes.Ret);
    }

    public static void TryDelete(params string[] paths) => BurstShapedAssembly.TryDelete(paths);
}
