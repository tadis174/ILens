using System.Reflection;
using System.Reflection.Emit;

namespace Tests;

/// <summary>
/// Writes an assembly with two uses of a name that IL allows and C# refuses, so no fixture
/// project can produce them. The class SameName.Twice declares a field Value and a method
/// Value(), and the class SameName.Named a method Named() beside its constructor.
/// </summary>
internal static class SameNameAssembly
{
    /// <summary>Write the assembly to a new file in the test output and return its path.</summary>
    public static string Write()
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"SameName_{Guid.NewGuid():N}.dll");
        var assembly = new PersistedAssemblyBuilder(
            new AssemblyName("SameName") { Version = new Version(1, 0, 0, 0) }, typeof(object).Assembly);
        var module = assembly.DefineDynamicModule("SameName");

        var twice = module.DefineType("SameName.Twice", TypeAttributes.Public | TypeAttributes.Class);
        twice.DefineDefaultConstructor(MethodAttributes.Public);
        twice.DefineField("Value", typeof(int), FieldAttributes.Public);
        ReturnOne(twice.DefineMethod("Value", MethodAttributes.Public, typeof(int), Type.EmptyTypes));

        var named = module.DefineType("SameName.Named", TypeAttributes.Public | TypeAttributes.Class);
        named.DefineDefaultConstructor(MethodAttributes.Public);
        ReturnOne(named.DefineMethod("Named", MethodAttributes.Public, typeof(int), Type.EmptyTypes));

        twice.CreateType();
        named.CreateType();
        assembly.Save(path);
        return path;
    }

    private static void ReturnOne(MethodBuilder method)
    {
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ret);
    }
}
