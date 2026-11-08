using System.Reflection;
using System.Reflection.Emit;

namespace Tests;

/// <summary>
/// Writes an assembly whose class Natives.Engine declares accessors without a body, as an engine
/// that implements them natively declares its internal calls: a property Enabled whose getter and
/// setter are both internal calls, as C# writes an extern property; a property Level whose getter
/// is one and whose setter stores into a field, which no C# compiler writes, since C# makes a
/// whole property extern; and an event Changed whose add and remove accessors are internal calls,
/// which the decompiler writes as a field-like extern event.
/// </summary>
internal static class ExternAccessorAssembly
{
    private const MethodAttributes AccessorAttributes =
        MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig;

    /// <summary>Write the assembly to a new file in the test output and return its path.</summary>
    public static string Write()
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"ExternAccessors_{Guid.NewGuid():N}.dll");
        var assembly = new PersistedAssemblyBuilder(
            new AssemblyName("ExternAccessors") { Version = new Version(1, 0, 0, 0) }, typeof(object).Assembly);
        var module = assembly.DefineDynamicModule("ExternAccessors");

        var engine = module.DefineType("Natives.Engine", TypeAttributes.Public | TypeAttributes.Class);
        engine.DefineDefaultConstructor(MethodAttributes.Public);

        var enabled = engine.DefineProperty("Enabled", PropertyAttributes.None, typeof(bool), null);
        enabled.SetGetMethod(InternalCall(engine, "get_Enabled", typeof(bool), Type.EmptyTypes));
        enabled.SetSetMethod(InternalCall(engine, "set_Enabled", typeof(void), [typeof(bool)]));

        var stored = engine.DefineField("_level", typeof(int), FieldAttributes.Private);
        var level = engine.DefineProperty("Level", PropertyAttributes.None, typeof(int), null);
        level.SetGetMethod(InternalCall(engine, "get_Level", typeof(int), Type.EmptyTypes));
        var setLevel = engine.DefineMethod("set_Level", AccessorAttributes, typeof(void), [typeof(int)]);
        setLevel.DefineParameter(1, ParameterAttributes.None, "value");
        var il = setLevel.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stfld, stored);
        il.Emit(OpCodes.Ret);
        level.SetSetMethod(setLevel);

        var changed = engine.DefineEvent("Changed", EventAttributes.None, typeof(EventHandler));
        changed.SetAddOnMethod(InternalCall(engine, "add_Changed", typeof(void), [typeof(EventHandler)]));
        changed.SetRemoveOnMethod(InternalCall(engine, "remove_Changed", typeof(void), [typeof(EventHandler)]));

        engine.CreateType();
        assembly.Save(path);
        return path;
    }

    /// <summary>
    /// An accessor without a body, which the runtime implements, as MethodImplOptions.InternalCall
    /// marks it, with its parameter named value, as C# names it.
    /// </summary>
    private static MethodBuilder InternalCall(TypeBuilder type, string name, Type returnType, Type[] parameters)
    {
        var method = type.DefineMethod(name, AccessorAttributes, returnType, parameters);
        if (parameters.Length == 1)
            method.DefineParameter(1, ParameterAttributes.None, "value");
        method.SetImplementationFlags(MethodImplAttributes.InternalCall);
        return method;
    }

    /// <summary>Best-effort cleanup, as <see cref="BurstShapedAssembly.TryDelete"/> describes.</summary>
    public static void TryDelete(string path) => BurstShapedAssembly.TryDelete(path);
}
