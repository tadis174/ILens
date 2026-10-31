using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;

namespace HarmonyMod3
{
    // The lookups Harmony 3.0 adds, which find the code the compiler generates for a method from the
    // method looked up: its lambdas, a local function and the MoveNext of its iterator or async state
    // machine. Each names the method with the kind after it. Every method looked up is the
    // framework's, so a lookup made to call what it finds keeps its line.

    // Pattern 1: as a target of TargetMethods and TargetMethod: the lambdas of a method, a local
    // function by its name, by its name and parameter types, and by a name the code does not give,
    // and a state machine's MoveNext. The lookup of the method adds no line.
    [HarmonyPatch]
    static class LambdasPatch
    {
        static IEnumerable<MethodBase> TargetMethods() =>
            AccessTools.Lambdas(AccessTools.Method(typeof(File), "ReadLines", new[] { typeof(string) }));

        static void Prefix() { }
    }

    [HarmonyPatch]
    static class LocalFunctionPatch
    {
        static MethodBase TargetMethod() =>
            AccessTools.LocalFunction(AccessTools.Method(typeof(File), "ReadAllText", new[] { typeof(string) }), "Read");

        static void Prefix() { }
    }

    [HarmonyPatch]
    static class LocalFunctionTypesPatch
    {
        static MethodBase TargetMethod() =>
            AccessTools.LocalFunction(AccessTools.Method(typeof(File), "ReadAllBytes"), "Fill", new[] { typeof(byte[]), typeof(int) });

        static void Prefix() { }
    }

    [HarmonyPatch]
    static class UnknownNamePatch
    {
        static MethodBase TargetMethod() =>
            AccessTools.LocalFunction(AccessTools.Method(typeof(File), "Copy"), Environment.MachineName);

        static void Prefix() { }
    }

    [HarmonyPatch]
    static class StateMachinePatch
    {
        static MethodBase TargetMethod() =>
            AccessTools.StateMachineMoveNext(AccessTools.Method(typeof(Directory), "EnumerateFiles", new[] { typeof(string) }));

        static void Postfix() { }
    }

    // Pattern 2: in Harmony.Patch calls: each lambda of a loop over them, a local function of a
    // method held in a local, and a state machine's MoveNext.
    static class PatchCalls
    {
        static void After() { }

        public static void Apply(Harmony harmony)
        {
            var postfix = new HarmonyMethod(typeof(PatchCalls), nameof(After));
            foreach (var lambda in AccessTools.Lambdas(AccessTools.Method(typeof(Console), "ReadLine")))
                harmony.Patch(lambda, postfix: postfix);
            var writer = AccessTools.Method(typeof(File), "WriteAllText", new[] { typeof(string), typeof(string) });
            harmony.Patch(AccessTools.LocalFunction(writer, "Write"), postfix: postfix);
            harmony.Patch(AccessTools.StateMachineMoveNext(AccessTools.Method(typeof(File), "ReadLines", new[] { typeof(string) })),
                postfix: postfix);
        }
    }

    // Pattern 3: made to call what they find, where the line is the lookup of the method inside.
    static class Calls
    {
        static void Use()
        {
            AccessTools.LocalFunction(AccessTools.Method(typeof(Path), "Combine", new[] { typeof(string), typeof(string) }), "Join")
                .Invoke(null, null);
            AccessTools.StateMachineMoveNext(AccessTools.Method(typeof(Directory), "EnumerateDirectories", new[] { typeof(string) }))
                .Invoke(null, null);
            foreach (var lambda in AccessTools.Lambdas(AccessTools.Method(typeof(Environment), "GetEnvironmentVariables")))
                lambda.Invoke(null, null);
        }
    }
}
