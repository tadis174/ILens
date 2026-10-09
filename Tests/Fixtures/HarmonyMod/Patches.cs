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

    // Members that only the lookups in patterns 19 to 22 reach: a static constructor, a property,
    // an iterator and an async method, whose state machines Harmony patches, and a class nested in
    // another.
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
    // operator and a finalizer. The fixture's Harmony 2.3.6 lacks these values; a cast encodes the
    // same ordinals: 7 Finalizer, 8 EventAdd, 9 EventRemove and 20 OperatorAddition.
    [HarmonyPatch(typeof(TargetE), nameof(TargetE.Changed), (MethodType)8)]
    static class EventAdderPatch
    {
        static void Prefix() { }
    }

    [HarmonyPatch(typeof(TargetE), nameof(TargetE.Changed), (MethodType)9)]
    static class EventRemoverPatch
    {
        static void Prefix() { }
    }

    [HarmonyPatch(typeof(TargetE), (MethodType)20, new Type[] { typeof(TargetE), typeof(TargetE) })]
    static class OperatorPatch
    {
        static void Prefix() { }
    }

    [HarmonyPatch(typeof(TargetE), (MethodType)7)]
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

    // Pattern 34:a helper that passes its parameters to Harmony.Patch, read at each call of it, with
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

    // Pattern 35:a patch described by an object whose constructor keeps the target and the patch
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

    // Pattern 36:a patch method an own extension method makes from a name, and a target taken from
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

    // Pattern 37:loops: a search of one class's methods by an exact name, which names no overload; a
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

    // Pattern 38:a field lookup whose class a helper's caller passes, read at each call.
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

    // Pattern 39:a generic method made for one type argument with MakeGenericMethod, which is what
    // Harmony patches; the target is the method it was made from, found by a search in a
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
}
