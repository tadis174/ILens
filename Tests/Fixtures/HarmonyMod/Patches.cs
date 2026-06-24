using System;
using System.Collections.Generic;
using System.Reflection;
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
}
