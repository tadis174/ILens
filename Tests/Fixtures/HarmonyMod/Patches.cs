using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using HarmonyLib;

namespace HarmonyMod
{
    // Target types — what the patches below bind to. A handful of members each
    // so the same-name overloads and private/internal cases are reachable.
    public class TargetA
    {
        public int viewHeight;
        public string Prop { get; set; }
        public TargetA() { }
        public TargetA(int capacity) { viewHeight = capacity; }
        public TargetA(List<int> items) { viewHeight = items.Count; }
        public TargetA(int? limit) { viewHeight = limit ?? 0; }
        public TargetA(int[,] grid) { viewHeight = grid.Length; }
        public TargetA(List<int>[] lists) { viewHeight = lists.Length; }
        public int Compute(string s) => s.Length;
        public int Compute(int n) => n * 2;
        private void PrivateMethod() { }
    }

    public class TargetB
    {
        internal int counter;
        public void SomeMethod() { }
    }

    public class TargetC
    {
        public class Nested
        {
            public Nested() { }
        }
    }

    // Two types that differ only in their generic arity, as Task and Task<TResult> do.
    public class TargetD
    {
        public void Run() { }
    }

    public class TargetD<T>
    {
        public void Run() { }
    }

    // An event, an operator and a finalizer, whose methods Harmony 2.4 patches by their kind.
    public class TargetE
    {
        public event EventHandler Changed { add { } remove { } }
        public static TargetE operator +(TargetE left, TargetE right) => left;
        ~TargetE() { }
    }

    // Indexers, which Harmony finds by their index types alone, under C#'s default name and renamed.
    public class TargetF
    {
        public int this[int index] => index;
        public string this[string key] { get => key; set { } }
    }

    public class TargetG
    {
        [System.Runtime.CompilerServices.IndexerName("Cell")]
        public int this[int row, int column] { get => row; set { } }
    }

    // Members that the lookups in patterns 19 to 22 and 51 and the attributes of pattern 48 reach: a
    // static constructor, a property, an iterator and an async method, whose state machines Harmony
    // patches, and a class nested in another.
    public class TargetH
    {
        static TargetH() { }
        public int Size { get; set; }
        public IEnumerable<int> Items() { yield return Size; }
        public async Task Refresh() => await Task.Yield();

        public class Inner
        {
            public void Describe() { }
        }
    }

    // Fields that only the field lookups in patterns 26 to 30 reach: an instance field, two static
    // ones and a struct's, and a method to patch.
    public class TargetI
    {
        public int count;
        public static string label;
        public static int total;
        public void Run() { }
    }

    public struct TargetJ
    {
        public int x;
    }

    // Pattern 1: typed attribute with nameof — TypedAttribute resolution.
    [HarmonyPatch(typeof(TargetB), nameof(TargetB.SomeMethod))]
    static class TypedNamedPatch
    {
        static void Prefix() { }
    }

    // Pattern 2: string-targeted (private member) — StringTargeted resolution.
    [HarmonyPatch(typeof(TargetA), "PrivateMethod")]
    static class StringTargetedPatch
    {
        static void Postfix() { }
    }

    // A getter patch names the property; the patched method is its get accessor.
    [HarmonyPatch(typeof(TargetA), nameof(TargetA.Prop), MethodType.Getter)]
    static class GetterPatch
    {
        static void Prefix() { }
    }

    // Pattern 3: overload-pinned with param-types array.
    [HarmonyPatch(typeof(TargetA), nameof(TargetA.Compute), new Type[] { typeof(string) })]
    static class OverloadPinnedPatch
    {
        static void Prefix(string s) { }
    }

    // Pattern 3b: constructor target via MethodType.Constructor, overload-pinned.
    [HarmonyPatch(typeof(TargetA), MethodType.Constructor, new Type[] { typeof(int) })]
    static class ConstructorPatch
    {
        static void Prefix() { }
    }

    // Pattern 3c: parameterless constructor target via MethodType.Constructor.
    [HarmonyPatch(typeof(TargetB), MethodType.Constructor)]
    static class ParameterlessConstructorPatch
    {
        static void Prefix() { }
    }

    // Pattern 3d: constructor target with a generic param type — exercises the
    // reflection-name un-mangling in LenientAttributeReader.NormalizeTypeName.
    [HarmonyPatch(typeof(TargetA), MethodType.Constructor, new Type[] { typeof(List<int>) })]
    static class GenericConstructorPatch
    {
        static void Prefix() { }
    }

    // Pattern 3d with a nullable param type, which the blob names
    // System.Nullable`1[[System.Int32, ...]] and the scanner writes as int?.
    [HarmonyPatch(typeof(TargetA), MethodType.Constructor, new Type[] { typeof(int?) })]
    static class NullableConstructorPatch
    {
        static void Prefix() { }
    }

    // Pattern 3d with a two-dimensional array param type, which the blob names
    // System.Int32[,] and the scanner writes as C# does, int[,].
    [HarmonyPatch(typeof(TargetA), MethodType.Constructor, new Type[] { typeof(int[,]) })]
    static class GridConstructorPatch
    {
        static void Prefix() { }
    }

    // Pattern 3d with an array of a generic type, whose brackets the blob writes after
    // the type arguments: System.Collections.Generic.List`1[[System.Int32, ...]][].
    [HarmonyPatch(typeof(TargetA), MethodType.Constructor, new Type[] { typeof(List<int>[]) })]
    static class ListArrayConstructorPatch
    {
        static void Prefix() { }
    }

    // Pattern 3e: constructor target on a nested type. The lenient decode reads the
    // target from the raw attribute blob, which names it the reflection way,
    // TargetC+Nested.
    [HarmonyPatch(typeof(TargetC.Nested), MethodType.Constructor)]
    static class NestedConstructorPatch
    {
        static void Prefix() { }
    }

    // Pattern 3f: a constructed generic target whose name a non-generic type shares.
    [HarmonyPatch(typeof(TargetD<int>), nameof(TargetD<int>.Run))]
    static class GenericTargetPatch
    {
        static void Prefix() { }
    }

    // Pattern 4: TargetMethod() with a statically determinable body.
    [HarmonyPatch]
    static class TargetMethodPatch
    {
        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(TargetA), "Compute", new Type[] { typeof(int) });
        }

        static void Prefix() { }
    }

    // Pattern 5: TargetMethods() enumerating multiple targets statically.
    [HarmonyPatch]
    static class TargetMethodsPatch
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(TargetA), "Compute", new Type[] { typeof(string) });
            yield return AccessTools.Method(typeof(TargetB), "SomeMethod");
        }

        static void Prefix() { }
    }

    // Pattern 6: TargetMethod() whose body depends on runtime state — the scanner
    // can't resolve it statically and should surface resolutionKind=DynamicTargetMethod.
    [HarmonyPatch]
    static class DynamicTargetPatch
    {
        static MethodBase TargetMethod()
        {
            var rng = new Random();
            return rng.Next() % 2 == 0
                ? AccessTools.Method(typeof(TargetA), "Compute", new Type[] { typeof(int) })
                : AccessTools.Method(typeof(TargetB), "SomeMethod");
        }

        static void Prefix() { }
    }

    // Pattern 7: AccessTools.Field — a reflective field access. The static
    // initializer is the call site; the scanner walks IL inside every method.
    [HarmonyPatch(typeof(TargetA), nameof(TargetA.Compute), new Type[] { typeof(int) })]
    static class FieldAccessPatch
    {
        static FieldInfo viewHeightField = AccessTools.Field(typeof(TargetA), "viewHeight");
        // A field of a type from another assembly, which the decompiled code names by its short name.
        static FieldInfo chunkField = AccessTools.Field(typeof(StringBuilder), "m_ChunkChars");

        static void Prefix(TargetA __instance)
        {
            var v = viewHeightField.GetValue(__instance);
        }
    }

    // Pattern 8: AccessTools.FieldRefAccess — generic-method reflective access.
    [HarmonyPatch(typeof(TargetB), nameof(TargetB.SomeMethod))]
    static class FieldRefAccessPatch
    {
        static AccessTools.FieldRef<TargetB, int> counterRef =
            AccessTools.FieldRefAccess<TargetB, int>("counter");

        static void Prefix(TargetB __instance)
        {
            counterRef(__instance)++;
        }
    }

    // Pattern 9: Traverse.Create(...).Field — chained reflective access.
    [HarmonyPatch(typeof(TargetA), nameof(TargetA.Compute), new Type[] { typeof(int) })]
    static class TraverseFieldPatch
    {
        static void Prefix(TargetA __instance)
        {
            var s = Traverse.Create(__instance).Field("Prop").GetValue<string>();
        }
    }

    // Pattern 10: patch methods named freely and marked by attribute, each completing the
    // class's target with its own member and overload. A MethodType argument on a method's
    // attribute empties ILSpy's own decoding, as it does on a class's.
    [HarmonyPatch(typeof(TargetA))]
    static class MarkedMethodsPatch
    {
        [HarmonyPrefix]
        [HarmonyPatch(nameof(TargetA.Compute), new Type[] { typeof(string) })]
        static void BeforeComputeString() { }

        [HarmonyPostfix]
        [HarmonyPatch(nameof(TargetA.Compute), new Type[] { typeof(int) })]
        static void AfterComputeInt() { }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(TargetA.Prop), MethodType.Setter)]
        static void BeforeSetProp() { }
    }

    // Pattern 11: the other kinds, marked by attribute under names of their own. The reverse
    // patch's own [HarmonyPatch] replaces the class's target rather than completing it.
    [HarmonyPatch(typeof(TargetB), nameof(TargetB.SomeMethod))]
    static class MarkedKindsPatch
    {
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions) => instructions;

        [HarmonyFinalizer]
        static Exception Swallow(Exception __exception) => null;

        [HarmonyReversePatch]
        [HarmonyPatch(typeof(TargetA), nameof(TargetA.Compute), new Type[] { typeof(int) })]
        static int OriginalCompute(TargetA instance, int n) => 0;
    }

    // Pattern 12: TargetMethod and TargetMethods found by their attributes rather than their names.
    [HarmonyPatch]
    static class MarkedTargetMethodPatch
    {
        [HarmonyTargetMethod]
        static MethodBase Target() => AccessTools.Method(typeof(TargetA), "PrivateMethod");

        static void Postfix() { }
    }

    [HarmonyPatch]
    static class MarkedTargetMethodsPatch
    {
        [HarmonyTargetMethods]
        static IEnumerable<MethodBase> Targets()
        {
            yield return AccessTools.Method(typeof(TargetB), "SomeMethod");
        }

        static void Postfix() { }
    }

    // Pattern 13: targets named on the patch methods alone, by a class carrying only a category
    // and by one applied by hand without any class attribute.
    [HarmonyPatchCategory("Optional")]
    static class CategorizedPatch
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(TargetB), nameof(TargetB.SomeMethod))]
        static void AfterSomeMethod() { }
    }

    static class MethodTargetPatch
    {
        [HarmonyPatch(typeof(TargetD), nameof(TargetD.Run))]
        static void Prefix() { }
    }

    // Pattern 14: TargetMethod looking its target up by reflection, by name alone.
    [HarmonyPatch]
    static class ReflectionTargetPatch
    {
        static MethodBase TargetMethod() =>
            typeof(TargetA).GetMethod("PrivateMethod", BindingFlags.Instance | BindingFlags.NonPublic);

        static void Postfix() { }
    }

    // Pattern 15: TargetMethod and TargetMethods that search a type's methods for a name and pick
    // the overload by tests of their own. A test on a parameter's name names no method.
    [HarmonyPatch]
    static class MethodSearchPatch
    {
        static MethodBase TargetMethod()
        {
            foreach (var method in typeof(TargetA).GetMethods(BindingFlags.Instance | BindingFlags.Public))
                if (method.Name == nameof(TargetA.Compute) && method.GetParameters()[0].Name == "n")
                    return method;
            return null;
        }

        static void Prefix() { }
    }

    [HarmonyPatch]
    static class MethodSearchesPatch
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var method in typeof(TargetB).GetMethods())
                if (method.Name == nameof(TargetB.SomeMethod))
                    yield return method;
        }

        static void Postfix() { }
    }

    // Pattern 16: the target class named by a string, as for a class the mod cannot reference,
    // and a nested one in the reflection form Harmony looks it up by.
    [HarmonyPatch("HarmonyMod.TargetB", "SomeMethod", MethodType.Normal)]
    static class TextTargetPatch
    {
        static void Prefix() { }
    }

    [HarmonyPatch("HarmonyMod.TargetC+Nested", null, MethodType.Constructor)]
    static class TextConstructorPatch
    {
        static void Prefix() { }
    }

    // Pattern 17: the kinds of method Harmony 2.4 added, an event's add or remove accessor, an
    // operator and a finalizer.
    [HarmonyPatch(typeof(TargetE), nameof(TargetE.Changed), MethodType.EventAdd)]
    static class EventAdderPatch
    {
        static void Prefix() { }
    }

    [HarmonyPatch(typeof(TargetE), nameof(TargetE.Changed), MethodType.EventRemove)]
    static class EventRemoverPatch
    {
        static void Prefix() { }
    }

    [HarmonyPatch(typeof(TargetE), MethodType.OperatorAddition, new Type[] { typeof(TargetE), typeof(TargetE) })]
    static class OperatorPatch
    {
        static void Prefix() { }
    }

    [HarmonyPatch(typeof(TargetE), MethodType.Finalizer)]
    static class FinalizeMethodPatch
    {
        static void Prefix() { }
    }

    // Pattern 18: getter and setter patches that name no property, which Harmony binds to the
    // accessor of the indexer with the index types named, or of the only indexer, in the scanned
    // assembly and in another.
    [HarmonyPatch(typeof(TargetF), MethodType.Getter, new Type[] { typeof(int) })]
    static class IndexerGetterPatch
    {
        static void Prefix() { }
    }

    [HarmonyPatch(typeof(TargetF), MethodType.Setter, new Type[] { typeof(string) })]
    static class IndexerSetterPatch
    {
        static void Prefix() { }
    }

    [HarmonyPatch(typeof(TargetG), MethodType.Getter)]
    static class RenamedIndexerGetterPatch
    {
        static void Prefix() { }
    }

    [HarmonyPatch(typeof(TargetG), MethodType.Setter, new Type[] { typeof(int), typeof(int) })]
    static class RenamedIndexerSetterPatch
    {
        static void Prefix() { }
    }

    [HarmonyPatch(typeof(List<int>), MethodType.Getter, new Type[] { typeof(int) })]
    static class ExternalIndexerGetterPatch
    {
        static void Prefix() { }
    }

    [HarmonyPatch(typeof(List<int>), MethodType.Setter, new Type[] { typeof(int) })]
    static class ExternalIndexerSetterPatch
    {
        static void Prefix() { }
    }

    // Pattern 19: the other AccessTools lookups that name the type with typeof and the member by
    // its name, in AccessTools' spelling and in Harmony's extension spelling. A constructor lookup
    // without parameter types finds the parameterless constructor, and an empty array pins the
    // parameterless overload.
    [HarmonyPatch]
    static class OtherLookupsPatch
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.DeclaredMethod(typeof(TargetB), nameof(TargetB.SomeMethod));
            yield return typeof(TargetA).Method(nameof(TargetA.Compute), new[] { typeof(string) });
            yield return typeof(TargetH).DeclaredMethod(nameof(TargetH.Items));
            yield return AccessTools.PropertyGetter(typeof(TargetA), nameof(TargetA.Prop));
            yield return typeof(TargetH).DeclaredPropertySetter(nameof(TargetH.Size));
            yield return AccessTools.Constructor(typeof(TargetA));
            yield return AccessTools.DeclaredConstructor(typeof(TargetA), new[] { typeof(List<int>) });
            yield return AccessTools.Constructor(typeof(TargetH), searchForStatic: true);
            yield return AccessTools.Method(typeof(TargetA), "PrivateMethod", Type.EmptyTypes);
        }

        static void Postfix() { }
    }

    // Pattern 20: lookups that name the type as text, as for a class the mod cannot reference: the
    // "Namespace.Type:Member" form, and AccessTools.TypeByName with '+' for a nested class.
    [HarmonyPatch]
    static class TextLookupsPatch
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method("HarmonyMod.TargetA:Compute", new[] { typeof(int) });
            yield return AccessTools.DeclaredPropertyGetter("HarmonyMod.TargetH:Size");
            yield return AccessTools.Method(AccessTools.TypeByName("HarmonyMod.TargetH+Inner"), "Describe");
        }

        static void Prefix() { }
    }

    // Pattern 21: a type held in a local, for the type looked in and for a parameter type, and a
    // target held in a field that TargetMethod returns, assigned in Prepare or by an initializer.
    [HarmonyPatch]
    static class LocalTypeLookupsPatch
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            var target = typeof(TargetA);
            var text = typeof(string);
            yield return AccessTools.Method(target, nameof(TargetA.Compute), new[] { text });
            yield return AccessTools.PropertySetter(target, nameof(TargetA.Prop));
            var inner = AccessTools.TypeByName("HarmonyMod.TargetH+Inner");
            yield return inner.GetMethod(nameof(TargetH.Inner.Describe));
        }

        static void Postfix() { }
    }

    [HarmonyPatch]
    static class PreparedTargetPatch
    {
        static MethodBase target;

        static bool Prepare()
        {
            var type = AccessTools.TypeByName("HarmonyMod.TargetB");
            target = AccessTools.Method(type, nameof(TargetB.SomeMethod));
            return target != null;
        }

        static MethodBase TargetMethod() => target;

        static void Prefix() { }
    }

    [HarmonyPatch]
    static class InitializedTargetPatch
    {
        static readonly MethodBase target = AccessTools.Method("HarmonyMod.TargetA:PrivateMethod");

        static MethodBase TargetMethod() => target;

        static void Postfix() { }
    }

    // Pattern 22: lookups wrapped to patch the state machine of an iterator or an async method,
    // which Harmony binds as for a [HarmonyPatch] with MethodType.Enumerator or MethodType.Async.
    [HarmonyPatch]
    static class StateMachineLookupsPatch
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(TargetH), nameof(TargetH.Items)));
            yield return AccessTools.AsyncMoveNext(typeof(TargetH).GetMethod(nameof(TargetH.Refresh)));
        }

        static void Prefix() { }
    }

    // Pattern 23: a lookup the scanner cannot read beside one it can, in TargetMethods and in
    // TargetMethod. The search tests a name by its start, which names no method.
    [HarmonyPatch]
    static class PartlyReadTargetsPatch
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(TargetB), nameof(TargetB.SomeMethod));
            yield return typeof(TargetA).GetMethods().First(method => method.Name.StartsWith("Comp"));
        }

        static void Postfix() { }
    }

    [HarmonyPatch]
    static class PartlyReadTargetPatch
    {
        static MethodBase TargetMethod() => Environment.ProcessorCount > 1
            ? AccessTools.Method(typeof(TargetB), nameof(TargetB.SomeMethod))
            : typeof(TargetA).GetMethods().First(method => method.Name.StartsWith("Comp"));

        static void Prefix() { }
    }

    // Pattern 24: the same in Harmony's extension spelling, which ILSpy writes as an extension
    // call only when it finds HarmonyLib, so the scanner meets both forms.
    [HarmonyPatch]
    static class PartlyReadExtensionsPatch
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return typeof(TargetB).Method(nameof(TargetB.SomeMethod));
            yield return typeof(TargetA).FirstMethod(method => method.Name.StartsWith("Comp"));
        }

        static void Postfix() { }
    }

    // Pattern 25: a Traverse receiver that ILSpy casts twice when it cannot load HarmonyLib, to
    // object and to HarmonyAttribute, the class declaring the field read, and not at all when it
    // can, as it does in a mod whose game assemblies it cannot load. The object's class is the one
    // the decompiler gives it, the type of the field info.
    [HarmonyPatch(typeof(TargetB), nameof(TargetB.SomeMethod))]
    static class CastReceiverPatch
    {
        static void Prefix()
        {
            var patch = new HarmonyPatch(typeof(TargetA));
            Traverse.Create(patch.info).Field("method");
        }
    }

    // Pattern 26: field lookups outside any patch class, kept in static fields as many mods keep
    // them, in each spelling the scanner reads: a class by typeof, as text or through TypeByName;
    // DeclaredField, which looks in the class alone; Harmony's extension spelling; the field-ref
    // lookups; and .NET's own GetField.
    static class FieldHolder
    {
        internal static FieldInfo byType = AccessTools.Field(typeof(TargetI), "count");
        internal static FieldInfo byText = AccessTools.Field("HarmonyMod.TargetI:label");
        internal static FieldInfo byName = AccessTools.Field(AccessTools.TypeByName("HarmonyMod.TargetI"), "total");
        internal static FieldInfo declared = AccessTools.DeclaredField(typeof(TargetI), "count");
        internal static FieldInfo extension = typeof(TargetI).Field("label");
        internal static FieldInfo declaredExtension = typeof(TargetI).DeclaredField("total");
        internal static AccessTools.FieldRef<TargetI, int> byArguments = AccessTools.FieldRefAccess<TargetI, int>("count");
        internal static AccessTools.FieldRef<object, int> byTypeRef = AccessTools.FieldRefAccess<int>(typeof(TargetI), "count");
        internal static AccessTools.FieldRef<object, int> byTextRef = AccessTools.FieldRefAccess<int>("HarmonyMod.TargetI:count");
        internal static FieldInfo reflection = typeof(TargetI).GetField("count", BindingFlags.Instance | BindingFlags.Public);
    }

    // Pattern 27: lookups in the methods of a class that is no patch class: the ref lookups only a
    // method can call, a class held in a local, an iterator, and each way Traverse finds the object
    // it reads, by the decompiler's type of it, from a class, through a local, and in the generic
    // Field<T>.
    class FieldUser
    {
        TargetI target = new TargetI();

        void Use(TargetI instance, TargetJ value)
        {
            AccessTools.StaticFieldRefAccess<TargetI, int>("total") = 1;
            AccessTools.StaticFieldRefAccess<string>(typeof(TargetI), "label") = "";
            AccessTools.StructFieldRefAccess<TargetJ, int>(ref value, "x") = 2;
            AccessTools.FieldRefAccess<TargetI, int>(instance, "count") = 3;
            var type = typeof(TargetI);
            AccessTools.Field(type, "count");
            type.GetField("label");
            Traverse.Create(instance).Field("count");
            Traverse.Create(target).Field<int>("count");
            Traverse.Create(typeof(TargetI)).Field("label");
            Traverse.Create<TargetI>().Field("total");
            Traverse.CreateWithType("HarmonyMod.TargetI").Field("label");
            var traverse = Traverse.Create(instance);
            traverse.Field("count");
            traverse.Field<int>("count");
            // A generic class from another assembly, which keeps its name when that assembly cannot load.
            AccessTools.FieldRefAccess<Traverse<int>, Traverse>("traverse");
        }

        IEnumerable<FieldInfo> Fields()
        {
            yield return AccessTools.Field(typeof(TargetI), "count");
        }

        // An object cast with as to a class from another assembly, which the decompiler writes as
        // (x is T) ? x : null when it cannot load that assembly.
        void Inspect(Attribute attribute)
        {
            Traverse.Create(attribute as HarmonyPatch).Field("info");
        }
    }

    // Pattern 28: lookups the scanner reads only in part, with '?' for what it cannot read: a class
    // from a property, or from a parameter no call passes, also beside the value the body assigns it
    // in a branch, a name from a parameter, an object typed as object, a Traverse reached through
    // another Field call, a field by its index, and the enumerations of a class's fields. A lookup
    // that takes a FieldInfo, called or passed as a method group, prints nothing: its field comes from
    // the lookup that found the FieldInfo.
    static class PartlyReadFields
    {
        static void Read(MethodBase method, string name, object value)
        {
            AccessTools.Field(method.DeclaringType, "count");
            AccessTools.Field(typeof(TargetI), name);
            Traverse.Create(value).Field("count");
            Traverse.Create(new TargetI()).Field("count").Field("x");
            AccessTools.DeclaredField(typeof(TargetI), 0);
            typeof(TargetJ).GetFields();
            AccessTools.GetDeclaredFields(typeof(TargetI));
            AccessTools.GetFieldNames(value);
            AccessTools.FieldRefAccess<TargetI, int>(AccessTools.Field(typeof(TargetI), "count"));
            typeof(TargetI).GetFields().Select(AccessTools.FieldRefAccess<TargetI, int>).ToList();
        }

        static void Reassign(Type type)
        {
            if (type == null)
                type = typeof(TargetI);
            AccessTools.Field(type, "count");
        }
    }

    // Pattern 29: lookups in a patch class's property getter and in a lambda, which the decompiler
    // shows inside the method it is written in.
    [HarmonyPatch(typeof(TargetI), nameof(TargetI.Run))]
    static class AccessorFieldPatch
    {
        static FieldInfo Count => AccessTools.Field(typeof(TargetI), "count");

        static void Postfix()
        {
            Action read = () => AccessTools.Field(typeof(TargetI), "label").GetValue(null);
            read();
        }
    }

    // Pattern 30: fields Harmony hands a patch method through parameters named ___ and the field,
    // read, written through ref, by index, and on a patch whose target the scanner cannot read.
    [HarmonyPatch(typeof(TargetI), nameof(TargetI.Run))]
    static class InjectedFieldsPatch
    {
        static void Prefix(int ___count, ref string ___label) { }
        static void Postfix(int ___0) { }
    }

    [HarmonyPatch]
    static class UnreadInjectedFieldPatch
    {
        static MethodBase TargetMethod() =>
            typeof(TargetI).GetMethods().First(method => method.Name.StartsWith("R"));

        static void Prefix(int ___count) { }
    }

    // Members that only the patches a mod applies by calling Harmony.Patch reach, patterns 31 to 38.
    public class TargetK
    {
        public int count;
        public void Run() { }
        public void Stop() { }
        public int Size => count;

        public class Part
        {
            public void Move() { }
        }
    }

    // Pattern 31: patches applied by calling Harmony.Patch, as prefix, postfix, transpiler and
    // finalizer by the argument's position, with arguments named or left out, the patch class held in
    // a local, a HarmonyMethod made from a class and a name or from a method, a MethodInfo converted
    // implicitly, and a lambda's method through SymbolExtensions. A ___name parameter of a prefix
    // applied this way names a field of the class it patches.
    static class ManualPatches
    {
        static void BeforeRun(int ___count) { }
        static void AfterStop() { }
        static IEnumerable<CodeInstruction> RewriteSize(IEnumerable<CodeInstruction> instructions) => instructions;
        static Exception CatchSize(Exception __exception) => null;

        public static void Apply(Harmony harmony)
        {
            var type = typeof(ManualPatches);
            harmony.Patch(AccessTools.Method(typeof(TargetK), nameof(TargetK.Run)), new HarmonyMethod(type, nameof(BeforeRun)));
            harmony.Patch(AccessTools.Method(typeof(TargetK), nameof(TargetK.Stop)), postfix: new HarmonyMethod(typeof(ManualPatches), nameof(AfterStop)));
            harmony.Patch(AccessTools.PropertyGetter(typeof(TargetK), nameof(TargetK.Size)),
                transpiler: new HarmonyMethod(AccessTools.Method(type, nameof(RewriteSize))),
                finalizer: new HarmonyMethod(type.GetMethod(nameof(CatchSize), BindingFlags.Static | BindingFlags.NonPublic)));
            harmony.Patch(SymbolExtensions.GetMethodInfo(() => new TargetK().Stop()), postfix: AccessTools.Method(type, nameof(AfterStop)));
        }

        // Pattern 32: a local assigned twice, which each call reads where it stands, and an empty
        // array from Array.Empty, which pins the parameterless overload.
        public static void Reassign(Harmony harmony)
        {
            var original = AccessTools.Method(typeof(TargetK), nameof(TargetK.Run));
            harmony.Patch(original, postfix: new HarmonyMethod(typeof(ManualPatches), nameof(AfterStop)));
            original = AccessTools.Method(typeof(TargetK), nameof(TargetK.Stop), Array.Empty<Type>());
            harmony.Patch(original, new HarmonyMethod(typeof(ManualPatches), nameof(BeforeRun)));
        }
    }

    // Pattern 33: the class the calling object is, from GetType(), and classes found by name: nested
    // through AccessTools.Inner or Type.GetNestedType, and by text through Type.GetType or
    // Assembly.GetType, behind ?. as well, with a property getter through its PropertyInfo.
    class InstancePatcher
    {
        static void AfterAny() { }

        void Apply(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(AccessTools.Inner(typeof(TargetK), "Part"), "Move"), postfix: new HarmonyMethod(GetType(), nameof(AfterAny)));
            harmony.Patch(typeof(TargetK).GetNestedType("Part").GetMethod("Move"), postfix: new HarmonyMethod(GetType(), nameof(AfterAny)));
            harmony.Patch(Type.GetType("HarmonyMod.TargetK, HarmonyMod")?.GetMethod("Stop"), postfix: new HarmonyMethod(GetType(), nameof(AfterAny)));
            harmony.Patch(typeof(TargetK).Assembly.GetType("HarmonyMod.TargetK").GetMethod("Run"), postfix: new HarmonyMethod(GetType(), nameof(AfterAny)));
            harmony.Patch(AccessTools.Property(typeof(TargetK), nameof(TargetK.Size)).GetGetMethod(), postfix: new HarmonyMethod(GetType(), nameof(AfterAny)));
        }
    }

    // Pattern 34: a helper that passes its parameters to Harmony.Patch, read at each call of it, with
    // a parameter left at its default.
    static class PatchHelper
    {
        static void BeforeAny() { }
        static void AfterAny() { }

        public static void Apply(Harmony harmony)
        {
            Patch(harmony, AccessTools.Method(typeof(TargetK), nameof(TargetK.Run)), prefix: AccessTools.Method(typeof(PatchHelper), nameof(BeforeAny)));
            Patch(harmony, AccessTools.Method(typeof(TargetK), nameof(TargetK.Stop)), postfix: AccessTools.Method(typeof(PatchHelper), nameof(AfterAny)));
        }

        static void Patch(Harmony harmony, MethodBase original, MethodInfo prefix = null, MethodInfo postfix = null) =>
            harmony.Patch(original, prefix == null ? null : new HarmonyMethod(prefix), postfix == null ? null : new HarmonyMethod(postfix));
    }

    // Pattern 35: a patch described by an object whose constructor keeps the target and the patch
    // methods in readonly fields, built through factories in an iterator of another class and applied
    // by a method of its own.
    readonly struct PatchSpec
    {
        private readonly MethodBase target;
        private readonly MethodInfo prefix;
        private readonly MethodInfo postfix;

        public PatchSpec(MethodBase target, MethodInfo prefix = null, MethodInfo postfix = null)
        {
            this.target = target;
            this.prefix = prefix;
            this.postfix = postfix;
        }

        public static PatchSpec Before(MethodBase target, MethodInfo prefix) => new PatchSpec(target, prefix);
        public static PatchSpec After(MethodBase target, MethodInfo postfix) => new PatchSpec(target, null, postfix);

        public void Apply(Harmony harmony) =>
            harmony.Patch(target, prefix == null ? null : new HarmonyMethod(prefix), postfix == null ? null : new HarmonyMethod(postfix));
    }

    class SpecPatches
    {
        static void BeforeRun() { }
        static void AfterStop() { }

        IEnumerable<PatchSpec> GetPatches()
        {
            yield return PatchSpec.Before(AccessTools.Method(typeof(TargetK), nameof(TargetK.Run)), AccessTools.Method(GetType(), nameof(BeforeRun)));
            yield return PatchSpec.After(AccessTools.Method(typeof(TargetK), nameof(TargetK.Stop)), AccessTools.Method(GetType(), nameof(AfterStop)));
        }
    }

    // Pattern 36: a patch method an own extension method makes from a name, and a target taken from
    // another class's TargetMethod, which the TargetMethod rule lists as well.
    static class OwnMethodPatches
    {
        static void AfterAny() { }

        static HarmonyMethod AsPatch(this string name) => new HarmonyMethod(typeof(OwnMethodPatches), name);

        public static void Apply(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(TargetK), nameof(TargetK.Run)), postfix: nameof(AfterAny).AsPatch());
            harmony.Patch(HandAppliedTargetPatch.TargetMethod(), postfix: new HarmonyMethod(typeof(HandAppliedTargetPatch), nameof(HandAppliedTargetPatch.Postfix)));
        }
    }

    static class HandAppliedTargetPatch
    {
        [HarmonyTargetMethod]
        internal static MethodBase TargetMethod() => AccessTools.Method(typeof(TargetK), nameof(TargetK.Stop));

        internal static void Postfix() { }
    }

    // Pattern 37: loops: a search of one class's methods by an exact name, which names no overload; a
    // list of "Type:Method" names the code writes out, a projection of names, and a list a call that
    // takes it may add to, whose names stand beside '?'; and loops the scanner cannot read, over a
    // class's subclasses and over data known only at run time.
    static class LoopPatches
    {
        static void AfterAny() { }

        static readonly List<string> Targets = new List<string> { "HarmonyMod.TargetI:Run", "HarmonyMod.TargetD:Run" };
        static readonly List<string> Extended = new List<string> { "HarmonyMod.TargetB:SomeMethod" };

        static void Extend(List<string> names) => names.Add("HarmonyMod.TargetC:ToString");

        public static void Apply(Harmony harmony, IEnumerable<MethodBase> found)
        {
            var postfix = new HarmonyMethod(typeof(LoopPatches), nameof(AfterAny));
            foreach (var method in typeof(TargetA).GetMethods().Where(candidate => candidate.Name == nameof(TargetA.Compute) && candidate.GetParameters().Length == 1))
                harmony.Patch(method, postfix: postfix);
            foreach (var name in Targets)
                harmony.Patch(AccessTools.Method(name), postfix: postfix);
            Extend(Extended);
            foreach (var name in Extended)
                harmony.Patch(AccessTools.Method(name), postfix: postfix);
            foreach (var original in new[] { "Run", "Stop" }.Select(name => AccessTools.Method(typeof(TargetK), name)))
                harmony.Patch(original, postfix: postfix);
            foreach (var type in typeof(TargetK).Assembly.GetTypes().Where(candidate => candidate.IsSubclassOf(typeof(TargetK))))
                harmony.Patch(AccessTools.Method(type, "Run"), postfix: postfix);
            foreach (var original in found)
                harmony.Patch(original, postfix: postfix);
        }
    }

    // Pattern 38: a field lookup whose class a helper's caller passes, read at each call.
    static class FieldHelper
    {
        static FieldInfo Count(Type type) => AccessTools.Field(type, "count");

        internal static FieldInfo targetCount = Count(typeof(TargetK));
        internal static FieldInfo otherCount = Count(typeof(TargetI));
    }

    // A generic method, which only pattern 39 reaches.
    public class TargetL
    {
        public static T Parse<T>(string text) where T : struct => default;
    }

    // Pattern 39: a generic method made for one type argument with MakeGenericMethod, which is what
    // Harmony patches; the target names the method with that type argument, found by a search in a
    // TargetMethod and by AccessTools in a Harmony.Patch call.
    [HarmonyPatch]
    static class GenericSearchPatch
    {
        static MethodBase TargetMethod()
        {
            foreach (var method in typeof(TargetL).GetMethods(BindingFlags.Static | BindingFlags.Public))
                if (method.Name == nameof(TargetL.Parse) && method.IsGenericMethodDefinition)
                    return method.MakeGenericMethod(typeof(int));
            return null;
        }

        static void Postfix() { }
    }

    static class GenericPatchCall
    {
        static void AfterParse() { }

        public static void Apply(Harmony harmony) =>
            harmony.Patch(AccessTools.Method(typeof(TargetL), nameof(TargetL.Parse)).MakeGenericMethod(typeof(int)),
                postfix: new HarmonyMethod(typeof(GenericPatchCall), nameof(AfterParse)));
    }

    // Pattern 40: methods, properties, events and constructors looked up to call them, all of
    // classes of another assembly, in each form the scanner reads: AccessTools' lookups by typeof,
    // as text and in Harmony's extension spelling, with and without parameter types; an accessor
    // taken from a property lookup; the indexer lookups, which name index types and no name; .NET's
    // own lookups, an event's among them, with an accessor taken right at the call and one taken
    // from a local; a delegate made from a name; the method a lambda calls; Traverse's, on an
    // object and on a class; and the enumerations and searches, which name the class alone.
    static class MemberLookups
    {
        static void Use(StringBuilder builder)
        {
            AccessTools.Method(typeof(StringBuilder), "Append", new[] { typeof(string) });
            AccessTools.Method("System.Text.StringBuilder:ToString");
            typeof(StringBuilder).Method("Clear");
            AccessTools.DeclaredMethod(typeof(StringBuilder), "ExpandByABlock");
            typeof(StringBuilder).DeclaredMethod("EnsureCapacity", new[] { typeof(int) });
            AccessTools.PropertyGetter(typeof(StringBuilder), "Capacity");
            AccessTools.PropertySetter(typeof(StringBuilder), "Capacity");
            AccessTools.DeclaredPropertyGetter(typeof(StringBuilder), "MaxCapacity");
            AccessTools.DeclaredPropertySetter("System.Text.StringBuilder:Length");
            AccessTools.Property(typeof(StringBuilder), "Length");
            AccessTools.DeclaredProperty(typeof(Version), "Major");
            AccessTools.Property(typeof(Version), "Minor").GetGetMethod();
            var setter = AccessTools.Property(typeof(StringBuilder), "Capacity").SetMethod;
            AccessTools.Constructor(typeof(StringBuilder), new[] { typeof(int) });
            AccessTools.Constructor(typeof(Version));
            AccessTools.DeclaredConstructor(typeof(StringBuilder), new[] { typeof(string) });

            AccessTools.IndexerGetter(typeof(List<int>), new[] { typeof(int) });
            AccessTools.IndexerSetter(typeof(List<int>), new[] { typeof(int) });
            AccessTools.DeclaredIndexerGetter(typeof(List<int>));
            typeof(List<int>).DeclaredIndexerSetter(null);
            AccessTools.Indexer(typeof(List<int>), new[] { typeof(int) });
            AccessTools.DeclaredIndexer(typeof(List<int>));

            typeof(StringBuilder).GetMethod("Append", new[] { typeof(char) });
            typeof(StringBuilder).GetMethod("Remove");
            typeof(StringBuilder).GetProperty("MaxCapacity");
            typeof(Version).GetProperty("Build").GetGetMethod();
            typeof(StringBuilder).GetConstructor(new[] { typeof(string) });
            typeof(Version).GetConstructor(Type.EmptyTypes);
            typeof(AppDomain).GetEvent("AssemblyLoad");
            typeof(Console).GetEvent("CancelKeyPress", BindingFlags.Static | BindingFlags.Public);
            typeof(AppDomain).GetEvent("UnhandledException").GetAddMethod();
            var remover = typeof(AppDomain).GetEvent("ProcessExit").RemoveMethod;
            var unload = typeof(AppDomain).GetEvent("DomainUnload");
            unload.GetAddMethod();

            AccessTools.MethodDelegate<Func<StringBuilder, string>>("System.Text.StringBuilder:ToString");
            SymbolExtensions.GetMethodInfo(() => new StringBuilder().Clear());

            Traverse.Create(builder).Method("FormatError");
            Traverse.Create(builder).Method("ExpandByABlock", 16);
            Traverse.Create(builder).Method("Insert", new[] { typeof(int), typeof(string) }, new object[] { 0, "" });
            Traverse.Create(typeof(Version)).Method("Parse", "1.0");
            Traverse.Create(builder).Property("Capacity");
            Traverse.Create(builder).Property<int>("Length");
            Traverse.Create<Version>().Property("Revision");

            typeof(StringBuilder).GetMethods();
            typeof(StringBuilder).GetProperties();
            typeof(AppDomain).GetEvents();
            typeof(StringBuilder).GetConstructors();
            typeof(StringBuilder).GetMembers();
            AccessTools.GetDeclaredMethods(typeof(StringBuilder));
            AccessTools.GetDeclaredProperties(typeof(Version));
            AccessTools.GetDeclaredConstructors(typeof(Version));
            AccessTools.GetMethodNames(typeof(Version));
            AccessTools.GetMethodNames(builder);
            AccessTools.GetPropertyNames(typeof(Version));
            AccessTools.GetPropertyNames(builder);
            AccessTools.FirstMethod(typeof(StringBuilder), method => method.Name.StartsWith("Append"));
            typeof(StringBuilder).FirstProperty(property => property.CanWrite);
            AccessTools.FirstConstructor(typeof(StringBuilder), constructor => constructor.GetParameters().Length == 2);
            Traverse.Create(builder).Methods();
            Traverse.Create(builder).Properties();
        }
    }

    // Pattern 41: lookups whose result the list shows as a patch target, which add no line of their
    // own: one a TargetMethod returns, and those Harmony.Patch is given directly, through a local,
    // through a static field, as the accessor of a property or an event looked up, taken with a call
    // or a property of the PropertyInfo or EventInfo, the EventInfo also from a local, and from a
    // helper that looks a method up in the class its caller passes. The helper's other call only
    // calls what it finds, in another class, and that lookup keeps its line, as does a second lookup
    // of a patched method. A constructor from .NET's own lookup and an indexer's getter are read as
    // targets like the rest.
    [HarmonyPatch]
    static class ExternalTargetPatch
    {
        static MethodBase TargetMethod() => AccessTools.Method(typeof(StringBuilder), "Clear");

        static void Postfix() { }
    }

    static class ExternalPatchCalls
    {
        static readonly MethodInfo insert = AccessTools.Method(typeof(StringBuilder), "Insert", new[] { typeof(int), typeof(string) });

        static void After() { }

        static MethodInfo Find(Type type) => AccessTools.Method(type, "ToString", Type.EmptyTypes);

        public static void Apply(Harmony harmony)
        {
            var postfix = new HarmonyMethod(typeof(ExternalPatchCalls), nameof(After));
            harmony.Patch(AccessTools.Method(typeof(StringBuilder), "AppendLine", Type.EmptyTypes), postfix: postfix);
            var remove = AccessTools.Method(typeof(StringBuilder), "Remove");
            harmony.Patch(remove, postfix: postfix);
            harmony.Patch(insert, postfix: postfix);
            harmony.Patch(AccessTools.Property(typeof(StringBuilder), "Length").GetSetMethod(), postfix: postfix);
            harmony.Patch(typeof(AppDomain).GetEvent("AssemblyResolve").GetAddMethod(), postfix: postfix);
            harmony.Patch(typeof(AppDomain).GetEvent("TypeResolve").RemoveMethod, postfix: postfix);
            var resolve = typeof(AppDomain).GetEvent("ResourceResolve");
            harmony.Patch(resolve.GetRemoveMethod(), postfix: postfix);
            harmony.Patch(Find(typeof(StringBuilder)), postfix: postfix);
            Find(typeof(Version)).Invoke(new Version(), null);
            harmony.Patch(typeof(Version).GetConstructor(new[] { typeof(string) }), postfix: postfix);
            harmony.Patch(AccessTools.IndexerGetter(typeof(List<int>), new[] { typeof(int) }), postfix: postfix);
            AccessTools.Method(typeof(StringBuilder), "Clear").Invoke(new StringBuilder(), null);
        }
    }

    // Pattern 42: lookups of the assembly's own methods, properties, events and constructors, which
    // add no line: by typeof, as text, through a Traverse and through .NET's own lookups.
    static class OwnMemberLookups
    {
        static void Use(TargetA target)
        {
            AccessTools.Method(typeof(TargetA), "PrivateMethod");
            AccessTools.Method("HarmonyMod.TargetA:Compute", new[] { typeof(int) });
            AccessTools.PropertyGetter(typeof(TargetH), nameof(TargetH.Size));
            AccessTools.Constructor(typeof(TargetB));
            Traverse.Create(target).Method("PrivateMethod");
            Traverse.Create(target).Property("Prop");
            typeof(TargetK).GetMethod("Run");
            typeof(TargetK).GetProperties();
            typeof(TargetE).GetEvent(nameof(TargetE.Changed));
            typeof(TargetE).GetEvents();
            typeof(TargetD<int>).GetMethod("Run");
        }
    }

    // Pattern 43: lookups the scanner reads only in part, with '?' for what it cannot read: a class
    // from a parameter no call passes, a name from a parameter, both, a Traverse reached through a
    // Field call, and a lookup passed as a method group. A lookup that takes a method already looked
    // up prints nothing: its method comes from the lookup that found it.
    static class PartlyReadMembers
    {
        static void Read(Type type, string name, StringBuilder builder)
        {
            AccessTools.Method(type, "Clear");
            AccessTools.Method(typeof(StringBuilder), name);
            AccessTools.PropertySetter(type, name);
            Traverse.Create(builder).Field("m_ChunkPrevious").Method("ToString");
            new[] { "System.Text.StringBuilder:Length" }.Select(AccessTools.PropertyGetter).ToList();
            AccessTools.MethodDelegate<Func<StringBuilder, string>>(AccessTools.Method(typeof(StringBuilder), "ToString"));
        }
    }

    // Pattern 44: lookups in a transpiler, in a lambda, in an iterator and in a property getter, each
    // listed under the member the decompiler shows it in.
    [HarmonyPatch(typeof(TargetI), nameof(TargetI.Run))]
    static class LookupPlacesPatch
    {
        static MethodInfo Append => AccessTools.Method(typeof(StringBuilder), "Append", new[] { typeof(bool) });

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var replace = AccessTools.Method(typeof(StringBuilder), "Replace", new[] { typeof(string), typeof(string) });
            return instructions.MethodReplacer(replace, replace);
        }

        static void Postfix()
        {
            Func<MethodInfo> find = () => AccessTools.Method(typeof(Version), "Clone");
            find();
        }

        static IEnumerable<MethodBase> Constructors()
        {
            yield return AccessTools.Constructor(typeof(Version), new[] { typeof(int), typeof(int) });
        }
    }

    // Pattern 45: classes Harmony patches nothing from and that name no target, which give no line:
    // a container of nested patch classes, whose method carries an empty [HarmonyPatch] of its own,
    // and a class whose [HarmonyPatch] serves no method at all.
    [HarmonyPatch]
    static class ContainerPatches
    {
        [HarmonyPatch(typeof(TargetB), nameof(TargetB.SomeMethod))]
        static class NestedPatch
        {
            static void Postfix() { }
        }

        [HarmonyPatch]
        static void Helper() { }
    }

    [HarmonyPatch]
    class MarkedOnlyPatch
    {
        public int Count() => 0;
    }

    // Pattern 46: targets named where Harmony applies no patch, each listed as Unapplied. A method
    // whose own [HarmonyPatch] names a target but whose name is no patch type and which carries no
    // patch attribute: in a class with an empty [HarmonyPatch], beside a patch method, and in a
    // class without any class attribute. A class whose [HarmonyPatch] names a target and whose
    // method is named postfix, which Harmony's names do not match. A class whose TargetMethod gives
    // a target, which Harmony looks up, with no patch method to apply to it.
    [HarmonyPatch]
    static class MisnamedPatch
    {
        [HarmonyPatch(typeof(TargetB), nameof(TargetB.SomeMethod))]
        static void SomeMethodPostfix() { }
    }

    [HarmonyPatch]
    static class PartlyMisnamedPatch
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(TargetI), nameof(TargetI.Run))]
        static void BeforeRun() { }

        [HarmonyPatch(typeof(TargetI), nameof(TargetI.Run))]
        static void AfterRun() { }
    }

    static class UnmarkedMisnamedPatch
    {
        [HarmonyPatch(typeof(TargetA), "PrivateMethod")]
        static void BeforePrivateMethod() { }
    }

    [HarmonyPatch(typeof(TargetB), nameof(TargetB.SomeMethod))]
    static class LowercasePatch
    {
        static void postfix() { }
    }

    [HarmonyPatch]
    static class TargetOnlyPatch
    {
        static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(TargetA), nameof(TargetA.Compute), new Type[] { typeof(string) });
    }

    // Pattern 47: a method whose own [HarmonyPatch] names a target and that a Harmony.Patch call
    // applies by hand, which gets the call's line and no Unapplied line.
    static class HandAppliedPatch
    {
        [HarmonyPatch(typeof(TargetB), nameof(TargetB.SomeMethod))]
        static void AfterSomeMethod() { }

        static void Apply(Harmony harmony) =>
            harmony.Patch(AccessTools.Method(typeof(TargetB), nameof(TargetB.SomeMethod)),
                postfix: new HarmonyMethod(typeof(HandAppliedPatch), nameof(AfterSomeMethod)));
    }

    // Pattern 48: argument types on the kinds Harmony binds without reading them, a static
    // constructor and a finalizer, here in the class's attribute and as an empty list, and a
    // getter, a setter and an event's add and remove methods named by their property or event,
    // here in the patch method's own attribute, once with the type the accessor does take.
    [HarmonyPatch(typeof(TargetH), MethodType.StaticConstructor, new Type[] { typeof(int) })]
    static class IgnoredTypesStaticConstructorPatch
    {
        static void Prefix() { }
    }

    [HarmonyPatch(typeof(TargetE), MethodType.Finalizer, new Type[0])]
    static class IgnoredTypesFinalizerPatch
    {
        static void Prefix() { }
    }

    [HarmonyPatch(typeof(TargetH), nameof(TargetH.Size), MethodType.Getter)]
    static class IgnoredTypesGetterPatch
    {
        [HarmonyPatch(new Type[] { typeof(int) })]
        static void Prefix() { }
    }

    [HarmonyPatch(typeof(TargetH), nameof(TargetH.Size), MethodType.Setter)]
    static class IgnoredTypesSetterPatch
    {
        [HarmonyPatch(new Type[] { typeof(string) })]
        static void Prefix() { }
    }

    [HarmonyPatch(typeof(TargetE), nameof(TargetE.Changed), MethodType.EventAdd)]
    static class IgnoredTypesEventAdderPatch
    {
        [HarmonyPatch(new Type[] { typeof(int) })]
        static void Prefix() { }
    }

    [HarmonyPatch(typeof(TargetE), nameof(TargetE.Changed), MethodType.EventRemove)]
    static class IgnoredTypesEventRemoverPatch
    {
        [HarmonyPatch(new Type[] { typeof(EventHandler) })]
        static void Prefix() { }
    }

    // Generic methods with two type parameters, with one and without parameters, and with one
    // and a parameter of it, which only pattern 49 reaches.
    public class TargetM
    {
        public static T Convert<T, U>(U value) => default;
        public static T Make<T>() => default;
        public static void Store<T>(T item) { }
    }

    // Pattern 49: generic methods made for type arguments, each named with them. MakeGenericMethod
    // with two type arguments, and with one from a parameter no call passes, which prints '?';
    // AccessTools.Method with its generics argument; the method a GetMethodInfo lambda calls, with
    // the type arguments the compiler infers; and a lookup of another assembly's method made
    // generic right at the call, which keeps a line of its own. A method of two that is made for
    // one of two type arguments is not paired, and its target reads '?'.
    [HarmonyPatch]
    static class TwoTypeArgumentsPatch
    {
        static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(TargetM), nameof(TargetM.Convert)).MakeGenericMethod(typeof(int), typeof(string));

        static void Postfix() { }
    }

    static class GenericLookups
    {
        static void AfterAny() { }

        public static void Apply(Harmony harmony, Type unknown)
        {
            var postfix = new HarmonyMethod(typeof(GenericLookups), nameof(AfterAny));
            harmony.Patch(AccessTools.Method(typeof(TargetM), nameof(TargetM.Make)).MakeGenericMethod(unknown), postfix: postfix);
            harmony.Patch(AccessTools.Method(typeof(TargetM), nameof(TargetM.Store), null, new[] { typeof(string) }), postfix: postfix);
            harmony.Patch(SymbolExtensions.GetMethodInfo(() => TargetM.Store(1)), postfix: postfix);
            harmony.Patch(SymbolExtensions.GetMethodInfo(() => TargetM.Make<long>()), postfix: postfix);
            var chosen = unknown == null
                ? AccessTools.Method(typeof(TargetM), nameof(TargetM.Make))
                : AccessTools.Method(typeof(TargetM), nameof(TargetM.Store));
            harmony.Patch(chosen.MakeGenericMethod(unknown == null ? typeof(int) : typeof(string)), postfix: postfix);
        }

        static IEnumerable<string> Use(IEnumerable<object> items) =>
            (IEnumerable<string>)typeof(Enumerable).GetMethod("Cast").MakeGenericMethod(typeof(string)).Invoke(null, new object[] { items });
    }

    // Pattern 50: the lookups Harmony 2.4 adds, of an event, of an event's add and remove methods
    // and of a finalizer, of classes of another assembly: as a TargetMethod's target, by typeof, as
    // text, in the extension spelling and through the accessor of an event looked up; in
    // Harmony.Patch calls; and made to call what they find. Those of the assembly's own class add
    // no line. A method named by a module's ID and a metadata token names nothing the scanner can
    // read. Beside them the lookups every Harmony 2 version has of the MoveNext of a method's
    // iterator or async state machine, here around lookups of another assembly's methods, which
    // then add no line of their own.
    [HarmonyPatch]
    static class EventAdderTargetPatch
    {
        static MethodBase TargetMethod() => AccessTools.EventAdder(typeof(AppDomain), "FirstChanceException");

        static void Prefix() { }
    }

    [HarmonyPatch]
    static class EventRemoverTextTargetPatch
    {
        static MethodBase TargetMethod() => AccessTools.DeclaredEventRemover("System.AppDomain:ReflectionOnlyAssemblyResolve");

        static void Prefix() { }
    }

    [HarmonyPatch]
    static class FinalizerTargetPatch
    {
        static MethodBase TargetMethod() => typeof(WeakReference).Finalizer();

        static void Prefix() { }
    }

    [HarmonyPatch]
    static class EventTargetPatch
    {
        static MethodBase TargetMethod() => AccessTools.Event(typeof(Console), "CancelKeyPress").GetRemoveMethod();

        static void Prefix() { }
    }

    [HarmonyPatch]
    static class TokenTargetPatch
    {
        static MethodBase TargetMethod() => AccessTools.GetMethodByModuleAndToken("5b2f4b1e-8a43-4d4e-9a3e-0f6f2b1c9d10", 0x06000001);

        static void Prefix() { }
    }

    [HarmonyPatch]
    static class EnumeratorTargetPatch
    {
        static MethodBase TargetMethod() =>
            AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(System.IO.File), "ReadLines", new[] { typeof(string) }));

        static void Postfix() { }
    }

    static class Harmony24PatchCalls
    {
        static void After() { }

        public static void Apply(Harmony harmony)
        {
            var postfix = new HarmonyMethod(typeof(Harmony24PatchCalls), nameof(After));
            harmony.Patch(AccessTools.DeclaredFinalizer(typeof(System.IO.FileStream)), postfix: postfix);
            harmony.Patch(typeof(AppDomain).EventAdder("ProcessExit"), postfix: postfix);
            harmony.Patch(AccessTools.DeclaredEvent("System.AppDomain:DomainUnload").AddMethod, postfix: postfix);
            harmony.Patch(AccessTools.AsyncMoveNext(AccessTools.Method(typeof(System.IO.Stream), "CopyToAsync", new[] { typeof(System.IO.Stream) })),
                postfix: postfix);
        }
    }

    static class Harmony24Lookups
    {
        static void Use(object domain, EventHandler handler)
        {
            AccessTools.Event(typeof(AppDomain), "AssemblyLoad");
            AccessTools.DeclaredEvent(typeof(Console), "CancelKeyPress").GetAddMethod();
            typeof(AppDomain).DeclaredEventAdder("DomainUnload").Invoke(domain, new object[] { handler });
            AccessTools.EventRemover("System.AppDomain:UnhandledException");
            AccessTools.Finalizer(typeof(System.IO.FileStream)).Invoke(null, null);
            typeof(WeakReference).DeclaredFinalizer();
            AccessTools.GetMethodByModuleAndToken("5b2f4b1e-8a43-4d4e-9a3e-0f6f2b1c9d10", 0x06000002).Invoke(null, null);
            AccessTools.EventAdder(typeof(TargetE), nameof(TargetE.Changed));
            AccessTools.Event(typeof(TargetE), nameof(TargetE.Changed));
            AccessTools.Finalizer(typeof(TargetE));
        }
    }

    // Pattern 51: lookups of a class's static constructor: .NET's GetConstructor with flags that
    // hold Static and not Instance, written out and from a field in the overload with calling
    // conventions; Type.TypeInitializer; and AccessTools' constructor lookups with searchForStatic
    // true from a field and from a parameter, and with parameter types, which a static constructor
    // never takes, so that the line names them. AccessTools.all holds Static and Instance and asks
    // for the instance constructor, without Instance for the static one, and a flag the scanner
    // cannot read asks for either. Each as a patch target, in a Harmony.Patch call and made to call
    // what it finds, with lists of a class's constructors limited to the static one and with a flag
    // no call passes.
    [HarmonyPatch]
    static class StaticFlagsTargetPatch
    {
        static MethodBase TargetMethod() => typeof(TargetH).GetConstructor(BindingFlags.Static | BindingFlags.NonPublic, null, Type.EmptyTypes, null);

        static void Prefix() { }
    }

    [HarmonyPatch]
    static class StaticFlagsFieldTargetPatch
    {
        static readonly BindingFlags staticOnly = BindingFlags.Static | BindingFlags.NonPublic;

        static MethodBase TargetMethod() => typeof(TargetH).GetConstructor(staticOnly, null, CallingConventions.Any, Type.EmptyTypes, null);

        static void Prefix() { }
    }

    [HarmonyPatch]
    static class TypeInitializerTargetPatch
    {
        static MethodBase TargetMethod() => typeof(TargetH).TypeInitializer;

        static void Prefix() { }
    }

    [HarmonyPatch]
    static class SearchForStaticFieldTargetPatch
    {
        static readonly bool searchForStatic = true;

        static MethodBase TargetMethod() => AccessTools.DeclaredConstructor(typeof(TargetH), null, searchForStatic);

        static void Prefix() { }
    }

    [HarmonyPatch]
    static class SearchForStaticParameterTargetPatch
    {
        static MethodBase TargetMethod() => Find(true);

        static ConstructorInfo Find(bool isStatic) => AccessTools.Constructor(typeof(TargetH), null, isStatic);

        static void Prefix() { }
    }

    [HarmonyPatch]
    static class StaticWithTypesTargetPatch
    {
        static MethodBase TargetMethod() => AccessTools.Constructor(typeof(TargetH), new[] { typeof(int) }, true);

        static void Prefix() { }
    }

    [HarmonyPatch]
    static class AllFlagsTargetPatch
    {
        static MethodBase TargetMethod() => typeof(TargetH).GetConstructor(AccessTools.all, null, Type.EmptyTypes, null);

        static void Prefix() { }
    }

    [HarmonyPatch]
    static class AllFlagsWithoutInstanceTargetPatch
    {
        static MethodBase TargetMethod() => typeof(TargetH).GetConstructor(AccessTools.all & ~BindingFlags.Instance, null, Type.EmptyTypes, null);

        static void Prefix() { }
    }

    [HarmonyPatch]
    static class UnreadFlagTargetPatch
    {
        static MethodBase TargetMethod() => AccessTools.Constructor(typeof(TargetH), null, Environment.Is64BitProcess);

        static void Prefix() { }
    }

    static class StaticConstructorPatchCalls
    {
        static void After() { }

        public static void Apply(Harmony harmony)
        {
            var postfix = new HarmonyMethod(typeof(StaticConstructorPatchCalls), nameof(After));
            harmony.Patch(typeof(Version).TypeInitializer, postfix: postfix);
            harmony.Patch(typeof(StringBuilder).GetConstructor(BindingFlags.Static | BindingFlags.NonPublic, null, Type.EmptyTypes, null),
                postfix: postfix);
        }
    }

    static class StaticConstructorLookups
    {
        static void Use()
        {
            typeof(Random).TypeInitializer.Invoke(null, null);
            typeof(Version).GetConstructor(BindingFlags.Static | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            AccessTools.Constructor(typeof(StringBuilder), new[] { typeof(int) }, true);
            typeof(Version).GetConstructors(BindingFlags.Static | BindingFlags.NonPublic);
            AccessTools.GetDeclaredConstructors(typeof(StringBuilder), true);
            typeof(StringBuilder).GetConstructor(AccessTools.allDeclared, null, new[] { typeof(int), typeof(int) }, null);
        }

        static void Read(bool isStatic, BindingFlags flags)
        {
            AccessTools.Constructor(typeof(Random), null, isStatic);
            AccessTools.Constructor(typeof(Random), new[] { typeof(int) }, isStatic);
            typeof(Random).GetConstructors(flags);
        }
    }

    // Pattern 52: the helpers a transpiler names a member to Harmony with. CodeInstruction's Call
    // names a method by its class and name, as text and in a lambda, and its LoadField and
    // StoreField a field by its class and name; CodeMatch's Calls and constructor name a method in a
    // lambda; and Harmony 2.4's SymbolExtensions.GetFieldInfo and CodeMatch's LoadsField and
    // StoresField name a field in a lambda. A lambda that makes a delegate from a method group names
    // the method, also of an object a call makes and also to SymbolExtensions.GetMethodInfo. The
    // overloads given a method or field found elsewhere name nothing of their own, nor does a
    // CodeMatch of an opcode; a method of the assembly's own class adds no line and a field of it
    // does; and a lambda the code is given rather than writes at the call is not read.
    static class TranspilerHelpers
    {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var instruction in instructions)
                yield return instruction;
            yield return CodeInstruction.Call(typeof(StringBuilder), "Append", new[] { typeof(char) });
            yield return CodeInstruction.Call("System.Text.StringBuilder:Clear");
            yield return CodeInstruction.Call(typeof(Array), "Empty", null, new[] { typeof(int) });
            yield return CodeInstruction.Call(typeof(TranspilerHelpers), nameof(Helper));
            yield return CodeInstruction.LoadField(typeof(Version), "_Build");
            yield return CodeInstruction.LoadField(typeof(TargetA), nameof(TargetA.viewHeight), true);
            yield return CodeInstruction.StoreField(typeof(Version), "_Revision");
            yield return CodeInstruction.Call(() => Console.WriteLine());
            yield return CodeInstruction.Call<StringBuilder>(builder => builder.AppendLine());
            yield return CodeInstruction.Call<string, int>(text => Convert.ToInt32(text));
            yield return CodeInstruction.Call((System.Linq.Expressions.Expression<Func<Action>>)(() => Console.Clear));
            yield return CodeInstruction.Call((System.Linq.Expressions.Expression<Func<Func<byte[]>>>)(() => Guid.NewGuid().ToByteArray));
        }

        static void Helper() { }

        static CodeMatch[] Matches() => new[]
        {
            CodeMatch.Calls(() => GC.Collect()),
            new CodeMatch(() => Console.ResetColor()),
            CodeMatch.Calls((System.Linq.Expressions.Expression<Func<Func<string, bool>>>)(() => string.IsNullOrEmpty)),
            CodeMatch.LoadsField(() => DBNull.Value),
            CodeMatch.StoresField(() => Type.Missing),
            CodeMatch.LoadsField(() => ((TargetA)null).viewHeight),
            CodeMatch.LoadsField(AccessTools.Field(typeof(Version), "_Minor")),
            CodeMatch.Calls(AccessTools.Method(typeof(StringBuilder), "Insert", new[] { typeof(int), typeof(string) })),
            new CodeMatch(System.Reflection.Emit.OpCodes.Ldarg_0),
        };

        static FieldInfo Field() => SymbolExtensions.GetFieldInfo(() => string.Empty);

        static FieldInfo UntypedField() =>
            SymbolExtensions.GetFieldInfo((System.Linq.Expressions.LambdaExpression)(System.Linq.Expressions.Expression<Func<EventArgs>>)(() => EventArgs.Empty));

        static MethodInfo MethodGroup() =>
            SymbolExtensions.GetMethodInfo((System.Linq.Expressions.Expression<Func<Action>>)(() => GC.WaitForPendingFinalizers));

        static CodeInstruction Given(System.Linq.Expressions.Expression<Action> call) => CodeInstruction.Call(call);
    }

    // Pattern 53: the methods Harmony hands a patch method as delegates. A prefix, postfix or
    // finalizer takes a parameter of a delegate type whose attributes name a method: [HarmonyDelegate]
    // with the class and the name, with parameter types, split over two attributes and with a
    // dispatch type, and [HarmonyPatch] with a method type, applied by attribute and by a
    // Harmony.Patch call. No line for a parameter Harmony reads as one of its own, for a postfix's
    // first parameter it hands the result, for a delegate type without Harmony's attributes, for a
    // delegate type nothing takes, for a method of the assembly's own class, generic or not, or for
    // the delegate type's [HarmonyPatch] as a target. AccessTools.HarmonyDelegate names the method
    // its type argument's attributes name, and nothing it reads where that is a type parameter.
    static class DelegatePatches
    {
        [HarmonyDelegate(typeof(StringBuilder), "Append", typeof(char))]
        public delegate StringBuilder AppendChar(char value);

        [HarmonyDelegate(typeof(Version), "CompareTo")]
        public delegate int CompareVersion(Version other);

        [HarmonyDelegate(typeof(Console))]
        [HarmonyDelegate("Beep")]
        public delegate void Beep();

        [HarmonyDelegate(typeof(Random), "Next", MethodDispatchType.Call)]
        public delegate int NextNumber();

        [HarmonyPatch(typeof(StringBuilder), "Capacity", MethodType.Getter)]
        public delegate int Capacity();

        [HarmonyDelegate(typeof(GC), "KeepAlive")]
        public delegate void KeepAlive(object value);

        [HarmonyDelegate(typeof(TargetA), nameof(TargetA.Compute), typeof(int))]
        public delegate int OwnCompute(int n);

        [HarmonyDelegate(typeof(TargetD<int>), nameof(TargetD<int>.Run))]
        public delegate void OwnRun();

        [HarmonyDelegate(typeof(Environment), "Exit")]
        public delegate void Unused(int code);

        public delegate void Unmarked();

        [HarmonyPatch(typeof(TargetB), nameof(TargetB.SomeMethod))]
        [HarmonyPrefix]
        static void Before(AppendChar append, Beep beep, NextNumber next, OwnCompute compute, OwnRun run,
            Unmarked unmarked, Action action, AppendChar __state) { }

        [HarmonyPatch(typeof(TargetB), nameof(TargetB.SomeMethod))]
        [HarmonyPostfix]
        static CompareVersion After(CompareVersion result, Capacity capacity) => result;

        [HarmonyPatch(typeof(TargetB), nameof(TargetB.SomeMethod))]
        [HarmonyFinalizer]
        static Exception Catch(Exception __exception, AppendChar append, AppendChar again) => __exception;

        static void BeforeCall(KeepAlive keepAlive) { }

        public static void Apply(Harmony harmony) =>
            harmony.Patch(AccessTools.Method(typeof(TargetB), nameof(TargetB.SomeMethod)),
                new HarmonyMethod(typeof(DelegatePatches), nameof(BeforeCall)));

        public static int Fetch()
        {
            AccessTools.HarmonyDelegate<OwnCompute>();
            AccessTools.HarmonyDelegate<OwnRun>();
            Make<Beep>();
            return AccessTools.HarmonyDelegate<CompareVersion>()(new Version());
        }

        static T Make<T>() where T : Delegate => AccessTools.HarmonyDelegate<T>();
    }

    // Pattern 54: one patch applied twice, by two Harmony.Patch calls that name the same patch method
    // and the same target, whose parameter is a nullable value type, once a keyword's and once a
    // generic struct's. The list prints each line once and without a count, since its '?' is C#'s
    // nullable mark, not a part not read. Beside them one field looked up twice in a class read from
    // a parameter no call passes, whose line has a part not read and so ends with the count.
    static class TwiceAppliedPatch
    {
        static void AfterAny() { }

        static void Take(KeyValuePair<int, int>? pair) { }

        static void CountOf(Type type)
        {
            AccessTools.Field(type, "count");
            AccessTools.Field(type, "count");
        }

        public static void Apply(Harmony harmony)
        {
            var postfix = new HarmonyMethod(typeof(TwiceAppliedPatch), nameof(AfterAny));
            harmony.Patch(AccessTools.Constructor(typeof(TargetA), new[] { typeof(int?) }), postfix: postfix);
            harmony.Patch(AccessTools.Constructor(typeof(TargetA), new[] { typeof(int?) }), postfix: postfix);
            harmony.Patch(AccessTools.Method(typeof(TwiceAppliedPatch), nameof(Take), new[] { typeof(KeyValuePair<int, int>?) }), postfix: postfix);
            harmony.Patch(AccessTools.Method(typeof(TwiceAppliedPatch), nameof(Take), new[] { typeof(KeyValuePair<int, int>?) }), postfix: postfix);
        }
    }

    // Pattern 55: a resolver that calls itself in what it returns, through a helper that keeps what it
    // found, and looks members up on what its own call returns, as the resolver Harmony's own library
    // ships for its reflection does. Each lookup is read for every chain of the resolver's calls that
    // binds its parameters, and each reading follows the resolver's own calls into what they return.
    // The lookups name no class the scan can read.
    static class Resolver
    {
        static readonly Dictionary<string, MemberInfo> Known = new Dictionary<string, MemberInfo>();

        static MemberInfo Keep(string key, MemberInfo member)
        {
            Known[key] = member;
            return member;
        }

        internal static MemberInfo Resolve(object reference, string key)
        {
            if (reference == null)
                return null;
            if (Known.TryGetValue(key, out var known))
                return known;
            var type = Resolve(reference.GetType(), key + ".") as Type;
            if (type == null)
                return Keep(key, Resolve(reference.ToString(), key + "?"));
            if (reference is string)
                return Keep(key, type.GetMethods().FirstOrDefault() ?? Resolve(type.DeclaringType, key + "+"));
            if (reference is char)
                return Keep(key, type.GetProperties().FirstOrDefault() ?? Resolve(type.ReflectedType, key + "*"));
            if (reference is bool)
                return Keep(key, type.GetEvents().FirstOrDefault() ?? Resolve(type.UnderlyingSystemType, key + "!"));
            if (reference is int)
                return Keep(key, type.GetFields().FirstOrDefault() ?? Resolve(type.BaseType, key + "-"));
            return Keep(key, type.GetConstructors().FirstOrDefault() ?? Resolve(type.BaseType, key + "^"));
        }
    }

    // Pattern 56: a helper that hands back the method it is given, called in two Harmony.Patch calls in
    // two classes, each on a lookup of its own of one member. Each call's patch line shows the method,
    // so neither lookup gets a line of its own: what the helper returns for the second call carries the
    // second lookup, although the first call gave it the same method.
    static class PassedThrough
    {
        internal static MethodBase Same(MethodBase method) => method;
    }

    static class FirstPassedPatch
    {
        static void AfterAny() { }

        public static void Apply(Harmony harmony) =>
            harmony.Patch(PassedThrough.Same(AccessTools.Method(typeof(StringBuilder), "EnsureCapacity")),
                postfix: new HarmonyMethod(typeof(FirstPassedPatch), nameof(AfterAny)));
    }

    static class SecondPassedPatch
    {
        static void AfterAny() { }

        public static void Apply(Harmony harmony) =>
            harmony.Patch(PassedThrough.Same(AccessTools.Method(typeof(StringBuilder), "EnsureCapacity")),
                postfix: new HarmonyMethod(typeof(SecondPassedPatch), nameof(AfterAny)));
    }

    // A class whose nested class has a method and a constructor that take the nested class, which only
    // patterns 57 and 60 reach.
    public class TargetN
    {
        public class Part
        {
            public Part() { }
            public Part(Part template) { }
            public void Join(Part other) { }
        }
    }

    // Pattern 57: a helper that looks a method up in a class nested in the class its caller passes, with
    // the nested class as the method's parameter type, called once with the class named as text and once
    // with the class from typeof. The scan finds the class the text names, so both calls name the method
    // alike.
    static class NestedLookups
    {
        static void AfterAny() { }

        static MethodInfo Join(Type outer) =>
            AccessTools.Method(AccessTools.Inner(outer, "Part"), "Join", new[] { AccessTools.Inner(outer, "Part") });

        public static void Apply(Harmony harmony)
        {
            var postfix = new HarmonyMethod(typeof(NestedLookups), nameof(AfterAny));
            harmony.Patch(Join(AccessTools.TypeByName("HarmonyMod.TargetN")), postfix: postfix);
            harmony.Patch(Join(typeof(TargetN)), postfix: postfix);
        }
    }

    // Pattern 58: a helper that keeps the method it looks up in a field the first time and returns the
    // field, which the scan reads through the assignment before the return, with the helper's parameter
    // bound to what the call passes.
    static class KeptLookups
    {
        static MethodInfo kept;

        static void AfterAny() { }

        static MethodInfo Keep(Type type)
        {
            if (kept == null)
                kept = AccessTools.Method(type, "TrimToSize");
            return kept;
        }

        public static void Apply(Harmony harmony) =>
            harmony.Patch(Keep(typeof(System.Collections.ArrayList)), postfix: new HarmonyMethod(typeof(KeptLookups), nameof(AfterAny)));
    }

    // Pattern 59: two patch classes a Harmony.Patch call names as text, through TypeByName and through
    // AccessTools.Method's "Type:Method" text, whose postfixes take a ___name parameter and a delegate
    // parameter, which the scan reads from the class the text names, as from a class from typeof.
    static class TextNamedPatchClass
    {
        static void Postfix(int ___count, DelegatePatches.AppendChar append) { }
    }

    static class MethodTextPatchClass
    {
        static void Postfix(string ___label, DelegatePatches.AppendChar append) { }
    }

    static class TextNamedPatches
    {
        public static void Apply(Harmony harmony)
        {
            var target = AccessTools.Method(typeof(TargetI), nameof(TargetI.Run));
            harmony.Patch(target, postfix: new HarmonyMethod(AccessTools.TypeByName("HarmonyMod.TextNamedPatchClass"), "Postfix"));
            harmony.Patch(target, postfix: new HarmonyMethod(AccessTools.Method("HarmonyMod.MethodTextPatchClass:Postfix")));
        }
    }

    // Pattern 60: methods looked up with a class nested in another as their parameter type, where the
    // scan cannot look into the enclosing class: one of an assembly the mod does not reference, named as
    // text, and Harmony's own class from typeof, which the test server does not load; beside them a
    // framework class named as text, which the scan finds, a nested class named as text with '+', and a
    // constructor patch naming the nested class, whose attribute the scan reads from its bytes, since
    // Harmony's MethodType is not loaded. Each nested class reads by its own name, as typeof writes it.
    static class UnreadNestedLookups
    {
        static void Elsewhere() { }
        static void UnloadedLibrary() { }
        static void Framework() { }
        static void PlusText() { }

        public static void Apply(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(AccessTools.Inner(AccessTools.TypeByName("Elsewhere.Outer"), "Part"), "Join",
                    new[] { AccessTools.Inner(AccessTools.TypeByName("Elsewhere.Outer"), "Part") }),
                postfix: new HarmonyMethod(typeof(UnreadNestedLookups), nameof(Elsewhere)));
            harmony.Patch(AccessTools.Method(typeof(TargetN.Part), "Join", new[] { typeof(Harmony).GetNestedType("Part") }),
                postfix: new HarmonyMethod(typeof(UnreadNestedLookups), nameof(UnloadedLibrary)));
            harmony.Patch(AccessTools.Method(typeof(Environment), "GetFolderPath",
                    new[] { AccessTools.Inner(AccessTools.TypeByName("System.Environment"), "SpecialFolder") }),
                postfix: new HarmonyMethod(typeof(UnreadNestedLookups), nameof(Framework)));
            harmony.Patch(AccessTools.Method(typeof(TargetN.Part), "Join", new[] { AccessTools.TypeByName("Elsewhere.Outer+Part") }),
                postfix: new HarmonyMethod(typeof(UnreadNestedLookups), nameof(PlusText)));
        }
    }

    [HarmonyPatch(typeof(TargetN.Part), MethodType.Constructor, new[] { typeof(TargetN.Part) })]
    static class NestedParameterConstructorPatch
    {
        static void Prefix() { }
    }

    // Pattern 61: a parameter passed by reference, pinned with MakeByRefType, which Harmony binds to a
    // parameter passed by ref, out or in alike: by a TargetMethod and by a lookup of reflection, each
    // naming a framework method whose parameter is out, beside lookups whose type is named as text of
    // an assembly the mod does not reference and whose type is a parameter no call passes, not read.
    [HarmonyPatch]
    static class ByReferenceTargetPatch
    {
        static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(int), nameof(int.TryParse), new[] { typeof(string), typeof(int).MakeByRefType() });

        static void Prefix() { }
    }

    static class ByReferenceLookups
    {
        public static MethodInfo Read() =>
            typeof(Dictionary<string, int>).GetMethod("TryGetValue", new[] { typeof(string), typeof(int).MakeByRefType() });

        public static MethodInfo ReadText() =>
            AccessTools.Method("Elsewhere.Outer:Swap", new[] { AccessTools.TypeByName("Elsewhere.Gadget").MakeByRefType() });

        public static MethodInfo ReadUnread(Type element) =>
            typeof(int).GetMethod(nameof(int.TryParse), new[] { typeof(string), element.MakeByRefType() });
    }

    // A generic class with a class nested in it that has a type parameter of its own, and a class whose
    // constructors and method take such classes, which only pattern 62 reaches.
    public class TargetO<T>
    {
        public class Slot<U>
        {
            public void Fill() { }
        }
    }

    public class TargetP
    {
        public TargetP(Dictionary<int, string>.Enumerator entries) { }
        public TargetP(TargetO<int>.Slot<string> slot) { }
        public void Take(Dictionary<int, string>.Enumerator entries) { }
    }

    // Pattern 62: classes named by a reflection name, as text or as a typeof an attribute's bytes hold,
    // the attributes taking Harmony's MethodType, whose arguments the scan reads from those bytes, since
    // it reads attributes in a type system that holds no other assembly: a generic class without type
    // arguments of an assembly the mod does not reference, a class nested in it, a class nested in a
    // generic class with type arguments, and one with a type argument of its own nested in a generic
    // class. Each reads as ILens writes the class elsewhere, never with the count a reflection name
    // carries.
    static class ReflectionNamePatches
    {
        static void TypeByNameText() { }
        static void MethodText() { }
        static void PlusText() { }
        static void InnerText() { }
        static void ConstructedTarget() { }
        static void ConstructedParameter() { }

        public static void Apply(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(AccessTools.TypeByName("Elsewhere.Box`1"), "Run"),
                postfix: new HarmonyMethod(typeof(ReflectionNamePatches), nameof(TypeByNameText)));
            harmony.Patch(AccessTools.Method("Elsewhere.Box`2:Run"),
                postfix: new HarmonyMethod(typeof(ReflectionNamePatches), nameof(MethodText)));
            harmony.Patch(AccessTools.Method(AccessTools.TypeByName("Elsewhere.Box`1+Lid"), "Open"),
                postfix: new HarmonyMethod(typeof(ReflectionNamePatches), nameof(PlusText)));
            harmony.Patch(AccessTools.Method(AccessTools.Inner(AccessTools.TypeByName("Elsewhere.Box`1"), "Lid"), "Close"),
                postfix: new HarmonyMethod(typeof(ReflectionNamePatches), nameof(InnerText)));
            harmony.Patch(AccessTools.Method(AccessTools.TypeByName(
                    "System.Collections.Generic.Dictionary`2+Enumerator[[System.Int32, mscorlib],[System.String, mscorlib]]"), "MoveNext"),
                postfix: new HarmonyMethod(typeof(ReflectionNamePatches), nameof(ConstructedTarget)));
            harmony.Patch(AccessTools.Method(typeof(TargetP), nameof(TargetP.Take), new[] { AccessTools.TypeByName(
                    "System.Collections.Generic.Dictionary`2+Enumerator[[System.Int32, mscorlib],[System.String, mscorlib]]") }),
                postfix: new HarmonyMethod(typeof(ReflectionNamePatches), nameof(ConstructedParameter)));
        }

        static FieldInfo Count() => AccessTools.Field("Elsewhere.Box`1:count");
    }

    [HarmonyPatch("Elsewhere.Box`1", "Run", MethodType.Normal)]
    static class TextTargetGenericPatch
    {
        static void Prefix() { }
    }

    [HarmonyPatch(typeof(List<>), "Clear", MethodType.Normal)]
    static class UnboundGenericPatch
    {
        static void Prefix() { }
    }

    [HarmonyPatch(typeof(Dictionary<,>.Enumerator), "MoveNext", MethodType.Normal)]
    static class UnboundNestedPatch
    {
        static void Prefix() { }
    }

    [HarmonyPatch(typeof(Dictionary<int, string>.Enumerator), "MoveNext", MethodType.Normal)]
    static class ConstructedNestedPatch
    {
        static void Prefix() { }
    }

    [HarmonyPatch(typeof(TargetO<int>.Slot<string>), "Fill", MethodType.Normal)]
    static class GenericNestedPatch
    {
        static void Prefix() { }
    }

    [HarmonyPatch(typeof(TargetP), MethodType.Constructor, new[] { typeof(Dictionary<int, string>.Enumerator) })]
    static class ConstructedNestedConstructorPatch
    {
        static void Prefix() { }
    }

    [HarmonyPatch(typeof(TargetP), MethodType.Constructor, new[] { typeof(TargetO<int>.Slot<string>) })]
    static class GenericNestedConstructorPatch
    {
        static void Prefix() { }
    }
}
