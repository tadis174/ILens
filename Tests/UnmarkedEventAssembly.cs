using System.Reflection;
using System.Reflection.Emit;

namespace Tests;

/// <summary>
/// Writes an assembly whose class Unmarked.Ticker declares an event Ticked, whose subscribers
/// a private field of the event's name holds without [CompilerGenerated], as the C# compiler
/// before Roslyn wrote it and no compiler today does, and a method Tick that raises the event.
/// </summary>
internal static class UnmarkedEventAssembly
{
    /// <summary>Write the assembly to a new file in the test output and return its path.</summary>
    public static string Write()
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"UnmarkedEvent_{Guid.NewGuid():N}.dll");
        var assembly = new PersistedAssemblyBuilder(
            new AssemblyName("UnmarkedEvent") { Version = new Version(1, 0, 0, 0) }, typeof(object).Assembly);
        var module = assembly.DefineDynamicModule("UnmarkedEvent");

        var ticker = module.DefineType("Unmarked.Ticker", TypeAttributes.Public | TypeAttributes.Class);
        ticker.DefineDefaultConstructor(MethodAttributes.Public);
        var subscribers = ticker.DefineField("Ticked", typeof(EventHandler), FieldAttributes.Private);
        var ticked = ticker.DefineEvent("Ticked", EventAttributes.None, typeof(EventHandler));
        ticked.SetAddOnMethod(Accessor(ticker, subscribers, "add_Ticked", nameof(Delegate.Combine)));
        ticked.SetRemoveOnMethod(Accessor(ticker, subscribers, "remove_Ticked", nameof(Delegate.Remove)));

        var tick = ticker.DefineMethod("Tick", MethodAttributes.Public, typeof(void), Type.EmptyTypes);
        var il = tick.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, subscribers);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldsfld, typeof(EventArgs).GetField(nameof(EventArgs.Empty))!);
        il.Emit(OpCodes.Callvirt, typeof(EventHandler).GetMethod(nameof(EventHandler.Invoke))!);
        il.Emit(OpCodes.Ret);

        ticker.CreateType();
        assembly.Save(path);
        return path;
    }

    /// <summary>An add or remove accessor that combines the handler into the field or takes it out.</summary>
    private static MethodBuilder Accessor(TypeBuilder type, FieldBuilder subscribers, string name, string operation)
    {
        var method = type.DefineMethod(name,
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
            typeof(void), [typeof(EventHandler)]);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, subscribers);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, typeof(Delegate).GetMethod(operation, [typeof(Delegate), typeof(Delegate)])!);
        il.Emit(OpCodes.Castclass, typeof(EventHandler));
        il.Emit(OpCodes.Stfld, subscribers);
        il.Emit(OpCodes.Ret);
        return method;
    }

    /// <summary>Best-effort cleanup, as <see cref="BurstShapedAssembly.TryDelete"/> describes.</summary>
    public static void TryDelete(string path) => BurstShapedAssembly.TryDelete(path);
}
