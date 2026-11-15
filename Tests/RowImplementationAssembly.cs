using System.Reflection;
using System.Reflection.Emit;

namespace Tests;

/// <summary>
/// Writes an assembly in which a class implements an interface's members by its metadata rows
/// alone, in two forms no C# fixture can hold. RowBound.IJob declares the method Run and the
/// property Size. RowBound.Worker implements Run with a public method named Start, as the VB
/// compiler writes a member that implements under a name of its own, and declares a public
/// method Run beside it, which the row keeps from IJob.Run. It implements Size's getter with a
/// method named RowBound.IJob.get_Size that no property owns, as the F# compiler writes an
/// interface's property.
/// <para>
/// It also holds classes whose rows name an interface without its base interface, as the VB
/// compiler writes a class whose base class has the base interface already; the C# compiler
/// writes a row for every base interface. RowBound.ILateStep derives from RowBound.IStep, which
/// declares Order. Step lists IStep and implements Order with a method named Step_Order, beside
/// a public virtual Order. LateStep derives from Step, lists ILateStep alone and overrides
/// Order: it keeps Step's binding. LoneStep lists ILateStep alone without such a base class, so
/// IStep is new to it and its public Order implements IStep's. RenewedStep derives from Step
/// and lists both interfaces, so it binds IStep's Order anew, to its own override.
/// </para>
/// <para>
/// It also holds an interface's members in flags no C# compiler writes. RowBound.ISlot's Get is
/// virtual with a body and without NewSlot, and its property Level has a getter with a body
/// that is not virtual beside an abstract setter. RowBound.Holder lists ISlot and declares a
/// public virtual Get and a property Level with both accessors; the runtime binds Get and
/// Level's setter to Holder's and leaves Level's getter out of its interface map, as a program
/// that loaded an assembly of these shapes measured on 2026-11-14.
/// </para>
/// </summary>
internal static class RowImplementationAssembly
{
    /// <summary>Write the assembly to a new file in the test output and return its path.</summary>
    public static string Write()
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"RowBound_{Guid.NewGuid():N}.dll");
        var assembly = new PersistedAssemblyBuilder(
            new AssemblyName("RowBound") { Version = new Version(1, 0, 0, 0) }, typeof(object).Assembly);
        var module = assembly.DefineDynamicModule("RowBound");
        const MethodAttributes Declares = MethodAttributes.Public | MethodAttributes.Abstract | MethodAttributes.Virtual
            | MethodAttributes.NewSlot | MethodAttributes.HideBySig;
        const MethodAttributes Implements = MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot;
        const MethodAttributes Overridable = MethodAttributes.Public | MethodAttributes.Virtual;

        var job = module.DefineType("RowBound.IJob",
            TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract);
        var run = job.DefineMethod("Run", Declares, typeof(int), Type.EmptyTypes);
        var getSize = job.DefineMethod("get_Size", Declares | MethodAttributes.SpecialName, typeof(int), Type.EmptyTypes);
        job.DefineProperty("Size", PropertyAttributes.None, typeof(int), null).SetGetMethod(getSize);
        job.CreateType();

        var worker = module.DefineType("RowBound.Worker", TypeAttributes.Public | TypeAttributes.Class, typeof(object), [job]);
        worker.DefineDefaultConstructor(MethodAttributes.Public);
        worker.DefineMethodOverride(Returning(worker, "Start", MethodAttributes.Public | Implements, 1), run);
        Returning(worker, "Run", MethodAttributes.Public, 2);
        worker.DefineMethodOverride(Returning(worker, "RowBound.IJob.get_Size", MethodAttributes.Private | Implements, 3), getSize);
        worker.CreateType();

        var step = module.DefineType("RowBound.IStep",
            TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract);
        var order = step.DefineMethod("Order", Declares, typeof(int), Type.EmptyTypes);
        step.CreateType();
        var lateStep = module.DefineType("RowBound.ILateStep",
            TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract, null, [step]);
        lateStep.CreateType();

        var stepClass = module.DefineType("RowBound.Step", TypeAttributes.Public | TypeAttributes.Class, typeof(object), [step]);
        stepClass.DefineMethodOverride(Returning(stepClass, "Step_Order", MethodAttributes.Private | Implements, 4), order);
        Returning(stepClass, "Order", Overridable | MethodAttributes.NewSlot, 5);
        stepClass.CreateType();

        // The rows a class gets are the interfaces given here and no base interface of them.
        var lateClass = module.DefineType("RowBound.LateStep", TypeAttributes.Public | TypeAttributes.Class, stepClass, [lateStep]);
        Returning(lateClass, "Order", Overridable, 6);
        lateClass.CreateType();

        var loneClass = module.DefineType("RowBound.LoneStep", TypeAttributes.Public | TypeAttributes.Class, typeof(object), [lateStep]);
        Returning(loneClass, "Order", Overridable | MethodAttributes.NewSlot, 7);
        loneClass.CreateType();

        var renewedClass = module.DefineType("RowBound.RenewedStep", TypeAttributes.Public | TypeAttributes.Class, stepClass, [lateStep, step]);
        Returning(renewedClass, "Order", Overridable, 8);
        renewedClass.CreateType();

        var slot = module.DefineType("RowBound.ISlot",
            TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract);
        Returning(slot, "Get", MethodAttributes.Public | MethodAttributes.Virtual, 9);
        var slotLevel = slot.DefineProperty("Level", PropertyAttributes.None, typeof(int), null);
        slotLevel.SetGetMethod(Returning(slot, "get_Level", MethodAttributes.Public | MethodAttributes.SpecialName, 10));
        slotLevel.SetSetMethod(Setter(slot, Declares | MethodAttributes.SpecialName));
        slot.CreateType();

        var holder = module.DefineType("RowBound.Holder", TypeAttributes.Public | TypeAttributes.Class, typeof(object), [slot]);
        holder.DefineDefaultConstructor(MethodAttributes.Public);
        Returning(holder, "Get", Overridable | MethodAttributes.NewSlot, 11);
        var holderLevel = holder.DefineProperty("Level", PropertyAttributes.None, typeof(int), null);
        holderLevel.SetGetMethod(Returning(holder, "get_Level", Overridable | MethodAttributes.NewSlot | MethodAttributes.SpecialName, 12));
        holderLevel.SetSetMethod(Setter(holder, Overridable | MethodAttributes.NewSlot | MethodAttributes.SpecialName));
        holder.CreateType();

        assembly.Save(path);
        return path;
    }

    private static MethodBuilder Setter(TypeBuilder type, MethodAttributes attributes)
    {
        var method = type.DefineMethod("set_Level", attributes | MethodAttributes.HideBySig, typeof(void), [typeof(int)]);
        if ((attributes & MethodAttributes.Abstract) == 0)
            method.GetILGenerator().Emit(OpCodes.Ret);
        return method;
    }

    private static MethodBuilder Returning(TypeBuilder type, string name, MethodAttributes attributes, int value)
    {
        var method = type.DefineMethod(name, attributes | MethodAttributes.HideBySig, typeof(int), Type.EmptyTypes);
        var code = method.GetILGenerator();
        code.Emit(OpCodes.Ldc_I4, value);
        code.Emit(OpCodes.Ret);
        return method;
    }

    /// <summary>Best-effort cleanup, as <see cref="BurstShapedAssembly.TryDelete"/> describes.</summary>
    public static void TryDelete(string path) => BurstShapedAssembly.TryDelete(path);
}
