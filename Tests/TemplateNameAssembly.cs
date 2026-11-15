using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;

namespace Tests;

/// <summary>
/// Writes an assembly whose classes bear angle brackets in their own names, as C++/CLI names the
/// class of a template instance, which no C# compiler can name, so no fixture project can produce
/// them. In the namespace Native.Wrappers, the class Handle&lt;_GUID&gt; has a field Value, a
/// constructor, a method Release that returns Value, and a nested class Inner; the class
/// Handle&lt;Native::Font *&gt; has a constructor; Handle&lt;T&gt; is a generic class of the name
/// before the brackets, with a field Item and a nested class Inner of its own; and the class User
/// has a field Current, a Handle&lt;_GUID&gt;, a method Take that takes one and calls its Release,
/// a method Make that returns a Handle&lt;Native::Font *&gt;, and a method Keep that takes a
/// Handle&lt;int&gt;. The class gcroot&lt;System::String ^&gt; stands in no namespace. The changed
/// build adds a method Reset to Handle&lt;_GUID&gt;.
/// </summary>
internal static class TemplateNameAssembly
{
    /// <summary>The name of the assembly <see cref="Write"/> writes, which the other writers name.</summary>
    public const string Name = "Template";

    /// <summary>
    /// Write the assembly, or with <paramref name="changed"/> its changed build, to a new file in the
    /// test output and return its path.
    /// </summary>
    public static string Write(bool changed = false)
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"{Name}_{Guid.NewGuid():N}.dll");
        Build(changed).Assembly.Save(path);
        return path;
    }

    /// <summary>
    /// Write the assembly as Template.dll into <paramref name="folder"/>, beside Extender.dll, which
    /// references it through Extending.Extensions.Hold, a method that takes a Native.Wrappers.User,
    /// names Handle&lt;_GUID&gt; nowhere, and declares the extension method
    /// Extending.Extensions.Count&lt;T&gt;(this T[] items). An assembly's references are looked for
    /// in its own folder, so the decompiler loads Template.dll for Extender.dll. Return
    /// Extender.dll's path.
    /// </summary>
    public static string WriteWithExtender(string folder)
    {
        var (template, user) = Build(changed: false);
        template.Save(Path.Combine(folder, $"{Name}.dll"));

        var assembly = new PersistedAssemblyBuilder(
            new AssemblyName("Extender") { Version = new Version(1, 0, 0, 0) }, typeof(object).Assembly);
        var module = assembly.DefineDynamicModule("Extender");
        var extensions = module.DefineType("Extending.Extensions",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Class);
        var extension = new CustomAttributeBuilder(typeof(ExtensionAttribute).GetConstructor(Type.EmptyTypes)!, []);
        extensions.SetCustomAttribute(extension);

        var count = extensions.DefineMethod("Count",
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig);
        var item = count.DefineGenericParameters("T")[0];
        count.SetReturnType(typeof(int));
        count.SetParameters(item.MakeArrayType());
        count.DefineParameter(1, ParameterAttributes.None, "items");
        count.SetCustomAttribute(extension);
        var il = count.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Ret);

        var hold = extensions.DefineMethod("Hold",
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, typeof(void), [user]);
        hold.DefineParameter(1, ParameterAttributes.None, "user");
        hold.GetILGenerator().Emit(OpCodes.Ret);

        extensions.CreateType();
        var path = Path.Combine(folder, "Extender.dll");
        assembly.Save(path);
        return path;
    }

    /// <summary>
    /// Write an assembly that names Native.Wrappers.Handle&lt;_GUID&gt; of <see cref="Name"/> only in a
    /// typeof argument, as an attribute's value holds it, with no reference to the class: its class
    /// Namers.Tagged carries Namers.TagAttribute, whose constructor takes a type. Return its path.
    /// </summary>
    public static string WriteNamer()
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"Namer_{Guid.NewGuid():N}.dll");
        var assembly = new PersistedAssemblyBuilder(
            new AssemblyName("Namer") { Version = new Version(1, 0, 0, 0) }, typeof(object).Assembly);
        var module = assembly.DefineDynamicModule("Namer");

        var tag = module.DefineType("Namers.TagAttribute", TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class,
            typeof(Attribute));
        var constructor = tag.DefineConstructor(
            MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
            CallingConventions.Standard, [typeof(Type)]);
        constructor.DefineParameter(1, ParameterAttributes.None, "type");
        var il = constructor.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, typeof(Attribute).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, Type.EmptyTypes)!);
        il.Emit(OpCodes.Ret);

        var tagged = module.DefineType("Namers.Tagged", TypeAttributes.Public | TypeAttributes.Class);
        tagged.DefineDefaultConstructor(MethodAttributes.Public);
        tagged.SetCustomAttribute(constructor, TypeArgument($"Native.Wrappers.Handle<_GUID>, {Name}"));

        tag.CreateType();
        tagged.CreateType();
        assembly.Save(path);
        return path;
    }

    /// <summary>The assembly <see cref="Write"/> saves, and its class User for another assembly to name.</summary>
    private static (PersistedAssemblyBuilder Assembly, Type User) Build(bool changed)
    {
        var assembly = new PersistedAssemblyBuilder(
            new AssemblyName(Name) { Version = new Version(1, 0, 0, 0) }, typeof(object).Assembly);
        var module = assembly.DefineDynamicModule(Name);

        var guid = module.DefineType("Native.Wrappers.Handle<_GUID>", TypeAttributes.Public | TypeAttributes.Class);
        var value = guid.DefineField("Value", typeof(IntPtr), FieldAttributes.Public);
        guid.DefineDefaultConstructor(MethodAttributes.Public);
        var release = guid.DefineMethod("Release", MethodAttributes.Public | MethodAttributes.HideBySig,
            typeof(IntPtr), Type.EmptyTypes);
        var il = release.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, value);
        il.Emit(OpCodes.Ret);
        if (changed)
            guid.DefineMethod("Reset", MethodAttributes.Public | MethodAttributes.HideBySig, typeof(void), Type.EmptyTypes)
                .GetILGenerator().Emit(OpCodes.Ret);
        var inner = guid.DefineNestedType("Inner", TypeAttributes.NestedPublic | TypeAttributes.Class);
        inner.DefineDefaultConstructor(MethodAttributes.Public);

        var font = module.DefineType("Native.Wrappers.Handle<Native::Font *>", TypeAttributes.Public | TypeAttributes.Class);
        font.DefineDefaultConstructor(MethodAttributes.Public);

        var generic = module.DefineType("Native.Wrappers.Handle`1", TypeAttributes.Public | TypeAttributes.Class);
        var item = generic.DefineGenericParameters("T")[0];
        generic.DefineField("Item", item, FieldAttributes.Public);
        generic.DefineDefaultConstructor(MethodAttributes.Public);
        // A class nested in a generic class takes its type parameters again, as C# writes it.
        var genericInner = generic.DefineNestedType("Inner", TypeAttributes.NestedPublic | TypeAttributes.Class);
        genericInner.DefineGenericParameters("T");
        genericInner.DefineDefaultConstructor(MethodAttributes.Public);

        var user = module.DefineType("Native.Wrappers.User", TypeAttributes.Public | TypeAttributes.Class);
        user.DefineField("Current", guid, FieldAttributes.Public);
        user.DefineDefaultConstructor(MethodAttributes.Public);
        var take = user.DefineMethod("Take", MethodAttributes.Public | MethodAttributes.HideBySig, typeof(void), [guid]);
        take.DefineParameter(1, ParameterAttributes.None, "handle");
        il = take.GetILGenerator();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, release);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ret);
        il = user.DefineMethod("Make", MethodAttributes.Public | MethodAttributes.HideBySig, font, Type.EmptyTypes)
            .GetILGenerator();
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
        var keep = user.DefineMethod("Keep", MethodAttributes.Public | MethodAttributes.HideBySig, typeof(void),
            [generic.MakeGenericType(typeof(int))]);
        keep.DefineParameter(1, ParameterAttributes.None, "handle");
        keep.GetILGenerator().Emit(OpCodes.Ret);

        var root = module.DefineType("gcroot<System::String ^>", TypeAttributes.Public | TypeAttributes.Class);
        root.DefineDefaultConstructor(MethodAttributes.Public);

        guid.CreateType();
        inner.CreateType();
        font.CreateType();
        generic.CreateType();
        genericInner.CreateType();
        user.CreateType();
        root.CreateType();
        return (assembly, user);
    }

    /// <summary>
    /// The value of an attribute whose one argument is a type, written as the metadata writes a
    /// typeof argument: the prolog, the type's name as a serialized string, and no named arguments.
    /// </summary>
    private static byte[] TypeArgument(string typeName)
    {
        var name = Encoding.UTF8.GetBytes(typeName);
        if (name.Length > 0x7F)
            throw new ArgumentException("The name is longer than one length byte holds.", nameof(typeName));
        return [0x01, 0x00, (byte)name.Length, .. name, 0x00, 0x00];
    }
}
