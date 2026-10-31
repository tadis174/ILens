using System.Reflection;
using System.Reflection.Emit;

namespace Tests;

/// <summary>
/// Writes a build of an assembly whose generic classes carry no count of their type parameters in
/// their names, as the F# compiler names the classes it generates; the C# compiler always writes
/// the count, so no fixture project can produce them. The class Uncounted.Box and the delegate
/// Uncounted.Notify have one type parameter each. In the first build Uncounted.Crate has one as
/// well and a static field Instance of its own construction, Crate&lt;T&gt;; in the second it has
/// none, and Instance is a Crate. Uncounted.Shelf has a field Slot, a constructor, a method Take
/// that takes a box, a method Give that returns one, a property Front and an event Changed, whose
/// types are Box&lt;int&gt; and Notify&lt;int&gt; in the first build and Box&lt;string&gt; and
/// Notify&lt;string&gt; in the second, beside a field Spare that is a Box&lt;int&gt; in both.
/// Uncounted.Tray has one type parameter, which its name counts in the first build, Tray`1, and
/// not in the second, and Uncounted.Cart has a field Load that is a Tray&lt;int&gt; in both.
/// </summary>
internal static class UncountedGenericAssembly
{
    /// <summary>
    /// Write the first build, or with <paramref name="second"/> the second, to a new file in the
    /// test output and return its path.
    /// </summary>
    public static string Write(bool second)
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"Uncounted_{Guid.NewGuid():N}.dll");
        var assembly = new PersistedAssemblyBuilder(
            new AssemblyName("Uncounted") { Version = new Version(1, 0, 0, 0) }, typeof(object).Assembly);
        var module = assembly.DefineDynamicModule("Uncounted");

        var box = module.DefineType("Uncounted.Box", TypeAttributes.Public | TypeAttributes.Class);
        box.DefineGenericParameters("T");
        box.DefineDefaultConstructor(MethodAttributes.Public);

        var notify = module.DefineType("Uncounted.Notify",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class, typeof(MulticastDelegate));
        var notified = notify.DefineGenericParameters("T")[0];
        notify.DefineConstructor(
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName
                    | MethodAttributes.RTSpecialName,
                CallingConventions.Standard, [typeof(object), typeof(IntPtr)])
            .SetImplementationFlags(MethodImplAttributes.Runtime | MethodImplAttributes.Managed);
        notify.DefineMethod("Invoke",
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Virtual,
                typeof(void), [notified])
            .SetImplementationFlags(MethodImplAttributes.Runtime | MethodImplAttributes.Managed);

        var crate = module.DefineType("Uncounted.Crate", TypeAttributes.Public | TypeAttributes.Class);
        var own = second ? crate : crate.MakeGenericType(crate.DefineGenericParameters("T"));
        crate.DefineField("Instance", own, FieldAttributes.Public | FieldAttributes.Static);
        crate.DefineDefaultConstructor(MethodAttributes.Public);

        var argument = second ? typeof(string) : typeof(int);
        var boxed = box.MakeGenericType(argument);
        var shelf = module.DefineType("Uncounted.Shelf", TypeAttributes.Public | TypeAttributes.Class);
        shelf.DefineField("Slot", boxed, FieldAttributes.Public);
        shelf.DefineField("Spare", box.MakeGenericType(typeof(int)), FieldAttributes.Public);

        var constructor = shelf.DefineConstructor(
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName
                | MethodAttributes.RTSpecialName,
            CallingConventions.Standard, [boxed]);
        constructor.DefineParameter(1, ParameterAttributes.None, "box");
        var il = constructor.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Ret);

        var take = shelf.DefineMethod("Take", MethodAttributes.Public | MethodAttributes.HideBySig,
            typeof(void), [boxed]);
        take.DefineParameter(1, ParameterAttributes.None, "box");
        take.GetILGenerator().Emit(OpCodes.Ret);

        ReturnsNull(shelf.DefineMethod("Give", MethodAttributes.Public | MethodAttributes.HideBySig,
            boxed, Type.EmptyTypes));

        var getFront = shelf.DefineMethod("get_Front",
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName,
            boxed, Type.EmptyTypes);
        ReturnsNull(getFront);
        shelf.DefineProperty("Front", PropertyAttributes.None, boxed, Type.EmptyTypes).SetGetMethod(getFront);

        var handler = notify.MakeGenericType(argument);
        var changed = shelf.DefineEvent("Changed", EventAttributes.None, handler);
        changed.SetAddOnMethod(Accessor(shelf, "add_Changed", handler));
        changed.SetRemoveOnMethod(Accessor(shelf, "remove_Changed", handler));

        var tray = module.DefineType(second ? "Uncounted.Tray" : "Uncounted.Tray`1",
            TypeAttributes.Public | TypeAttributes.Class);
        tray.DefineGenericParameters("T");
        var cart = module.DefineType("Uncounted.Cart", TypeAttributes.Public | TypeAttributes.Class);
        cart.DefineField("Load", tray.MakeGenericType(typeof(int)), FieldAttributes.Public);

        box.CreateType();
        notify.CreateType();
        crate.CreateType();
        shelf.CreateType();
        tray.CreateType();
        cart.CreateType();
        assembly.Save(path);
        return path;
    }

    private static void ReturnsNull(MethodBuilder method)
    {
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
    }

    private static MethodBuilder Accessor(TypeBuilder type, string name, Type handler)
    {
        var accessor = type.DefineMethod(name,
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName,
            typeof(void), [handler]);
        accessor.DefineParameter(1, ParameterAttributes.None, "value");
        accessor.GetILGenerator().Emit(OpCodes.Ret);
        return accessor;
    }

    public static void TryDelete(params string[] paths) => BurstShapedAssembly.TryDelete(paths);
}
