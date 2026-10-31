using System.Reflection;
using System.Reflection.Emit;

namespace Tests;

/// <summary>
/// Writes an assembly whose virtual calls name an override where the method has no type
/// parameter. The VB and the F# compiler write such a call for any virtual method, and a call
/// compiled against another version of a library can turn out to be one; a C# compiler writes it
/// only for a method with a type parameter of its own, so no fixture project can hold it.
/// NamedOverride.Crane declares the virtual method Lift and the virtual property Reach,
/// NamedOverride.Gantry overrides both, and NamedOverride.Derrick overrides Lift and calls
/// Gantry's without virtual dispatch, as <c>base.Lift()</c> compiles. Each method of
/// NamedOverride.Yard uses one of them on its parameter: OnCrane, OnGantry and OnDerrick call
/// the Lift of the parameter's own class, ReachOfCrane and ReachOfGantry read that class's
/// Reach, and Group makes a delegate of Gantry's Lift through the object.
/// </summary>
internal static class NamedOverrideAssembly
{
    /// <summary>Write the assembly to a new file in the test output and return its path.</summary>
    public static string Write()
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"NamedOverride_{Guid.NewGuid():N}.dll");
        var assembly = new PersistedAssemblyBuilder(
            new AssemblyName("NamedOverride") { Version = new Version(1, 0, 0, 0) }, typeof(object).Assembly);
        var module = assembly.DefineDynamicModule("NamedOverride");
        const MethodAttributes Declares = MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot;
        const MethodAttributes Overrides = MethodAttributes.Public | MethodAttributes.Virtual;

        var crane = module.DefineType("NamedOverride.Crane", TypeAttributes.Public | TypeAttributes.Class);
        var lift = Returning(crane, "Lift", Declares, 1);
        var reach = Getter(crane, "Reach", Declares, 1);
        crane.CreateType();

        var gantry = module.DefineType("NamedOverride.Gantry", TypeAttributes.Public | TypeAttributes.Class, crane);
        var gantryLift = Returning(gantry, "Lift", Overrides, 2);
        var gantryReach = Getter(gantry, "Reach", Overrides, 2);
        gantry.CreateType();

        var derrick = module.DefineType("NamedOverride.Derrick", TypeAttributes.Public | TypeAttributes.Class, gantry);
        var derrickLift = derrick.DefineMethod("Lift", Overrides | MethodAttributes.HideBySig, typeof(int), Type.EmptyTypes);
        var code = derrickLift.GetILGenerator();
        code.Emit(OpCodes.Ldarg_0);
        code.Emit(OpCodes.Call, gantryLift);
        code.Emit(OpCodes.Ret);
        derrick.CreateType();

        var yard = module.DefineType("NamedOverride.Yard",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Class);
        Using(yard, "OnCrane", crane, "crane", lift);
        Using(yard, "OnGantry", gantry, "gantry", gantryLift);
        Using(yard, "OnDerrick", derrick, "derrick", derrickLift);
        Using(yard, "ReachOfCrane", crane, "crane", reach);
        Using(yard, "ReachOfGantry", gantry, "gantry", gantryReach);
        var group = yard.DefineMethod("Group", MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            typeof(Func<int>), [gantry]);
        group.DefineParameter(1, ParameterAttributes.None, "gantry");
        code = group.GetILGenerator();
        code.Emit(OpCodes.Ldarg_0);
        code.Emit(OpCodes.Dup);
        code.Emit(OpCodes.Ldvirtftn, gantryLift);
        code.Emit(OpCodes.Newobj, typeof(Func<int>).GetConstructor([typeof(object), typeof(IntPtr)])!);
        code.Emit(OpCodes.Ret);
        yard.CreateType();

        assembly.Save(path);
        return path;
    }

    private static MethodBuilder Returning(TypeBuilder type, string name, MethodAttributes attributes, int value)
    {
        var method = type.DefineMethod(name, attributes | MethodAttributes.HideBySig, typeof(int), Type.EmptyTypes);
        var code = method.GetILGenerator();
        code.Emit(OpCodes.Ldc_I4, value);
        code.Emit(OpCodes.Ret);
        return method;
    }

    private static MethodBuilder Getter(TypeBuilder type, string name, MethodAttributes attributes, int value)
    {
        var getter = Returning(type, "get_" + name, attributes | MethodAttributes.SpecialName, value);
        type.DefineProperty(name, PropertyAttributes.None, typeof(int), null).SetGetMethod(getter);
        return getter;
    }

    /// <summary>A static method that makes one virtual call on its parameter, naming <paramref name="called"/>.</summary>
    private static void Using(TypeBuilder type, string name, Type receiver, string parameter, MethodInfo called)
    {
        var method = type.DefineMethod(name, MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
            typeof(int), [receiver]);
        method.DefineParameter(1, ParameterAttributes.None, parameter);
        var code = method.GetILGenerator();
        code.Emit(OpCodes.Ldarg_0);
        code.Emit(OpCodes.Callvirt, called);
        code.Emit(OpCodes.Ret);
    }

    /// <summary>Best-effort cleanup, as <see cref="BurstShapedAssembly.TryDelete"/> describes.</summary>
    public static void TryDelete(string path) => BurstShapedAssembly.TryDelete(path);
}
