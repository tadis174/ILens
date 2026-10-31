using System.Diagnostics;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for find_harmony_dependencies. The fixture under
/// Tests/Fixtures/HarmonyMod/ compiles to HarmonyMod.dll alongside the
/// shared inspection target — exercising every pattern the scanner must
/// extract (typed and string-targeted attributes, overload-pinning param
/// arrays, patch methods marked by attribute with targets of their own,
/// TargetMethod/TargetMethods bodies, Harmony.Patch calls, targets named where
/// Harmony applies no patch, field lookups in any class, ___name parameters,
/// lookups of methods, properties, events and constructors of other
/// assemblies' classes, Harmony 2.4's among them, and the members a transpiler
/// names to Harmony's helpers, by text or in a lambda). HarmonyMod3 holds the
/// lookups only Harmony 3.0 has. The shared server cannot load HarmonyLib, the
/// second server can.
/// </summary>
public sealed class HarmonyTests : IClassFixture<ILensServerFixture>, IClassFixture<ReferencesServerFixture>
{
    private readonly ILensServerFixture _server;
    private readonly ReferencesServerFixture _references;
    private readonly string _harmonyModPath;

    public HarmonyTests(ILensServerFixture server, ReferencesServerFixture references)
    {
        _server = server;
        _references = references;
        _harmonyModPath = Path.Combine(
            Path.GetDirectoryName(_server.AssemblyPath)!,
            "HarmonyMod.dll");
        if (!File.Exists(_harmonyModPath))
            throw new FileNotFoundException(
                $"HarmonyMod fixture not found: {_harmonyModPath}. " +
                "Tests/Fixtures/HarmonyMod/ is expected to land here via Tests.csproj's " +
                "ProjectReference.");
    }

    // Every dependency of the fixture, as find_harmony_dependencies lists them. A type is written
    // as ILens writes it whether an attribute names it or decompiled code, where a using
    // declaration shortens it. A line several lookups or calls give stands once.
    private static readonly string[] FixtureDependencies =
    [
        "373 dependencies:",
        "Method ? <- HarmonyMod.DelegatePatches.Make [HarmonyDelegate]",
        // One of two methods made for one of two type arguments: which goes with which is not known.
        "Postfix ? <- HarmonyMod.GenericLookups.AfterAny [PatchCall in HarmonyMod.GenericLookups.Apply]",
        // A method named by a module's ID and a metadata token: a lookup of it made to call what it
        // finds names nothing the scanner can read, and so does one a TargetMethod returns, below,
        // which keeps its line beside the patch line's '?'.
        "Method ? <- HarmonyMod.Harmony24Lookups.Use [GetMethodByModuleAndToken]",
        // A loop over a class's subclasses, over values the code is given, or over a list a call may
        // add to leaves the target unread; the label names the member to look at, and ×3 the three
        // calls the one line stands for, whose targets may differ.
        "Postfix ? <- HarmonyMod.LoopPatches.AfterAny [PatchCall in HarmonyMod.LoopPatches.Apply] ×3",
        // A method lookup the scanner cannot read keeps a line of its own where its result is a patch
        // target too, beside the patch line's '?': here one named as text from a list a call may add to.
        "Method ? <- HarmonyMod.LoopPatches.Apply [AccessToolsMethod]",
        // A lookup the scanner cannot read beside one it can adds '?', so the list says it is not
        // complete; in a TargetMethod that makes the binding branch-dependent.
        "Postfix ? <- HarmonyMod.PartlyReadExtensionsPatch.Postfix [TargetMethods]",
        // A field lookup the scanner cannot read is listed with '?' for each part it cannot read:
        // here both, for the fields of an object typed as object.
        "Field ? <- HarmonyMod.PartlyReadFields.Read [GetFieldNames]",
        // So is a lookup of a method, property, event or constructor: here one whose class and
        // name come from parameters no call passes, and one passed as a method group.
        "Method ? <- HarmonyMod.PartlyReadMembers.Read [PropertySetter]",
        "Method ? <- HarmonyMod.PartlyReadMembers.Read [PropertyGetter]",
        "Prefix ? <- HarmonyMod.PartlyReadTargetPatch.Prefix [DynamicTargetMethod]",
        "Postfix ? <- HarmonyMod.PartlyReadTargetsPatch.Postfix [TargetMethods]",
        // A resolver that calls itself in what it returns looks members up on what its own call
        // returns, which names no class.
        "Field ? <- HarmonyMod.Resolver.Resolve [GetFields]",
        "Method ? <- HarmonyMod.Resolver.Resolve [GetMethods]",
        "Property ? <- HarmonyMod.Resolver.Resolve [GetProperties]",
        "Event ? <- HarmonyMod.Resolver.Resolve [GetEvents]",
        "Prefix ? <- HarmonyMod.TokenTargetPatch.Prefix [DynamicTargetMethod]",
        "Method ? <- HarmonyMod.TokenTargetPatch.TargetMethod [GetMethodByModuleAndToken]",
        // A lambda a transpiler's helper is given through a parameter, not written at the call, is not read.
        "Method ? <- HarmonyMod.TranspilerHelpers.Given [Call]",
        "Prefix ? <- HarmonyMod.UnreadInjectedFieldPatch.Prefix [DynamicTargetMethod]",
        // The resolver's lookup of constructors, on what its own call returns.
        "Method ?..ctor <- HarmonyMod.Resolver.Resolve [GetConstructors]",
        // '?' stands for the class of a method looked up when it comes from a parameter no call
        // passes, from a loop over the assembly's types, or from a Traverse reached through a Field call.
        "Method ?.Clear <- HarmonyMod.PartlyReadMembers.Read [AccessToolsMethod]",
        "Method ?.Run <- HarmonyMod.LoopPatches.Apply [AccessToolsMethod]",
        "Method ?.ToString <- HarmonyMod.PartlyReadMembers.Read [TraverseMethod]",
        // '?' stands for the class when it comes from a property, from a parameter no call in the
        // assembly passes, from an object typed as object, or from a Traverse reached through another
        // Field call, and when a ___name parameter belongs to a patch whose target is not read.
        "Field ?.count <- HarmonyMod.PartlyReadFields.Read [AccessToolsField]",
        "Field ?.count <- HarmonyMod.PartlyReadFields.Read [TraverseField]",
        "Field ?.count <- HarmonyMod.PartlyReadFields.Reassign [AccessToolsField]",
        // Two lookups that give the same line, whose class is not read, stand once with their count.
        "Field ?.count <- HarmonyMod.TwiceAppliedPatch.CountOf [AccessToolsField] ×2",
        "Field ?.count <- HarmonyMod.UnreadInjectedFieldPatch.Prefix [Injected]",
        "Field ?.x <- HarmonyMod.PartlyReadFields.Read [TraverseField]",
        // A generic class named as text without type arguments, through TypeByName, AccessTools.Method's
        // and AccessTools.Field's text and HarmonyPatch's string target, reads in C#'s unbound form, and so
        // does a class nested in it, named with '+' or through AccessTools.Inner.
        "Postfix Elsewhere.Box<,>.Run <- HarmonyMod.ReflectionNamePatches.MethodText [PatchCall in HarmonyMod.ReflectionNamePatches.Apply]",
        "Postfix Elsewhere.Box<>.Lid.Close <- HarmonyMod.ReflectionNamePatches.InnerText [PatchCall in HarmonyMod.ReflectionNamePatches.Apply]",
        "Postfix Elsewhere.Box<>.Lid.Open <- HarmonyMod.ReflectionNamePatches.PlusText [PatchCall in HarmonyMod.ReflectionNamePatches.Apply]",
        "Postfix Elsewhere.Box<>.Run <- HarmonyMod.ReflectionNamePatches.TypeByNameText [PatchCall in HarmonyMod.ReflectionNamePatches.Apply]",
        "Prefix Elsewhere.Box<>.Run <- HarmonyMod.TextTargetGenericPatch.Prefix [StringTargeted]",
        "Field Elsewhere.Box<>.count <- HarmonyMod.ReflectionNamePatches.Count [AccessToolsField]",
        // A class nested in a class of an assembly the mod does not reference, named as text, reads
        // by its own name.
        "Postfix Elsewhere.Outer.Part.Join(Part) <- HarmonyMod.UnreadNestedLookups.Elsewhere [PatchCall in HarmonyMod.UnreadNestedLookups.Apply]",
        // A type named as text is passed by reference as ref and the name the text gives it.
        "Method Elsewhere.Outer.Swap(ref Gadget) <- HarmonyMod.ByReferenceLookups.ReadText [AccessToolsMethod]",
        // A Traverse receiver is read past the casts ILSpy writes around it, and its class is the
        // one the decompiler gives the object: the type of the field info, the class an as cast
        // names, which ILSpy writes as a conditional when it cannot load that class.
        "Field HarmonyLib.HarmonyMethod.method <- HarmonyMod.CastReceiverPatch.Prefix [TraverseField]",
        "Field HarmonyLib.HarmonyPatch.info <- HarmonyMod.FieldUser.Inspect [TraverseField]",
        // A generic class from an assembly the decompiler cannot load keeps its name.
        "Field HarmonyLib.Traverse<int>.traverse <- HarmonyMod.FieldUser.Use [FieldRefAccess]",
        // A constructor lookup without parameter types finds the parameterless constructor.
        "Postfix HarmonyMod.TargetA..ctor() <- HarmonyMod.OtherLookupsPatch.Postfix [TargetMethods]",
        // The blob path: a MethodType.Constructor argument empties ILSpy's own decoding.
        "Prefix HarmonyMod.TargetA..ctor(List<int>) <- HarmonyMod.GenericConstructorPatch.Prefix [TypedAttribute]",
        "Postfix HarmonyMod.TargetA..ctor(List<int>) <- HarmonyMod.OtherLookupsPatch.Postfix [TargetMethods]",
        // The blob path reads an array's brackets after a generic type's arguments.
        "Prefix HarmonyMod.TargetA..ctor(List<int>[]) <- HarmonyMod.ListArrayConstructorPatch.Prefix [TypedAttribute]",
        "Prefix HarmonyMod.TargetA..ctor(int) <- HarmonyMod.ConstructorPatch.Prefix [TypedAttribute]",
        // The blob path writes a nullable value type and an array's dimensions as C# does.
        "Prefix HarmonyMod.TargetA..ctor(int?) <- HarmonyMod.NullableConstructorPatch.Prefix [TypedAttribute]",
        // A patch two Harmony.Patch calls apply stands once, and without a count, since the '?' of a
        // nullable value type is no part the scanner could not read.
        "Postfix HarmonyMod.TargetA..ctor(int?) <- HarmonyMod.TwiceAppliedPatch.AfterAny [PatchCall in HarmonyMod.TwiceAppliedPatch.Apply]",
        "Prefix HarmonyMod.TargetA..ctor(int[,]) <- HarmonyMod.GridConstructorPatch.Prefix [TypedAttribute]",
        // A search of one class's methods by an exact name names no overload.
        "Postfix HarmonyMod.TargetA.Compute <- HarmonyMod.LoopPatches.AfterAny [PatchCallSearch in HarmonyMod.LoopPatches.Apply]",
        // A search of the type's methods names the method; its own tests pick the overload, and its
        // test on a parameter's name names no method.
        "Prefix HarmonyMod.TargetA.Compute <- HarmonyMod.MethodSearchPatch.Prefix [TargetMethodSearch]",
        // Two AccessTools.Method calls in one TargetMethod make the binding branch-dependent.
        "Prefix HarmonyMod.TargetA.Compute(int) <- HarmonyMod.DynamicTargetPatch.Prefix [DynamicTargetMethod]",
        "Prefix HarmonyMod.TargetA.Compute(int) <- HarmonyMod.FieldAccessPatch.Prefix [TypedAttribute]",
        // A marker attribute gives a patch method its kind whatever its name, and the method's
        // own [HarmonyPatch] replaces the class's target or completes it with a member and overload.
        "ReversePatch HarmonyMod.TargetA.Compute(int) <- HarmonyMod.MarkedKindsPatch.OriginalCompute [TypedAttribute]",
        "Postfix HarmonyMod.TargetA.Compute(int) <- HarmonyMod.MarkedMethodsPatch.AfterComputeInt [TypedAttribute]",
        "Prefix HarmonyMod.TargetA.Compute(int) <- HarmonyMod.TargetMethodPatch.Prefix [TargetMethod]",
        // A type named by text, "Namespace.Type:Member", is the type Harmony looks up by that name.
        "Prefix HarmonyMod.TargetA.Compute(int) <- HarmonyMod.TextLookupsPatch.Prefix [TargetMethods]",
        "Prefix HarmonyMod.TargetA.Compute(int) <- HarmonyMod.TraverseFieldPatch.Prefix [TypedAttribute]",
        // A local assigned typeof names the type looked in and a parameter type.
        "Postfix HarmonyMod.TargetA.Compute(string) <- HarmonyMod.LocalTypeLookupsPatch.Postfix [TargetMethods]",
        "Prefix HarmonyMod.TargetA.Compute(string) <- HarmonyMod.MarkedMethodsPatch.BeforeComputeString [TypedAttribute]",
        // Harmony's extension spelling, typeof(X).Method("Y"), is the same lookup as AccessTools.Method.
        "Postfix HarmonyMod.TargetA.Compute(string) <- HarmonyMod.OtherLookupsPatch.Postfix [TargetMethods]",
        // A parameter-type array pins one overload.
        "Prefix HarmonyMod.TargetA.Compute(string) <- HarmonyMod.OverloadPinnedPatch.Prefix [TypedAttribute]",
        "Prefix HarmonyMod.TargetA.Compute(string) <- HarmonyMod.TargetMethodsPatch.Prefix [TargetMethods]",
        // A class without a patch method lists the target its TargetMethod gives as Unapplied:
        // Harmony looks the target up and applies nothing to it.
        "Unapplied HarmonyMod.TargetA.Compute(string) <- HarmonyMod.TargetOnlyPatch [TargetMethod]",
        // A field TargetMethod returns is read where it is assigned, here by its initializer.
        "Postfix HarmonyMod.TargetA.PrivateMethod <- HarmonyMod.InitializedTargetPatch.Postfix [TargetMethod]",
        // TargetMethod and TargetMethods are found by their attributes as well as by their names.
        "Postfix HarmonyMod.TargetA.PrivateMethod <- HarmonyMod.MarkedTargetMethodPatch.Postfix [TargetMethod]",
        // A reflection lookup by name is listed like AccessTools.Method without parameter types.
        "Postfix HarmonyMod.TargetA.PrivateMethod <- HarmonyMod.ReflectionTargetPatch.Postfix [TargetMethod]",
        // A private member can only be named by a string.
        "Postfix HarmonyMod.TargetA.PrivateMethod <- HarmonyMod.StringTargetedPatch.Postfix [StringTargeted]",
        // A method whose own [HarmonyPatch] names a target, but whose name and attributes make it no
        // patch method, is Unapplied, also in a class without a class attribute.
        "Unapplied HarmonyMod.TargetA.PrivateMethod <- HarmonyMod.UnmarkedMisnamedPatch.BeforePrivateMethod [StringTargeted]",
        // An empty parameter-type array pins the parameterless overload.
        "Postfix HarmonyMod.TargetA.PrivateMethod() <- HarmonyMod.OtherLookupsPatch.Postfix [TargetMethods]",
        // Traverse.Create(__instance) takes its type from the patch method's parameter.
        "Field HarmonyMod.TargetA.Prop <- HarmonyMod.TraverseFieldPatch.Prefix [TraverseField]",
        // A getter patch targets the property's get accessor, and so does a property-accessor lookup.
        "Prefix HarmonyMod.TargetA.get_Prop <- HarmonyMod.GetterPatch.Prefix [TypedAttribute]",
        "Postfix HarmonyMod.TargetA.get_Prop <- HarmonyMod.OtherLookupsPatch.Postfix [TargetMethods]",
        "Postfix HarmonyMod.TargetA.set_Prop <- HarmonyMod.LocalTypeLookupsPatch.Postfix [TargetMethods]",
        // A MethodType argument on a patch method's attribute is read from the blob, as on a class's.
        "Prefix HarmonyMod.TargetA.set_Prop <- HarmonyMod.MarkedMethodsPatch.BeforeSetProp [TypedAttribute]",
        // A static field's initializer runs in the static constructor.
        "Field HarmonyMod.TargetA.viewHeight <- HarmonyMod.FieldAccessPatch..cctor [AccessToolsField]",
        // A field a transpiler names to Harmony's helpers, in a lambda or by its class and name, is
        // listed in the assembly's own class too, as a field lookup is; such a method is not.
        "Field HarmonyMod.TargetA.viewHeight <- HarmonyMod.TranspilerHelpers.Matches [LoadsField]",
        "Field HarmonyMod.TargetA.viewHeight <- HarmonyMod.TranspilerHelpers.Transpiler [LoadField]",
        // A constructor patch without argument types binds the parameterless constructor too.
        "Prefix HarmonyMod.TargetB..ctor() <- HarmonyMod.ParameterlessConstructorPatch.Prefix [TypedAttribute]",
        "Prefix HarmonyMod.TargetB.SomeMethod <- HarmonyMod.CastReceiverPatch.Prefix [TypedAttribute]",
        // A patch method's own [HarmonyPatch] may be all that names a target, as on a class
        // carrying only a category.
        "Postfix HarmonyMod.TargetB.SomeMethod <- HarmonyMod.CategorizedPatch.AfterSomeMethod [TypedAttribute]",
        // A nested patch class is listed on its own; the container around it, which names no target
        // and has no patch method, gives no line.
        "Postfix HarmonyMod.TargetB.SomeMethod <- HarmonyMod.ContainerPatches.NestedPatch.Postfix [TypedAttribute]",
        "Postfix HarmonyMod.TargetB.SomeMethod <- HarmonyMod.DelegatePatches.After [TypedAttribute]",
        "Prefix HarmonyMod.TargetB.SomeMethod <- HarmonyMod.DelegatePatches.Before [TypedAttribute]",
        "Prefix HarmonyMod.TargetB.SomeMethod <- HarmonyMod.DelegatePatches.BeforeCall [PatchCall in HarmonyMod.DelegatePatches.Apply]",
        "Finalizer HarmonyMod.TargetB.SomeMethod <- HarmonyMod.DelegatePatches.Catch [TypedAttribute]",
        "Prefix HarmonyMod.TargetB.SomeMethod <- HarmonyMod.DynamicTargetPatch.Prefix [DynamicTargetMethod]",
        "Prefix HarmonyMod.TargetB.SomeMethod <- HarmonyMod.FieldRefAccessPatch.Prefix [TypedAttribute]",
        // A method a Harmony.Patch call applies is listed by the call and not as Unapplied for its own
        // [HarmonyPatch].
        "Postfix HarmonyMod.TargetB.SomeMethod <- HarmonyMod.HandAppliedPatch.AfterSomeMethod [PatchCall in HarmonyMod.HandAppliedPatch.Apply]",
        // A list a call may add to still gives the names it is written with.
        "Postfix HarmonyMod.TargetB.SomeMethod <- HarmonyMod.LoopPatches.AfterAny [PatchCall in HarmonyMod.LoopPatches.Apply]",
        // A class whose [HarmonyPatch] names a target and that has no patch method, here a method
        // named postfix, is Unapplied.
        "Unapplied HarmonyMod.TargetB.SomeMethod <- HarmonyMod.LowercasePatch [TypedAttribute]",
        "Transpiler HarmonyMod.TargetB.SomeMethod <- HarmonyMod.MarkedKindsPatch.Rewrite [TypedAttribute]",
        "Finalizer HarmonyMod.TargetB.SomeMethod <- HarmonyMod.MarkedKindsPatch.Swallow [TypedAttribute]",
        "Postfix HarmonyMod.TargetB.SomeMethod <- HarmonyMod.MarkedTargetMethodsPatch.Postfix [TargetMethods]",
        "Postfix HarmonyMod.TargetB.SomeMethod <- HarmonyMod.MethodSearchesPatch.Postfix [TargetMethodsSearch]",
        "Unapplied HarmonyMod.TargetB.SomeMethod <- HarmonyMod.MisnamedPatch.SomeMethodPostfix [TypedAttribute]",
        "Postfix HarmonyMod.TargetB.SomeMethod <- HarmonyMod.OtherLookupsPatch.Postfix [TargetMethods]",
        "Postfix HarmonyMod.TargetB.SomeMethod <- HarmonyMod.PartlyReadExtensionsPatch.Postfix [TargetMethods]",
        "Prefix HarmonyMod.TargetB.SomeMethod <- HarmonyMod.PartlyReadTargetPatch.Prefix [DynamicTargetMethod]",
        "Postfix HarmonyMod.TargetB.SomeMethod <- HarmonyMod.PartlyReadTargetsPatch.Postfix [TargetMethods]",
        // A field Prepare assigns is read there, with the type Prepare holds in a local.
        "Prefix HarmonyMod.TargetB.SomeMethod <- HarmonyMod.PreparedTargetPatch.Prefix [TargetMethod]",
        "Prefix HarmonyMod.TargetB.SomeMethod <- HarmonyMod.TargetMethodsPatch.Prefix [TargetMethods]",
        // A class named by a string is string-targeted wherever it lives.
        "Prefix HarmonyMod.TargetB.SomeMethod <- HarmonyMod.TextTargetPatch.Prefix [StringTargeted]",
        "Prefix HarmonyMod.TargetB.SomeMethod <- HarmonyMod.TypedNamedPatch.Prefix [TypedAttribute]",
        "Field HarmonyMod.TargetB.counter <- HarmonyMod.FieldRefAccessPatch..cctor [FieldRefAccess]",
        // The blob names a nested type Outer+Inner.
        "Prefix HarmonyMod.TargetC.Nested..ctor() <- HarmonyMod.NestedConstructorPatch.Prefix [TypedAttribute]",
        // A string names a nested class with '+', as Harmony looks it up.
        "Prefix HarmonyMod.TargetC.Nested..ctor() <- HarmonyMod.TextConstructorPatch.Prefix [StringTargeted]",
        // A list of "Type:Method" names the code writes out gives a target for each name.
        "Postfix HarmonyMod.TargetD.Run <- HarmonyMod.LoopPatches.AfterAny [PatchCall in HarmonyMod.LoopPatches.Apply]",
        // A class without any Harmony attribute names its target on the patch method alone.
        "Prefix HarmonyMod.TargetD.Run <- HarmonyMod.MethodTargetPatch.Prefix [TypedAttribute]",
        // TargetD and TargetD<T> share a name; the type argument says which is patched.
        "Prefix HarmonyMod.TargetD<int>.Run <- HarmonyMod.GenericTargetPatch.Prefix [TypedAttribute]",
        // Harmony 2.4 binds a finalizer or an operator by its kind alone, so typeof names all of the
        // target, though the finalizer is not public.
        "Prefix HarmonyMod.TargetE.Finalize <- HarmonyMod.FinalizeMethodPatch.Prefix [TypedAttribute]",
        // Harmony binds a finalizer, a static constructor and an accessor named by its property or
        // event without reading argument types, so such a target leaves out those its attribute gives.
        "Prefix HarmonyMod.TargetE.Finalize <- HarmonyMod.IgnoredTypesFinalizerPatch.Prefix [TypedAttribute]",
        // An event patch targets the accessor, and a public event counts as a public property does.
        "Prefix HarmonyMod.TargetE.add_Changed <- HarmonyMod.EventAdderPatch.Prefix [TypedAttribute]",
        "Prefix HarmonyMod.TargetE.add_Changed <- HarmonyMod.IgnoredTypesEventAdderPatch.Prefix [TypedAttribute]",
        "Prefix HarmonyMod.TargetE.op_Addition(TargetE, TargetE) <- HarmonyMod.OperatorPatch.Prefix [TypedAttribute]",
        "Prefix HarmonyMod.TargetE.remove_Changed <- HarmonyMod.EventRemoverPatch.Prefix [TypedAttribute]",
        "Prefix HarmonyMod.TargetE.remove_Changed <- HarmonyMod.IgnoredTypesEventRemoverPatch.Prefix [TypedAttribute]",
        // A getter or setter patch that names no property binds the accessor of the indexer with
        // the index types named, which counts as bound by its kind; a setter takes the value last.
        "Prefix HarmonyMod.TargetF.get_Item(int) <- HarmonyMod.IndexerGetterPatch.Prefix [TypedAttribute]",
        "Prefix HarmonyMod.TargetF.set_Item(string, string) <- HarmonyMod.IndexerSetterPatch.Prefix [TypedAttribute]",
        // Without index types it binds the only indexer, under the name the indexer was given.
        "Prefix HarmonyMod.TargetG.get_Cell <- HarmonyMod.RenamedIndexerGetterPatch.Prefix [TypedAttribute]",
        "Prefix HarmonyMod.TargetG.set_Cell(int, int, int) <- HarmonyMod.RenamedIndexerSetterPatch.Prefix [TypedAttribute]",
        // AccessTools.all without Instance asks for the static constructor.
        "Prefix HarmonyMod.TargetH..cctor <- HarmonyMod.AllFlagsWithoutInstanceTargetPatch.Prefix [TargetMethod]",
        // A static constructor patch prints without the type its attribute gives.
        "Prefix HarmonyMod.TargetH..cctor <- HarmonyMod.IgnoredTypesStaticConstructorPatch.Prefix [TypedAttribute]",
        // searchForStatic finds the static constructor.
        "Postfix HarmonyMod.TargetH..cctor <- HarmonyMod.OtherLookupsPatch.Postfix [TargetMethods]",
        // So do binding flags that hold Static and not Instance, written out or from a field,
        // TypeInitializer, and searchForStatic from a field or a parameter.
        "Prefix HarmonyMod.TargetH..cctor <- HarmonyMod.SearchForStaticFieldTargetPatch.Prefix [TargetMethod]",
        "Prefix HarmonyMod.TargetH..cctor <- HarmonyMod.SearchForStaticParameterTargetPatch.Prefix [TargetMethod]",
        "Prefix HarmonyMod.TargetH..cctor <- HarmonyMod.StaticFlagsFieldTargetPatch.Prefix [TargetMethod]",
        "Prefix HarmonyMod.TargetH..cctor <- HarmonyMod.StaticFlagsTargetPatch.Prefix [TargetMethod]",
        "Prefix HarmonyMod.TargetH..cctor <- HarmonyMod.TypeInitializerTargetPatch.Prefix [TargetMethod]",
        // A flag the scanner cannot read may ask for either constructor.
        "Prefix HarmonyMod.TargetH..cctor <- HarmonyMod.UnreadFlagTargetPatch.Prefix [DynamicTargetMethod]",
        // A static constructor takes no parameters, so a lookup for it with parameter types finds
        // nothing, and the line names what it asks for.
        "Prefix HarmonyMod.TargetH..cctor(int) <- HarmonyMod.StaticWithTypesTargetPatch.Prefix [TargetMethod]",
        // AccessTools.all holds Static and Instance and asks for the instance constructor.
        "Prefix HarmonyMod.TargetH..ctor() <- HarmonyMod.AllFlagsTargetPatch.Prefix [TargetMethod]",
        "Prefix HarmonyMod.TargetH..ctor() <- HarmonyMod.UnreadFlagTargetPatch.Prefix [DynamicTargetMethod]",
        // AccessTools.TypeByName names a nested class with '+', as an argument or held in a local.
        "Postfix HarmonyMod.TargetH.Inner.Describe <- HarmonyMod.LocalTypeLookupsPatch.Postfix [TargetMethods]",
        "Prefix HarmonyMod.TargetH.Inner.Describe <- HarmonyMod.TextLookupsPatch.Prefix [TargetMethods]",
        "Postfix HarmonyMod.TargetH.Items <- HarmonyMod.OtherLookupsPatch.Postfix [TargetMethods]",
        // A lookup wrapped in EnumeratorMoveNext or AsyncMoveNext patches the method's state machine.
        "Prefix HarmonyMod.TargetH.Items (Enumerator) <- HarmonyMod.StateMachineLookupsPatch.Prefix [TargetMethods]",
        "Prefix HarmonyMod.TargetH.Refresh (Async) <- HarmonyMod.StateMachineLookupsPatch.Prefix [TargetMethods]",
        // So do a getter and a setter patch named by their property.
        "Prefix HarmonyMod.TargetH.get_Size <- HarmonyMod.IgnoredTypesGetterPatch.Prefix [TypedAttribute]",
        "Prefix HarmonyMod.TargetH.get_Size <- HarmonyMod.TextLookupsPatch.Prefix [TargetMethods]",
        "Prefix HarmonyMod.TargetH.set_Size <- HarmonyMod.IgnoredTypesSetterPatch.Prefix [TypedAttribute]",
        "Postfix HarmonyMod.TargetH.set_Size <- HarmonyMod.OtherLookupsPatch.Postfix [TargetMethods]",
        // '?' stands for the field when ___ is followed by an index, when the name is not written
        // out, when a lookup takes an index, and when an enumeration reaches all of a class's fields.
        "Field HarmonyMod.TargetI.? <- HarmonyMod.InjectedFieldsPatch.Postfix [Injected]",
        "Field HarmonyMod.TargetI.? <- HarmonyMod.PartlyReadFields.Read [AccessToolsField]",
        "Field HarmonyMod.TargetI.? <- HarmonyMod.PartlyReadFields.Read [DeclaredField]",
        "Field HarmonyMod.TargetI.? <- HarmonyMod.PartlyReadFields.Read [GetDeclaredFields]",
        "Field HarmonyMod.TargetI.? <- HarmonyMod.PartlyReadFields.Read [GetFields]",
        "Postfix HarmonyMod.TargetI.Run <- HarmonyMod.AccessorFieldPatch.Postfix [TypedAttribute]",
        "Postfix HarmonyMod.TargetI.Run <- HarmonyMod.InjectedFieldsPatch.Postfix [TypedAttribute]",
        "Prefix HarmonyMod.TargetI.Run <- HarmonyMod.InjectedFieldsPatch.Prefix [TypedAttribute]",
        "Postfix HarmonyMod.TargetI.Run <- HarmonyMod.LookupPlacesPatch.Postfix [TypedAttribute]",
        "Transpiler HarmonyMod.TargetI.Run <- HarmonyMod.LookupPlacesPatch.Transpiler [TypedAttribute]",
        "Postfix HarmonyMod.TargetI.Run <- HarmonyMod.LoopPatches.AfterAny [PatchCall in HarmonyMod.LoopPatches.Apply]",
        // A patch class a Harmony.Patch call names through AccessTools.Method's text.
        "Postfix HarmonyMod.TargetI.Run <- HarmonyMod.MethodTextPatchClass.Postfix [PatchCall in HarmonyMod.TextNamedPatches.Apply]",
        // A method that is no patch method is Unapplied beside a patch method of its class as well.
        "Unapplied HarmonyMod.TargetI.Run <- HarmonyMod.PartlyMisnamedPatch.AfterRun [TypedAttribute]",
        "Prefix HarmonyMod.TargetI.Run <- HarmonyMod.PartlyMisnamedPatch.BeforeRun [TypedAttribute]",
        // A patch class a Harmony.Patch call names through TypeByName.
        "Postfix HarmonyMod.TargetI.Run <- HarmonyMod.TextNamedPatchClass.Postfix [PatchCall in HarmonyMod.TextNamedPatches.Apply]",
        // A lookup is found in any member of any class, here a patch class's property getter.
        "Field HarmonyMod.TargetI.count <- HarmonyMod.AccessorFieldPatch.get_Count [AccessToolsField]",
        // A class a helper's callers pass is read at each call.
        "Field HarmonyMod.TargetI.count <- HarmonyMod.FieldHelper.Count [AccessToolsField]",
        // Lookups in the static constructor of a class that patches nothing. The label names the
        // call: DeclaredField looks in the class alone, Field in its base classes too. A class is
        // read from typeof, from text, through TypeByName, from a local and from a lookup's type
        // argument, in Harmony's extension spelling as in AccessTools'.
        "Field HarmonyMod.TargetI.count <- HarmonyMod.FieldHolder..cctor [AccessToolsField]",
        "Field HarmonyMod.TargetI.count <- HarmonyMod.FieldHolder..cctor [DeclaredField]",
        "Field HarmonyMod.TargetI.count <- HarmonyMod.FieldHolder..cctor [FieldRefAccess]",
        "Field HarmonyMod.TargetI.count <- HarmonyMod.FieldHolder..cctor [GetField]",
        // A lookup in an iterator, which the decompiler shows in the method it is written in.
        "Field HarmonyMod.TargetI.count <- HarmonyMod.FieldUser.Fields [AccessToolsField]",
        "Field HarmonyMod.TargetI.count <- HarmonyMod.FieldUser.Use [FieldRefAccess]",
        "Field HarmonyMod.TargetI.count <- HarmonyMod.FieldUser.Use [AccessToolsField]",
        // A Traverse reads the class of the object it was created from, given directly, held in a
        // field or a local, and in the generic Field<T> as in Field.
        "Field HarmonyMod.TargetI.count <- HarmonyMod.FieldUser.Use [TraverseField]",
        // A ___name parameter of a prefix names the field of the class the patch targets.
        "Field HarmonyMod.TargetI.count <- HarmonyMod.InjectedFieldsPatch.Prefix [Injected]",
        // A Traverse created from a new object reads that object's class.
        "Field HarmonyMod.TargetI.count <- HarmonyMod.PartlyReadFields.Read [TraverseField]",
        // Only the inner lookup of a FieldRefAccess given a FieldInfo is listed, and neither the
        // FieldRefAccess nor the method group passed to Select, whose fields come from GetFields.
        "Field HarmonyMod.TargetI.count <- HarmonyMod.PartlyReadFields.Read [AccessToolsField]",
        // A parameter the body assigns in a branch also takes that value.
        "Field HarmonyMod.TargetI.count <- HarmonyMod.PartlyReadFields.Reassign [AccessToolsField]",
        // A ___name parameter of a patch class named through TypeByName, read from the class the text names.
        "Field HarmonyMod.TargetI.count <- HarmonyMod.TextNamedPatchClass.Postfix [Injected]",
        // A lookup in a lambda, which the decompiler shows in the method it is written in.
        "Field HarmonyMod.TargetI.label <- HarmonyMod.AccessorFieldPatch.Postfix [AccessToolsField]",
        "Field HarmonyMod.TargetI.label <- HarmonyMod.FieldHolder..cctor [AccessToolsField]",
        "Field HarmonyMod.TargetI.label <- HarmonyMod.FieldUser.Use [StaticFieldRefAccess]",
        "Field HarmonyMod.TargetI.label <- HarmonyMod.FieldUser.Use [GetField]",
        // A Traverse of a class's static fields, created from the class by typeof or as text.
        "Field HarmonyMod.TargetI.label <- HarmonyMod.FieldUser.Use [TraverseField]",
        // A ___name parameter passed by ref, through which the patch writes the field.
        "Field HarmonyMod.TargetI.label <- HarmonyMod.InjectedFieldsPatch.Prefix [Injected]",
        // A ___name parameter of a patch class named through AccessTools.Method's text.
        "Field HarmonyMod.TargetI.label <- HarmonyMod.MethodTextPatchClass.Postfix [Injected]",
        "Field HarmonyMod.TargetI.total <- HarmonyMod.FieldHolder..cctor [AccessToolsField]",
        "Field HarmonyMod.TargetI.total <- HarmonyMod.FieldHolder..cctor [DeclaredField]",
        "Field HarmonyMod.TargetI.total <- HarmonyMod.FieldUser.Use [StaticFieldRefAccess]",
        "Field HarmonyMod.TargetI.total <- HarmonyMod.FieldUser.Use [TraverseField]",
        "Field HarmonyMod.TargetJ.? <- HarmonyMod.PartlyReadFields.Read [GetFields]",
        "Field HarmonyMod.TargetJ.x <- HarmonyMod.FieldUser.Use [StructFieldRefAccess]",
        // A nested class found by name through AccessTools.Inner or Type.GetNestedType, and a patch
        // class from GetType(), the class the calling member is declared in.
        "Postfix HarmonyMod.TargetK.Part.Move <- HarmonyMod.InstancePatcher.AfterAny [PatchCall in HarmonyMod.InstancePatcher.Apply]",
        // A class named as text through Assembly.GetType, and below through Type.GetType behind ?.
        "Postfix HarmonyMod.TargetK.Run <- HarmonyMod.InstancePatcher.AfterAny [PatchCall in HarmonyMod.InstancePatcher.Apply]",
        // A projection of names the code writes out gives a target for each name.
        "Postfix HarmonyMod.TargetK.Run <- HarmonyMod.LoopPatches.AfterAny [PatchCall in HarmonyMod.LoopPatches.Apply]",
        // A local assigned twice is read as it stands at each call.
        "Postfix HarmonyMod.TargetK.Run <- HarmonyMod.ManualPatches.AfterStop [PatchCall in HarmonyMod.ManualPatches.Reassign]",
        // A patch applied by calling Harmony.Patch: its kind from the argument's position, its patch
        // method from a class held in a local and a name, and the calling member in the label.
        "Prefix HarmonyMod.TargetK.Run <- HarmonyMod.ManualPatches.BeforeRun [PatchCall in HarmonyMod.ManualPatches.Apply]",
        // A patch method an own extension method makes from a name.
        "Postfix HarmonyMod.TargetK.Run <- HarmonyMod.OwnMethodPatches.AfterAny [PatchCall in HarmonyMod.OwnMethodPatches.Apply]",
        // A helper's parameters are read at each call of the helper, which the label names.
        "Prefix HarmonyMod.TargetK.Run <- HarmonyMod.PatchHelper.BeforeAny [PatchCall in HarmonyMod.PatchHelper.Apply]",
        // A patch object's fields are read where a factory builds it, here in an iterator that
        // names the patch class with GetType().
        "Prefix HarmonyMod.TargetK.Run <- HarmonyMod.SpecPatches.BeforeRun [PatchCall in HarmonyMod.SpecPatches.GetPatches]",
        // Another class's TargetMethod applied by a call is listed by both routes.
        "Postfix HarmonyMod.TargetK.Stop <- HarmonyMod.HandAppliedTargetPatch.Postfix [TargetMethod]",
        "Postfix HarmonyMod.TargetK.Stop <- HarmonyMod.HandAppliedTargetPatch.Postfix [PatchCall in HarmonyMod.OwnMethodPatches.Apply]",
        "Postfix HarmonyMod.TargetK.Stop <- HarmonyMod.InstancePatcher.AfterAny [PatchCall in HarmonyMod.InstancePatcher.Apply]",
        "Postfix HarmonyMod.TargetK.Stop <- HarmonyMod.LoopPatches.AfterAny [PatchCall in HarmonyMod.LoopPatches.Apply]",
        // An argument passed by name is the kind its parameter is.
        "Postfix HarmonyMod.TargetK.Stop <- HarmonyMod.ManualPatches.AfterStop [PatchCall in HarmonyMod.ManualPatches.Apply]",
        "Postfix HarmonyMod.TargetK.Stop <- HarmonyMod.PatchHelper.AfterAny [PatchCall in HarmonyMod.PatchHelper.Apply]",
        "Postfix HarmonyMod.TargetK.Stop <- HarmonyMod.SpecPatches.AfterStop [PatchCall in HarmonyMod.SpecPatches.GetPatches]",
        // SymbolExtensions.GetMethodInfo names the overload its lambda calls, and a MethodInfo passed
        // for a HarmonyMethod is the patch method.
        "Postfix HarmonyMod.TargetK.Stop() <- HarmonyMod.ManualPatches.AfterStop [PatchCall in HarmonyMod.ManualPatches.Apply]",
        // An empty array from Array.Empty pins the parameterless overload.
        "Prefix HarmonyMod.TargetK.Stop() <- HarmonyMod.ManualPatches.BeforeRun [PatchCall in HarmonyMod.ManualPatches.Reassign]",
        "Field HarmonyMod.TargetK.count <- HarmonyMod.FieldHelper.Count [AccessToolsField]",
        // A ___name parameter of a prefix a call applies names a field of the class it patches.
        "Field HarmonyMod.TargetK.count <- HarmonyMod.ManualPatches.BeforeRun [Injected]",
        // A property getter through its PropertyInfo.
        "Postfix HarmonyMod.TargetK.get_Size <- HarmonyMod.InstancePatcher.AfterAny [PatchCall in HarmonyMod.InstancePatcher.Apply]",
        // A HarmonyMethod made from a method looked up by reflection, and below by AccessTools.
        "Finalizer HarmonyMod.TargetK.get_Size <- HarmonyMod.ManualPatches.CatchSize [PatchCall in HarmonyMod.ManualPatches.Apply]",
        "Transpiler HarmonyMod.TargetK.get_Size <- HarmonyMod.ManualPatches.RewriteSize [PatchCall in HarmonyMod.ManualPatches.Apply]",
        // A method made generic for type arguments is named with them, in a Harmony.Patch call and
        // in a TargetMethod's search, so a line says which version of the method is patched.
        "Postfix HarmonyMod.TargetL.Parse<int> <- HarmonyMod.GenericPatchCall.AfterParse [PatchCall in HarmonyMod.GenericPatchCall.Apply]",
        "Postfix HarmonyMod.TargetL.Parse<int> <- HarmonyMod.GenericSearchPatch.Postfix [TargetMethodSearch]",
        // Two type arguments, and one from a parameter no call passes, which is '?'.
        "Postfix HarmonyMod.TargetM.Convert<int, string> <- HarmonyMod.TwoTypeArgumentsPatch.Postfix [TargetMethod]",
        "Postfix HarmonyMod.TargetM.Make<?> <- HarmonyMod.GenericLookups.AfterAny [PatchCall in HarmonyMod.GenericLookups.Apply]",
        // The method a GetMethodInfo lambda calls, made for the type arguments the compiler infers,
        // pinned to the parameter types the method declares, which pass back as parameter_types.
        "Postfix HarmonyMod.TargetM.Make<long>() <- HarmonyMod.GenericLookups.AfterAny [PatchCall in HarmonyMod.GenericLookups.Apply]",
        "Postfix HarmonyMod.TargetM.Store<int>(T) <- HarmonyMod.GenericLookups.AfterAny [PatchCall in HarmonyMod.GenericLookups.Apply]",
        // AccessTools.Method's generics argument.
        "Postfix HarmonyMod.TargetM.Store<string> <- HarmonyMod.GenericLookups.AfterAny [PatchCall in HarmonyMod.GenericLookups.Apply]",
        // A constructor patch whose attribute is read from its bytes names a nested class by its own name.
        "Prefix HarmonyMod.TargetN.Part..ctor(Part) <- HarmonyMod.NestedParameterConstructorPatch.Prefix [TypedAttribute]",
        // One helper given one class named as text and from typeof names the method alike, and a
        // nested class named as text with '+', or nested in a class from typeof whose library is not
        // loaded, reads by its own name as well.
        "Postfix HarmonyMod.TargetN.Part.Join(Part) <- HarmonyMod.NestedLookups.AfterAny [PatchCall in HarmonyMod.NestedLookups.Apply]",
        "Postfix HarmonyMod.TargetN.Part.Join(Part) <- HarmonyMod.UnreadNestedLookups.PlusText [PatchCall in HarmonyMod.UnreadNestedLookups.Apply]",
        "Postfix HarmonyMod.TargetN.Part.Join(Part) <- HarmonyMod.UnreadNestedLookups.UnloadedLibrary [PatchCall in HarmonyMod.UnreadNestedLookups.Apply]",
        // A class with a type argument of its own nested in a generic class with one, and a class nested
        // in a generic class with type arguments, read from an attribute's bytes or from text, keep the
        // nested class and each class's own type arguments, as a target and as a parameter type.
        "Prefix HarmonyMod.TargetO<int>.Slot<string>.Fill <- HarmonyMod.GenericNestedPatch.Prefix [TypedAttribute]",
        "Prefix HarmonyMod.TargetP..ctor(Dictionary<int, string>.Enumerator) <- HarmonyMod.ConstructedNestedConstructorPatch.Prefix [TypedAttribute]",
        "Prefix HarmonyMod.TargetP..ctor(TargetO<int>.Slot<string>) <- HarmonyMod.GenericNestedConstructorPatch.Prefix [TypedAttribute]",
        "Postfix HarmonyMod.TargetP.Take(Dictionary<int, string>.Enumerator) <- HarmonyMod.ReflectionNamePatches.ConstructedParameter [PatchCall in HarmonyMod.ReflectionNamePatches.Apply]",
        // A patch two Harmony.Patch calls apply to a method that takes a generic struct's nullable
        // type stands once, and without a count, since a '?' after '>' is no part not read either.
        "Postfix HarmonyMod.TwiceAppliedPatch.Take(KeyValuePair<int, int>?) <- HarmonyMod.TwiceAppliedPatch.AfterAny [PatchCall in HarmonyMod.TwiceAppliedPatch.Apply]",
        // From here on, members of other assemblies' classes. A method, property, event or
        // constructor the assembly looks up by reflection has a line that starts with Method,
        // Property or Event and ends with the lookup call. An enumeration of a class's events names
        // the class alone; an accessor taken right at an event lookup is a Method line, and one
        // taken from a local the lookup was stored in leaves the Event line.
        "Event System.AppDomain.? <- HarmonyMod.MemberLookups.Use [GetEvents]",
        // Harmony 2.4's event lookup gives an Event line, as .NET's own does.
        "Event System.AppDomain.AssemblyLoad <- HarmonyMod.Harmony24Lookups.Use [AccessToolsEvent]",
        "Event System.AppDomain.AssemblyLoad <- HarmonyMod.MemberLookups.Use [GetEvent]",
        "Event System.AppDomain.DomainUnload <- HarmonyMod.MemberLookups.Use [GetEvent]",
        // An event's add or remove method passed to Harmony.Patch, taken with a call or a property,
        // from the lookup or from a local, is the patch's target, and its lookup has no line of its
        // own.
        "Postfix System.AppDomain.add_AssemblyResolve <- HarmonyMod.ExternalPatchCalls.After [PatchCall in HarmonyMod.ExternalPatchCalls.Apply]",
        // Harmony 2.4's lookups of an event's add or remove method name the accessor, made to call it,
        // as a TargetMethod's target and in a Harmony.Patch call, by typeof, as text and in the
        // extension spelling; so does an accessor taken from Harmony's event lookup, which then adds
        // no line of its own.
        "Method System.AppDomain.add_DomainUnload <- HarmonyMod.Harmony24Lookups.Use [DeclaredEventAdder]",
        "Postfix System.AppDomain.add_DomainUnload <- HarmonyMod.Harmony24PatchCalls.After [PatchCall in HarmonyMod.Harmony24PatchCalls.Apply]",
        "Prefix System.AppDomain.add_FirstChanceException <- HarmonyMod.EventAdderTargetPatch.Prefix [TargetMethod]",
        "Postfix System.AppDomain.add_ProcessExit <- HarmonyMod.Harmony24PatchCalls.After [PatchCall in HarmonyMod.Harmony24PatchCalls.Apply]",
        "Method System.AppDomain.add_UnhandledException <- HarmonyMod.MemberLookups.Use [GetEvent]",
        "Method System.AppDomain.remove_ProcessExit <- HarmonyMod.MemberLookups.Use [GetEvent]",
        "Prefix System.AppDomain.remove_ReflectionOnlyAssemblyResolve <- HarmonyMod.EventRemoverTextTargetPatch.Prefix [TargetMethod]",
        "Postfix System.AppDomain.remove_ResourceResolve <- HarmonyMod.ExternalPatchCalls.After [PatchCall in HarmonyMod.ExternalPatchCalls.Apply]",
        "Postfix System.AppDomain.remove_TypeResolve <- HarmonyMod.ExternalPatchCalls.After [PatchCall in HarmonyMod.ExternalPatchCalls.Apply]",
        "Method System.AppDomain.remove_UnhandledException <- HarmonyMod.Harmony24Lookups.Use [EventRemover]",
        "Method System.Array.Empty<int> <- HarmonyMod.TranspilerHelpers.Transpiler [Call]",
        // A helper keeps the method it looks up in a field and returns the field: the scan reads the
        // field through the assignment before the return, with the class the call passes.
        "Postfix System.Collections.ArrayList.TrimToSize <- HarmonyMod.KeptLookups.AfterAny [PatchCall in HarmonyMod.KeptLookups.Apply]",
        // A class nested in a generic class without type arguments and with them, from a typeof an
        // attribute's bytes hold or from text, and below a generic class without them, is written as
        // ILens writes the class a typeof in code names.
        "Prefix System.Collections.Generic.Dictionary<,>.Enumerator.MoveNext <- HarmonyMod.UnboundNestedPatch.Prefix [Attribute]",
        "Prefix System.Collections.Generic.Dictionary<int, string>.Enumerator.MoveNext <- HarmonyMod.ConstructedNestedPatch.Prefix [Attribute]",
        "Postfix System.Collections.Generic.Dictionary<int, string>.Enumerator.MoveNext <- HarmonyMod.ReflectionNamePatches.ConstructedTarget [PatchCall in HarmonyMod.ReflectionNamePatches.Apply]",
        // A parameter type MakeByRefType makes is written ref T, which the method declares out here.
        "Method System.Collections.Generic.Dictionary<string, int>.TryGetValue(string, ref int) <- HarmonyMod.ByReferenceLookups.Read [GetMethod]",
        "Prefix System.Collections.Generic.List<>.Clear <- HarmonyMod.UnboundGenericPatch.Prefix [Attribute]",
        // An indexer lookup names index types and no name, so its line carries C#'s default name;
        // an indexer looked up as a property is a Property line.
        "Property System.Collections.Generic.List<int>.Item <- HarmonyMod.MemberLookups.Use [DeclaredIndexer]",
        "Property System.Collections.Generic.List<int>.Item(int) <- HarmonyMod.MemberLookups.Use [Indexer]",
        "Method System.Collections.Generic.List<int>.get_Item <- HarmonyMod.MemberLookups.Use [DeclaredIndexerGetter]",
        // An indexer of a class in another assembly carries C#'s default name, and a setter's value
        // type is unknown.
        "Prefix System.Collections.Generic.List<int>.get_Item(int) <- HarmonyMod.ExternalIndexerGetterPatch.Prefix [TypedAttribute]",
        // An indexer lookup passed to Harmony.Patch is read as its target, and that lookup has no
        // line of its own; the same lookup made elsewhere has one.
        "Postfix System.Collections.Generic.List<int>.get_Item(int) <- HarmonyMod.ExternalPatchCalls.After [PatchCall in HarmonyMod.ExternalPatchCalls.Apply]",
        "Method System.Collections.Generic.List<int>.get_Item(int) <- HarmonyMod.MemberLookups.Use [IndexerGetter]",
        "Method System.Collections.Generic.List<int>.set_Item <- HarmonyMod.MemberLookups.Use [DeclaredIndexerSetter]",
        "Prefix System.Collections.Generic.List<int>.set_Item(int, ?) <- HarmonyMod.ExternalIndexerSetterPatch.Prefix [TypedAttribute]",
        "Method System.Collections.Generic.List<int>.set_Item(int, ?) <- HarmonyMod.MemberLookups.Use [IndexerSetter]",
        "Method System.Console.Beep <- HarmonyMod.DelegatePatches.Before [Injected]",
        "Event System.Console.CancelKeyPress <- HarmonyMod.MemberLookups.Use [GetEvent]",
        // A method a lambda names to CodeInstruction.Call, CodeMatch.Calls or CodeMatch's constructor
        // is pinned to its parameter types, also where the lambda makes a delegate from a method
        // group, as for SymbolExtensions.GetMethodInfo.
        "Method System.Console.Clear() <- HarmonyMod.TranspilerHelpers.Transpiler [Call]",
        "Method System.Console.ResetColor() <- HarmonyMod.TranspilerHelpers.Matches [CodeMatch]",
        "Method System.Console.WriteLine() <- HarmonyMod.TranspilerHelpers.Transpiler [Call]",
        "Method System.Console.add_CancelKeyPress <- HarmonyMod.Harmony24Lookups.Use [DeclaredEvent]",
        "Prefix System.Console.remove_CancelKeyPress <- HarmonyMod.EventTargetPatch.Prefix [TargetMethod]",
        "Method System.Convert.ToInt32(string) <- HarmonyMod.TranspilerHelpers.Transpiler [Call]",
        "Field System.DBNull.Value <- HarmonyMod.TranspilerHelpers.Matches [LoadsField]",
        // A framework class named as text is found, and the class nested in it read.
        "Postfix System.Environment.GetFolderPath(SpecialFolder) <- HarmonyMod.UnreadNestedLookups.Framework [PatchCall in HarmonyMod.UnreadNestedLookups.Apply]",
        "Field System.EventArgs.Empty <- HarmonyMod.TranspilerHelpers.UntypedField [GetFieldInfo]",
        "Method System.GC.Collect() <- HarmonyMod.TranspilerHelpers.Matches [Calls]",
        "Method System.GC.KeepAlive <- HarmonyMod.DelegatePatches.BeforeCall [Injected]",
        "Method System.GC.WaitForPendingFinalizers() <- HarmonyMod.TranspilerHelpers.MethodGroup [GetMethodInfo]",
        // A method group on an object a call makes names the group's method, not the call, also on a
        // struct, whose method group the decompiler writes as a pointer's member.
        "Method System.Guid.ToByteArray() <- HarmonyMod.TranspilerHelpers.Transpiler [Call]",
        // A lookup of another assembly's method wrapped in EnumeratorMoveNext or AsyncMoveNext patches
        // its state machine and adds no line of its own.
        "Postfix System.IO.File.ReadLines(string) (Enumerator) <- HarmonyMod.EnumeratorTargetPatch.Postfix [TargetMethod]",
        // Harmony 2.4's finalizer lookups name Finalize, made to call it, as a TargetMethod's target
        // and in a Harmony.Patch call, by typeof and in the extension spelling.
        "Method System.IO.FileStream.Finalize <- HarmonyMod.Harmony24Lookups.Use [AccessToolsFinalizer]",
        "Postfix System.IO.FileStream.Finalize <- HarmonyMod.Harmony24PatchCalls.After [PatchCall in HarmonyMod.Harmony24PatchCalls.Apply]",
        "Postfix System.IO.Stream.CopyToAsync(Stream) (Async) <- HarmonyMod.Harmony24PatchCalls.After [PatchCall in HarmonyMod.Harmony24PatchCalls.Apply]",
        // A type not read stays '?' when passed by reference.
        "Method System.Int32.TryParse(string, ?) <- HarmonyMod.ByReferenceLookups.ReadUnread [GetMethod]",
        "Prefix System.Int32.TryParse(string, ref int) <- HarmonyMod.ByReferenceTargetPatch.Prefix [TargetMethod]",
        // A method lookup the code makes generic right at the call names the type arguments.
        "Method System.Linq.Enumerable.Cast<string> <- HarmonyMod.GenericLookups.Use [GetMethod]",
        // Lookups of a static constructor made to call what they find. A flag no call passes gives
        // both constructors, and with parameter types only the instance one, which can take them.
        "Method System.Random..cctor <- HarmonyMod.StaticConstructorLookups.Read [AccessToolsConstructor]",
        "Method System.Random..cctor <- HarmonyMod.StaticConstructorLookups.Read [GetConstructors]",
        "Method System.Random..cctor <- HarmonyMod.StaticConstructorLookups.Use [TypeInitializer]",
        "Method System.Random..ctor <- HarmonyMod.StaticConstructorLookups.Read [GetConstructors]",
        "Method System.Random..ctor() <- HarmonyMod.StaticConstructorLookups.Read [AccessToolsConstructor]",
        "Method System.Random..ctor(int) <- HarmonyMod.StaticConstructorLookups.Read [AccessToolsConstructor]",
        "Method System.Random.Next <- HarmonyMod.DelegatePatches.Before [Injected]",
        "Field System.String.Empty <- HarmonyMod.TranspilerHelpers.Field [GetFieldInfo]",
        "Method System.String.IsNullOrEmpty(string) <- HarmonyMod.TranspilerHelpers.Matches [Calls]",
        "Method System.Text.StringBuilder..cctor <- HarmonyMod.StaticConstructorLookups.Use [GetDeclaredConstructors]",
        // The static constructor from .NET's own lookup, passed to Harmony.Patch, is read as its target.
        "Postfix System.Text.StringBuilder..cctor <- HarmonyMod.StaticConstructorPatchCalls.After [PatchCall in HarmonyMod.StaticConstructorPatchCalls.Apply]",
        "Method System.Text.StringBuilder..cctor(int) <- HarmonyMod.StaticConstructorLookups.Use [AccessToolsConstructor]",
        // An enumeration of a class's constructors, and a search among them, name no overload.
        "Method System.Text.StringBuilder..ctor <- HarmonyMod.MemberLookups.Use [GetConstructors]",
        "Method System.Text.StringBuilder..ctor <- HarmonyMod.MemberLookups.Use [FirstConstructor]",
        // A constructor lookup, Harmony's or .NET's own, with the parameter types it pins.
        "Method System.Text.StringBuilder..ctor(int) <- HarmonyMod.MemberLookups.Use [AccessToolsConstructor]",
        // AccessTools.allDeclared holds Static and Instance, and only an instance constructor takes parameters.
        "Method System.Text.StringBuilder..ctor(int, int) <- HarmonyMod.StaticConstructorLookups.Use [GetConstructor]",
        "Method System.Text.StringBuilder..ctor(string) <- HarmonyMod.MemberLookups.Use [DeclaredConstructor]",
        "Method System.Text.StringBuilder..ctor(string) <- HarmonyMod.MemberLookups.Use [GetConstructor]",
        // An enumeration of a class's methods or properties, a search among them and a list of their
        // names name the class alone; all of a class's members are a Member line.
        "Method System.Text.StringBuilder.? <- HarmonyMod.MemberLookups.Use [GetMethods]",
        "Property System.Text.StringBuilder.? <- HarmonyMod.MemberLookups.Use [GetProperties]",
        "Member System.Text.StringBuilder.? <- HarmonyMod.MemberLookups.Use [GetMembers]",
        "Method System.Text.StringBuilder.? <- HarmonyMod.MemberLookups.Use [GetDeclaredMethods]",
        "Method System.Text.StringBuilder.? <- HarmonyMod.MemberLookups.Use [GetMethodNames]",
        "Property System.Text.StringBuilder.? <- HarmonyMod.MemberLookups.Use [GetPropertyNames]",
        "Method System.Text.StringBuilder.? <- HarmonyMod.MemberLookups.Use [FirstMethod]",
        "Property System.Text.StringBuilder.? <- HarmonyMod.MemberLookups.Use [FirstProperty]",
        "Method System.Text.StringBuilder.? <- HarmonyMod.MemberLookups.Use [TraverseMethods]",
        "Property System.Text.StringBuilder.? <- HarmonyMod.MemberLookups.Use [TraverseProperties]",
        // '?' stands for the name when it comes from a parameter no call passes.
        "Method System.Text.StringBuilder.? <- HarmonyMod.PartlyReadMembers.Read [AccessToolsMethod]",
        // A lookup is listed under the member the decompiler shows it in, here a property getter.
        "Method System.Text.StringBuilder.Append(bool) <- HarmonyMod.LookupPlacesPatch.get_Append [AccessToolsMethod]",
        "Method System.Text.StringBuilder.Append(char) <- HarmonyMod.DelegatePatches.Before [Injected]",
        "Method System.Text.StringBuilder.Append(char) <- HarmonyMod.DelegatePatches.Catch [Injected]",
        "Method System.Text.StringBuilder.Append(char) <- HarmonyMod.MemberLookups.Use [GetMethod]",
        // The delegate parameters of patch classes named as text, read from the classes the texts name.
        "Method System.Text.StringBuilder.Append(char) <- HarmonyMod.MethodTextPatchClass.Postfix [Injected]",
        "Method System.Text.StringBuilder.Append(char) <- HarmonyMod.TextNamedPatchClass.Postfix [Injected]",
        "Method System.Text.StringBuilder.Append(char) <- HarmonyMod.TranspilerHelpers.Transpiler [Call]",
        "Method System.Text.StringBuilder.Append(string) <- HarmonyMod.MemberLookups.Use [AccessToolsMethod]",
        // A lookup passed straight to Harmony.Patch is the patch's target and has no line of its own.
        "Postfix System.Text.StringBuilder.AppendLine() <- HarmonyMod.ExternalPatchCalls.After [PatchCall in HarmonyMod.ExternalPatchCalls.Apply]",
        "Method System.Text.StringBuilder.AppendLine() <- HarmonyMod.TranspilerHelpers.Transpiler [Call]",
        // A Traverse's property, in the class of the object the Traverse was created from.
        "Property System.Text.StringBuilder.Capacity <- HarmonyMod.MemberLookups.Use [TraverseProperty]",
        // The lookup a TargetMethod returns has no line; a second lookup of the method it patches,
        // made elsewhere to call it, keeps its own.
        "Method System.Text.StringBuilder.Clear <- HarmonyMod.ExternalPatchCalls.Apply [AccessToolsMethod]",
        "Postfix System.Text.StringBuilder.Clear <- HarmonyMod.ExternalTargetPatch.Postfix [TargetMethod]",
        // Harmony's extension spelling takes the label of the call it spells.
        "Method System.Text.StringBuilder.Clear <- HarmonyMod.MemberLookups.Use [AccessToolsMethod]",
        "Method System.Text.StringBuilder.Clear <- HarmonyMod.TranspilerHelpers.Transpiler [Call]",
        // SymbolExtensions.GetMethodInfo names the overload its lambda calls.
        "Method System.Text.StringBuilder.Clear() <- HarmonyMod.MemberLookups.Use [GetMethodInfo]",
        // Two Harmony.Patch calls pass a lookup of their own of one method through a helper that hands
        // it back; each patch line shows its call's lookup, so neither lookup has a line of its own.
        "Postfix System.Text.StringBuilder.EnsureCapacity <- HarmonyMod.FirstPassedPatch.AfterAny [PatchCall in HarmonyMod.FirstPassedPatch.Apply]",
        "Postfix System.Text.StringBuilder.EnsureCapacity <- HarmonyMod.SecondPassedPatch.AfterAny [PatchCall in HarmonyMod.SecondPassedPatch.Apply]",
        "Method System.Text.StringBuilder.EnsureCapacity(int) <- HarmonyMod.MemberLookups.Use [DeclaredMethod]",
        "Method System.Text.StringBuilder.ExpandByABlock <- HarmonyMod.MemberLookups.Use [DeclaredMethod]",
        // A Traverse's method, called with arguments and with none.
        "Method System.Text.StringBuilder.ExpandByABlock <- HarmonyMod.MemberLookups.Use [TraverseMethod]",
        "Method System.Text.StringBuilder.FormatError <- HarmonyMod.MemberLookups.Use [TraverseMethod]",
        // A lookup kept in a static field that Harmony.Patch is given has no line either.
        "Postfix System.Text.StringBuilder.Insert(int, string) <- HarmonyMod.ExternalPatchCalls.After [PatchCall in HarmonyMod.ExternalPatchCalls.Apply]",
        // A Traverse's method with parameter types, which pin the overload.
        "Method System.Text.StringBuilder.Insert(int, string) <- HarmonyMod.MemberLookups.Use [TraverseMethod]",
        "Method System.Text.StringBuilder.Insert(int, string) <- HarmonyMod.TranspilerHelpers.Matches [AccessToolsMethod]",
        // A property lookup names the property.
        "Property System.Text.StringBuilder.Length <- HarmonyMod.MemberLookups.Use [AccessToolsProperty]",
        "Property System.Text.StringBuilder.Length <- HarmonyMod.MemberLookups.Use [TraverseProperty]",
        "Property System.Text.StringBuilder.MaxCapacity <- HarmonyMod.MemberLookups.Use [GetProperty]",
        // A lookup held in a local that Harmony.Patch is given has no line.
        "Postfix System.Text.StringBuilder.Remove <- HarmonyMod.ExternalPatchCalls.After [PatchCall in HarmonyMod.ExternalPatchCalls.Apply]",
        "Method System.Text.StringBuilder.Remove <- HarmonyMod.MemberLookups.Use [GetMethod]",
        // A lookup in a transpiler is listed like any other.
        "Method System.Text.StringBuilder.Replace(string, string) <- HarmonyMod.LookupPlacesPatch.Transpiler [AccessToolsMethod]",
        // A class named as text, "Namespace.Type:Member", and a delegate made from such a name.
        "Method System.Text.StringBuilder.ToString <- HarmonyMod.MemberLookups.Use [AccessToolsMethod]",
        "Method System.Text.StringBuilder.ToString <- HarmonyMod.MemberLookups.Use [MethodDelegate]",
        // A delegate made from a method already looked up adds no line; the lookup inside it has one.
        "Method System.Text.StringBuilder.ToString <- HarmonyMod.PartlyReadMembers.Read [AccessToolsMethod]",
        // A helper looks a method up in the class its caller passes. For the class whose method is
        // patched that lookup has no line; for the other class, System.Version below, it has one.
        "Postfix System.Text.StringBuilder.ToString() <- HarmonyMod.ExternalPatchCalls.After [PatchCall in HarmonyMod.ExternalPatchCalls.Apply]",
        "Method System.Text.StringBuilder.get_Capacity <- HarmonyMod.DelegatePatches.After [Injected]",
        // An accessor lookup names the accessor.
        "Method System.Text.StringBuilder.get_Capacity <- HarmonyMod.MemberLookups.Use [PropertyGetter]",
        "Method System.Text.StringBuilder.get_MaxCapacity <- HarmonyMod.MemberLookups.Use [DeclaredPropertyGetter]",
        // A type from another assembly is qualified from the assembly's reference to it.
        "Field System.Text.StringBuilder.m_ChunkChars <- HarmonyMod.FieldAccessPatch..cctor [AccessToolsField]",
        // A Traverse's field is listed as a field, also where a method is looked up through it.
        "Field System.Text.StringBuilder.m_ChunkPrevious <- HarmonyMod.PartlyReadMembers.Read [TraverseField]",
        "Method System.Text.StringBuilder.set_Capacity <- HarmonyMod.MemberLookups.Use [PropertySetter]",
        // A property lookup whose accessor the code takes right at the call names that accessor,
        // under the property lookup's label.
        "Method System.Text.StringBuilder.set_Capacity <- HarmonyMod.MemberLookups.Use [AccessToolsProperty]",
        // The accessor of a property looked up, passed to Harmony.Patch: the property lookup has no line.
        "Postfix System.Text.StringBuilder.set_Length <- HarmonyMod.ExternalPatchCalls.After [PatchCall in HarmonyMod.ExternalPatchCalls.Apply]",
        "Method System.Text.StringBuilder.set_Length <- HarmonyMod.MemberLookups.Use [DeclaredPropertySetter]",
        "Field System.Type.Missing <- HarmonyMod.TranspilerHelpers.Matches [StoresField]",
        "Method System.Version..cctor <- HarmonyMod.StaticConstructorLookups.Use [GetConstructor]",
        "Method System.Version..cctor <- HarmonyMod.StaticConstructorLookups.Use [GetConstructors]",
        "Postfix System.Version..cctor <- HarmonyMod.StaticConstructorPatchCalls.After [PatchCall in HarmonyMod.StaticConstructorPatchCalls.Apply]",
        "Method System.Version..ctor <- HarmonyMod.MemberLookups.Use [GetDeclaredConstructors]",
        // A constructor lookup without parameter types finds the parameterless constructor, as
        // .NET's own does with an empty array.
        "Method System.Version..ctor() <- HarmonyMod.MemberLookups.Use [AccessToolsConstructor]",
        "Method System.Version..ctor() <- HarmonyMod.MemberLookups.Use [GetConstructor]",
        // A lookup in an iterator.
        "Method System.Version..ctor(int, int) <- HarmonyMod.LookupPlacesPatch.Constructors [AccessToolsConstructor]",
        // A constructor from .NET's own lookup, passed to Harmony.Patch, is read as its target.
        "Postfix System.Version..ctor(string) <- HarmonyMod.ExternalPatchCalls.After [PatchCall in HarmonyMod.ExternalPatchCalls.Apply]",
        "Property System.Version.? <- HarmonyMod.MemberLookups.Use [GetDeclaredProperties]",
        "Method System.Version.? <- HarmonyMod.MemberLookups.Use [GetMethodNames]",
        "Property System.Version.? <- HarmonyMod.MemberLookups.Use [GetPropertyNames]",
        // A lookup in a lambda.
        "Method System.Version.Clone <- HarmonyMod.LookupPlacesPatch.Postfix [AccessToolsMethod]",
        "Method System.Version.CompareTo <- HarmonyMod.DelegatePatches.Fetch [HarmonyDelegate]",
        "Property System.Version.Major <- HarmonyMod.MemberLookups.Use [DeclaredProperty]",
        // A Traverse of a class's static members, created from the class by typeof or by a type argument.
        "Method System.Version.Parse <- HarmonyMod.MemberLookups.Use [TraverseMethod]",
        "Property System.Version.Revision <- HarmonyMod.MemberLookups.Use [TraverseProperty]",
        // The helper's lookup in the class whose method it only calls.
        "Method System.Version.ToString() <- HarmonyMod.ExternalPatchCalls.Find [AccessToolsMethod]",
        "Field System.Version._Build <- HarmonyMod.TranspilerHelpers.Transpiler [LoadField]",
        "Field System.Version._Minor <- HarmonyMod.TranspilerHelpers.Matches [AccessToolsField]",
        "Field System.Version._Revision <- HarmonyMod.TranspilerHelpers.Transpiler [StoreField]",
        "Method System.Version.get_Build <- HarmonyMod.MemberLookups.Use [GetProperty]",
        "Method System.Version.get_Minor <- HarmonyMod.MemberLookups.Use [AccessToolsProperty]",
        "Prefix System.WeakReference.Finalize <- HarmonyMod.FinalizerTargetPatch.Prefix [TargetMethod]",
        "Method System.WeakReference.Finalize <- HarmonyMod.Harmony24Lookups.Use [DeclaredFinalizer]",
    ];

    [Fact]
    public async Task FindHarmonyDependencies_OnTheFixture_ListsEveryDependencyByTarget()
    {
        string text = await CallToolText("find_harmony_dependencies", new()
        {
            ["assembly"] = _harmonyModPath,
            ["limit"] = FixtureDependencies.Length,
        });

        Assert.Equal(FixtureDependencies, text.Split('\n'));
    }

    [Fact]
    public async Task FindHarmonyDependencies_WithHarmonyLibLoaded_ListsTheSameDependencies()
    {
        // With HarmonyLib among the references it can load, ILSpy writes Harmony's extension
        // spelling, typeof(X).Method("Y"), as an extension call rather than a static
        // AccessToolsExtensions call, drops arguments left at their defaults, and resolves the
        // overload a method group picks.
        string text = await CallToolText("find_harmony_dependencies", new()
        {
            ["assembly"] = _harmonyModPath,
            ["limit"] = FixtureDependencies.Length,
        }, _references.Client);

        Assert.Equal(FixtureDependencies, text.Split('\n'));
    }

    // The members lambdas name in pattern 52, to a transpiler's helpers and to GetMethodInfo.
    private static readonly string[] LambdaLines =
    [
        "Field HarmonyMod.TargetA.viewHeight <- HarmonyMod.TranspilerHelpers.Matches [LoadsField]",
        "Method System.Console.Clear() <- HarmonyMod.TranspilerHelpers.Transpiler [Call]",
        "Method System.Console.ResetColor() <- HarmonyMod.TranspilerHelpers.Matches [CodeMatch]",
        "Method System.Console.WriteLine() <- HarmonyMod.TranspilerHelpers.Transpiler [Call]",
        "Method System.Convert.ToInt32(string) <- HarmonyMod.TranspilerHelpers.Transpiler [Call]",
        "Field System.DBNull.Value <- HarmonyMod.TranspilerHelpers.Matches [LoadsField]",
        "Field System.EventArgs.Empty <- HarmonyMod.TranspilerHelpers.UntypedField [GetFieldInfo]",
        "Method System.GC.Collect() <- HarmonyMod.TranspilerHelpers.Matches [Calls]",
        "Method System.GC.WaitForPendingFinalizers() <- HarmonyMod.TranspilerHelpers.MethodGroup [GetMethodInfo]",
        "Method System.Guid.ToByteArray() <- HarmonyMod.TranspilerHelpers.Transpiler [Call]",
        "Field System.String.Empty <- HarmonyMod.TranspilerHelpers.Field [GetFieldInfo]",
        "Method System.String.IsNullOrEmpty(string) <- HarmonyMod.TranspilerHelpers.Matches [Calls]",
        "Method System.Text.StringBuilder.AppendLine() <- HarmonyMod.TranspilerHelpers.Transpiler [Call]",
        "Field System.Type.Missing <- HarmonyMod.TranspilerHelpers.Matches [StoresField]",
    ];

    [Fact]
    public async Task FindHarmonyDependencies_WhereTheDecompilerWritesAnExpressionTreesConstruction_NamesWhatTheLambdaNames()
    {
        // Beside a netstandard that defines no class the decompiler cannot resolve the reflection
        // types an expression tree is built from, as for an assembly built against .NET 10 read
        // beside an older System.Runtime, and writes each lambda as the tree's construction. The
        // member is then read from the token the code passes, and the lines are those the lambdas
        // give where the decompiler writes them out.
        Assert.All(LambdaLines, line => Assert.Contains(line, FixtureDependencies));
        string root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "ILens-constructions-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            File.Copy(_harmonyModPath, Path.Combine(root, "HarmonyMod.dll"));
            EmptyCoreAssembly.Write(root);
            await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "ILens-constructions",
                Command = ILensServerFixture.ResolveILensExecutable(),
                Arguments = ["--allow-root", root],
            }));
            string assembly = Path.Combine(root, "HarmonyMod.dll");
            string code = await CallToolText("decompile", new()
            {
                ["assembly"] = assembly,
                ["type_name"] = "HarmonyMod.TranspilerHelpers",
                ["member_name"] = "Field",
            }, client);
            Assert.Contains("Expression.Lambda", code);

            string text = await CallToolText("find_harmony_dependencies", new()
            {
                ["assembly"] = assembly,
                ["limit"] = 1000,
            }, client);
            string[] lines = text.Split('\n');
            Assert.All(LambdaLines, line => Assert.Contains(line, lines));
        }
        finally
        {
            await ILensServerFixture.DeleteWhenReleased(root);
        }
    }

    [Fact]
    public async Task FindHarmonyDependencies_OnHarmony3Lookups_NamesTheMethodWithTheKindOfGeneratedCode()
    {
        // Harmony 3.0's lookups of a method's lambdas, a local function and a state machine's MoveNext
        // name the method with the kind after it, and the lookup of the method adds no line of its
        // own; made to call what they find, they leave the line of the lookup inside them.
        string text = await CallToolText("find_harmony_dependencies", new()
        {
            ["assembly"] = Path.Combine(Path.GetDirectoryName(_harmonyModPath)!, "HarmonyMod3.dll"),
        });

        Assert.Equal(
        [
            "11 dependencies:",
            "Postfix System.Console.ReadLine (Lambdas) <- HarmonyMod3.PatchCalls.After [PatchCall in HarmonyMod3.PatchCalls.Apply]",
            "Method System.Environment.GetEnvironmentVariables <- HarmonyMod3.Calls.Use [AccessToolsMethod]",
            "Method System.IO.Directory.EnumerateDirectories(string) <- HarmonyMod3.Calls.Use [AccessToolsMethod]",
            "Postfix System.IO.Directory.EnumerateFiles(string) (StateMachine) <- HarmonyMod3.StateMachinePatch.Postfix [TargetMethod]",
            "Prefix System.IO.File.Copy (LocalFunction ?) <- HarmonyMod3.UnknownNamePatch.Prefix [TargetMethod]",
            "Prefix System.IO.File.ReadAllBytes (LocalFunction Fill(byte[], int)) <- HarmonyMod3.LocalFunctionTypesPatch.Prefix [TargetMethod]",
            "Prefix System.IO.File.ReadAllText(string) (LocalFunction Read) <- HarmonyMod3.LocalFunctionPatch.Prefix [TargetMethod]",
            "Prefix System.IO.File.ReadLines(string) (Lambdas) <- HarmonyMod3.LambdasPatch.Prefix [TargetMethods]",
            "Postfix System.IO.File.ReadLines(string) (StateMachine) <- HarmonyMod3.PatchCalls.After [PatchCall in HarmonyMod3.PatchCalls.Apply]",
            "Postfix System.IO.File.WriteAllText(string, string) (LocalFunction Write) <- HarmonyMod3.PatchCalls.After [PatchCall in HarmonyMod3.PatchCalls.Apply]",
            "Method System.IO.Path.Combine(string, string) <- HarmonyMod3.Calls.Use [AccessToolsMethod]",
        ], text.Split('\n'));
    }

    [Fact]
    public async Task FindHarmonyDependencies_OnANonHarmonyAssembly_SaysThereAreNone()
    {
        string text = await CallToolText("find_harmony_dependencies", new()
        {
            ["assembly"] = _server.AssemblyPath,
        });

        Assert.Equal("No dependencies", text);
    }

    [Fact]
    public async Task FindHarmonyDependencies_CutByLimit_CountsEveryDependency()
    {
        string text = await CallToolText("find_harmony_dependencies", new()
        {
            ["assembly"] = _harmonyModPath,
            ["limit"] = 2,
        });

        Assert.Equal(
            FixtureDependencies.Take(3).Append("... (371 more; raise limit to see all)"),
            text.Split('\n'));
    }

    [Fact]
    public async Task FindHarmonyDependencies_OnAResolverThatCallsItselfInWhatItReturns_AnswersInSeconds()
    {
        // Pattern 55's resolver calls itself in what it returns, as the one Harmony's own library ships
        // does, and each of its five lookups is read for every chain of its calls. A scan that reads what
        // an own method returns anew for every call took over four minutes on this fixture, one that keeps
        // it for each set of values about a second, so the bound sits far from both.
        var watch = Stopwatch.StartNew();
        string text = await CallToolText("find_harmony_dependencies", new()
        {
            ["assembly"] = _harmonyModPath,
            ["limit"] = 0,
        });
        watch.Stop();

        Assert.StartsWith(FixtureDependencies[0], text);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30),
            $"find_harmony_dependencies took {watch.Elapsed.TotalSeconds:F0} s on the fixture.");
    }

    [Theory]
    [InlineData("HarmonyMod.TargetC.Nested", "public class HarmonyMod.TargetC.Nested")]
    [InlineData("HarmonyMod.TargetD<int>", "public class HarmonyMod.TargetD<T>")]
    public async Task FindHarmonyDependencies_NamesATargetTypeTheOtherToolsTake(string printed, string declaration)
    {
        // A target type is printed so that a caller can pass it on unchanged: a nested type
        // with '.', and a generic one with the type argument that picks it among its namesakes.
        Assert.Contains(FixtureDependencies, line => line.Contains($" {printed}."));

        string members = await CallToolText("list_members", new()
        {
            ["assembly"] = _harmonyModPath,
            ["type_name"] = printed,
            ["accessibility"] = "all",
        });

        Assert.Matches($@"^\d+ members? of {System.Text.RegularExpressions.Regex.Escape(declaration)}:", members);
    }

    [Theory]
    [InlineData("TargetE", "add_Changed", null, "// HarmonyMod.TargetE.add_Changed(EventHandler) → void")]
    [InlineData("TargetE", "remove_Changed", null, "// HarmonyMod.TargetE.remove_Changed(EventHandler) → void")]
    [InlineData("TargetE", "op_Addition", new[] { "TargetE", "TargetE" }, "// HarmonyMod.TargetE.op_Addition(TargetE, TargetE) → TargetE")]
    [InlineData("TargetE", "Finalize", null, "// HarmonyMod.TargetE.Finalize() → void")]
    [InlineData("TargetF", "get_Item", new[] { "int" }, "// HarmonyMod.TargetF.get_Item(int) → int")]
    [InlineData("TargetF", "set_Item", new[] { "string", "string" }, "// HarmonyMod.TargetF.set_Item(string, string) → void")]
    [InlineData("TargetG", "get_Cell", null, "// HarmonyMod.TargetG.get_Cell(int, int) → int")]
    [InlineData("TargetG", "set_Cell", new[] { "int", "int", "int" }, "// HarmonyMod.TargetG.set_Cell(int, int, int) → void")]
    [InlineData("TargetA", ".ctor", new string[0], "// HarmonyMod.TargetA..ctor() → void")]
    [InlineData("TargetH", ".cctor", null, "// HarmonyMod.TargetH..cctor() → void")]
    [InlineData("TargetH", "set_Size", null, "// HarmonyMod.TargetH.set_Size(int) → void")]
    [InlineData("TargetH.Inner", "Describe", null, "// HarmonyMod.TargetH.Inner.Describe() → void")]
    public async Task FindHarmonyDependencies_NamesAPatchedMethodTheOtherToolsTake(
        string type, string member, string[]? parameterTypes, string reference)
    {
        // A method Harmony binds by its kind, or a lookup finds, is printed by its name in metadata,
        // so a caller can pass the name, and the parameter types that pin an overload, on unchanged.
        string printed = parameterTypes is null ? member : $"{member}({string.Join(", ", parameterTypes)})";
        Assert.Contains(FixtureDependencies, line => line.Contains($" HarmonyMod.{type}.{printed} <- "));

        var arguments = new Dictionary<string, object?>
        {
            ["assembly"] = _harmonyModPath,
            ["type_name"] = $"HarmonyMod.{type}",
            ["member_name"] = member,
        };
        if (parameterTypes is not null)
            arguments["parameter_types"] = parameterTypes;
        string code = await CallToolText("decompile", arguments);

        Assert.Equal(reference, code.Split('\n')[0]);
    }

    [Theory]
    [InlineData("TargetL", "Parse<int>", null, "// HarmonyMod.TargetL.Parse<T>(string) → T")]
    [InlineData("TargetM", "Convert<int, string>", null, "// HarmonyMod.TargetM.Convert<T, U>(U) → T")]
    [InlineData("TargetM", "Make<?>", null, "// HarmonyMod.TargetM.Make<T>() → T")]
    [InlineData("TargetM", "Make<long>", new string[0], "// HarmonyMod.TargetM.Make<T>() → T")]
    [InlineData("TargetM", "Store<int>", new[] { "T" }, "// HarmonyMod.TargetM.Store<T>(T) → void")]
    [InlineData("TargetM", "Store<string>", null, "// HarmonyMod.TargetM.Store<T>(T) → void")]
    public async Task FindHarmonyDependencies_NamesAGenericMethodMadeForTypeArguments_SoDecompileAndCompareTakeIt(
        string type, string member, string[]? parameterTypes, string reference)
    {
        // The type arguments follow the method's name as the code makes the method, and the name
        // passes on as member_name with them, together with the parameter types that pin it.
        string printed = parameterTypes is null ? member : $"{member}({string.Join(", ", parameterTypes)})";
        Assert.Contains(FixtureDependencies, line => line.Contains($" HarmonyMod.{type}.{printed} <- "));

        var question = new Dictionary<string, object?>
        {
            ["assembly"] = _harmonyModPath,
            ["type_name"] = $"HarmonyMod.{type}",
            ["member_name"] = member,
        };
        if (parameterTypes is not null)
            question["parameter_types"] = parameterTypes;
        string code = await CallToolText("decompile", question);
        Assert.Equal(reference, code.Split('\n')[0]);

        var comparison = new Dictionary<string, object?>
        {
            ["assembly_a"] = _harmonyModPath,
            ["assembly_b"] = Path.Combine(Path.GetDirectoryName(_harmonyModPath)!, "HarmonyMod2.dll"),
            ["type_name"] = $"HarmonyMod.{type}",
            ["member_name"] = member,
        };
        if (parameterTypes is not null)
            comparison["parameter_types"] = parameterTypes;
        Assert.Equal($"{reference} — bodies are identical in both assemblies.", await CallToolText("compare", comparison));
    }

    private async Task<string> CallToolText(
        string toolName, Dictionary<string, object?> arguments, McpClient? client = null)
    {
        CallToolResult result = await (client ?? _server.Client).CallToolAsync(toolName, arguments);
        string text = string.Join(
            "\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.True(result.IsError != true, $"Tool '{toolName}' returned an error: {text}");
        return text;
    }
}
