using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Tests;

/// <summary>
/// End-to-end tests for find_harmony_dependencies. The fixture under
/// Tests/Fixtures/HarmonyMod/ compiles to HarmonyMod.dll alongside the
/// shared inspection target — exercising every pattern the scanner must
/// extract (typed and string-targeted attributes, overload-pinning param
/// arrays, patch methods marked by attribute with targets of their own,
/// TargetMethod/TargetMethods bodies, Harmony.Patch calls, field lookups in any
/// class, ___name parameters). The shared server cannot load HarmonyLib, the
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
    // declaration shortens it.
    private static readonly string[] FixtureDependencies =
    [
        "155 dependencies:",
        // A loop over a class's subclasses, over values the code is given, or over a list a call may
        // add to leaves the target unread; the label names the member to look at.
        "Postfix ? <- HarmonyMod.LoopPatches.AfterAny [PatchCall in HarmonyMod.LoopPatches.Apply]",
        "Postfix ? <- HarmonyMod.LoopPatches.AfterAny [PatchCall in HarmonyMod.LoopPatches.Apply]",
        "Postfix ? <- HarmonyMod.LoopPatches.AfterAny [PatchCall in HarmonyMod.LoopPatches.Apply]",
        // A lookup the scanner cannot read beside one it can adds '?', so the list says it is not
        // complete; in a TargetMethod that makes the binding branch-dependent.
        "Postfix ? <- HarmonyMod.PartlyReadExtensionsPatch.Postfix [TargetMethods]",
        // A field lookup the scanner cannot read is listed with '?' for each part it cannot read:
        // here both, for the fields of an object typed as object.
        "Field ? <- HarmonyMod.PartlyReadFields.Read [GetFieldNames]",
        "Prefix ? <- HarmonyMod.PartlyReadTargetPatch.Prefix [DynamicTargetMethod]",
        "Postfix ? <- HarmonyMod.PartlyReadTargetsPatch.Postfix [TargetMethods]",
        "Prefix ? <- HarmonyMod.UnreadInjectedFieldPatch.Prefix [DynamicTargetMethod]",
        // '?' stands for the class when it comes from a property, from a parameter no call in the
        // assembly passes, from an object typed as object, or from a Traverse reached through another
        // Field call, and when a ___name parameter belongs to a patch whose target is not read.
        "Field ?.count <- HarmonyMod.PartlyReadFields.Read [AccessToolsField]",
        "Field ?.count <- HarmonyMod.PartlyReadFields.Read [TraverseField]",
        "Field ?.count <- HarmonyMod.PartlyReadFields.Reassign [AccessToolsField]",
        "Field ?.count <- HarmonyMod.UnreadInjectedFieldPatch.Prefix [Injected]",
        "Field ?.x <- HarmonyMod.PartlyReadFields.Read [TraverseField]",
        // A Traverse receiver is read past the casts ILSpy writes around it, and its class is the
        // one the decompiler gives the object: the type of the field info, the class an as cast
        // names, which ILSpy writes as a conditional when it cannot load that class.
        "Field HarmonyLib.HarmonyMethod.method <- HarmonyMod.CastReceiverPatch.Prefix [TraverseField]",
        "Field HarmonyLib.HarmonyPatch.info <- HarmonyMod.FieldUser.Inspect [TraverseField]",
        // A generic class from an assembly the decompiler cannot load keeps its name.
        "Field HarmonyLib.Traverse<System.Int32>.traverse <- HarmonyMod.FieldUser.Use [FieldRefAccess]",
        // A constructor lookup without parameter types finds the parameterless constructor.
        "Postfix HarmonyMod.TargetA..ctor() <- HarmonyMod.OtherLookupsPatch.Postfix [TargetMethods]",
        // The blob path: a MethodType.Constructor argument empties ILSpy's own decoding.
        "Prefix HarmonyMod.TargetA..ctor(List<int>) <- HarmonyMod.GenericConstructorPatch.Prefix [TypedAttribute]",
        "Postfix HarmonyMod.TargetA..ctor(List<int>) <- HarmonyMod.OtherLookupsPatch.Postfix [TargetMethods]",
        "Prefix HarmonyMod.TargetA..ctor(int) <- HarmonyMod.ConstructorPatch.Prefix [TypedAttribute]",
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
        // A field TargetMethod returns is read where it is assigned, here by its initializer.
        "Postfix HarmonyMod.TargetA.PrivateMethod <- HarmonyMod.InitializedTargetPatch.Postfix [TargetMethod]",
        // TargetMethod and TargetMethods are found by their attributes as well as by their names.
        "Postfix HarmonyMod.TargetA.PrivateMethod <- HarmonyMod.MarkedTargetMethodPatch.Postfix [TargetMethod]",
        // A reflection lookup by name is listed like AccessTools.Method without parameter types.
        "Postfix HarmonyMod.TargetA.PrivateMethod <- HarmonyMod.ReflectionTargetPatch.Postfix [TargetMethod]",
        // A private member can only be named by a string.
        "Postfix HarmonyMod.TargetA.PrivateMethod <- HarmonyMod.StringTargetedPatch.Postfix [StringTargeted]",
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
        // A constructor patch without argument types binds the parameterless constructor too.
        "Prefix HarmonyMod.TargetB..ctor() <- HarmonyMod.ParameterlessConstructorPatch.Prefix [TypedAttribute]",
        "Prefix HarmonyMod.TargetB.SomeMethod <- HarmonyMod.CastReceiverPatch.Prefix [TypedAttribute]",
        // A patch method's own [HarmonyPatch] may be all that names a target, as on a class
        // carrying only a category.
        "Postfix HarmonyMod.TargetB.SomeMethod <- HarmonyMod.CategorizedPatch.AfterSomeMethod [TypedAttribute]",
        "Prefix HarmonyMod.TargetB.SomeMethod <- HarmonyMod.DynamicTargetPatch.Prefix [DynamicTargetMethod]",
        "Prefix HarmonyMod.TargetB.SomeMethod <- HarmonyMod.FieldRefAccessPatch.Prefix [TypedAttribute]",
        // A list a call may add to still gives the names it is written with.
        "Postfix HarmonyMod.TargetB.SomeMethod <- HarmonyMod.LoopPatches.AfterAny [PatchCall in HarmonyMod.LoopPatches.Apply]",
        "Transpiler HarmonyMod.TargetB.SomeMethod <- HarmonyMod.MarkedKindsPatch.Rewrite [TypedAttribute]",
        "Finalizer HarmonyMod.TargetB.SomeMethod <- HarmonyMod.MarkedKindsPatch.Swallow [TypedAttribute]",
        "Postfix HarmonyMod.TargetB.SomeMethod <- HarmonyMod.MarkedTargetMethodsPatch.Postfix [TargetMethods]",
        "Postfix HarmonyMod.TargetB.SomeMethod <- HarmonyMod.MethodSearchesPatch.Postfix [TargetMethodsSearch]",
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
        "Prefix HarmonyMod.TargetD<System.Int32>.Run <- HarmonyMod.GenericTargetPatch.Prefix [TypedAttribute]",
        // Harmony 2.4 binds a finalizer or an operator by its kind alone, so typeof names all of the
        // target, though the finalizer is not public.
        "Prefix HarmonyMod.TargetE.Finalize <- HarmonyMod.FinalizeMethodPatch.Prefix [TypedAttribute]",
        // An event patch targets the accessor, and a public event counts as a public property does.
        "Prefix HarmonyMod.TargetE.add_Changed <- HarmonyMod.EventAdderPatch.Prefix [TypedAttribute]",
        "Prefix HarmonyMod.TargetE.op_Addition(TargetE, TargetE) <- HarmonyMod.OperatorPatch.Prefix [TypedAttribute]",
        "Prefix HarmonyMod.TargetE.remove_Changed <- HarmonyMod.EventRemoverPatch.Prefix [TypedAttribute]",
        // A getter or setter patch that names no property binds the accessor of the indexer with
        // the index types named, which counts as bound by its kind; a setter takes the value last.
        "Prefix HarmonyMod.TargetF.get_Item(int) <- HarmonyMod.IndexerGetterPatch.Prefix [TypedAttribute]",
        "Prefix HarmonyMod.TargetF.set_Item(string, string) <- HarmonyMod.IndexerSetterPatch.Prefix [TypedAttribute]",
        // Without index types it binds the only indexer, under the name the indexer was given.
        "Prefix HarmonyMod.TargetG.get_Cell <- HarmonyMod.RenamedIndexerGetterPatch.Prefix [TypedAttribute]",
        "Prefix HarmonyMod.TargetG.set_Cell(int, int, int) <- HarmonyMod.RenamedIndexerSetterPatch.Prefix [TypedAttribute]",
        // searchForStatic finds the static constructor.
        "Postfix HarmonyMod.TargetH..cctor <- HarmonyMod.OtherLookupsPatch.Postfix [TargetMethods]",
        // AccessTools.TypeByName names a nested class with '+', as an argument or held in a local.
        "Postfix HarmonyMod.TargetH.Inner.Describe <- HarmonyMod.LocalTypeLookupsPatch.Postfix [TargetMethods]",
        "Prefix HarmonyMod.TargetH.Inner.Describe <- HarmonyMod.TextLookupsPatch.Prefix [TargetMethods]",
        "Postfix HarmonyMod.TargetH.Items <- HarmonyMod.OtherLookupsPatch.Postfix [TargetMethods]",
        // A lookup wrapped in EnumeratorMoveNext or AsyncMoveNext patches the method's state machine.
        "Prefix HarmonyMod.TargetH.Items (Enumerator) <- HarmonyMod.StateMachineLookupsPatch.Prefix [TargetMethods]",
        "Prefix HarmonyMod.TargetH.Refresh (Async) <- HarmonyMod.StateMachineLookupsPatch.Prefix [TargetMethods]",
        "Prefix HarmonyMod.TargetH.get_Size <- HarmonyMod.TextLookupsPatch.Prefix [TargetMethods]",
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
        "Postfix HarmonyMod.TargetI.Run <- HarmonyMod.LoopPatches.AfterAny [PatchCall in HarmonyMod.LoopPatches.Apply]",
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
        "Field HarmonyMod.TargetI.count <- HarmonyMod.FieldHolder..cctor [FieldRefAccess]",
        "Field HarmonyMod.TargetI.count <- HarmonyMod.FieldHolder..cctor [FieldRefAccess]",
        "Field HarmonyMod.TargetI.count <- HarmonyMod.FieldHolder..cctor [GetField]",
        // A lookup in an iterator, which the decompiler shows in the method it is written in.
        "Field HarmonyMod.TargetI.count <- HarmonyMod.FieldUser.Fields [AccessToolsField]",
        "Field HarmonyMod.TargetI.count <- HarmonyMod.FieldUser.Use [FieldRefAccess]",
        "Field HarmonyMod.TargetI.count <- HarmonyMod.FieldUser.Use [AccessToolsField]",
        // A Traverse reads the class of the object it was created from, given directly, held in a
        // field or a local, and in the generic Field<T> as in Field.
        "Field HarmonyMod.TargetI.count <- HarmonyMod.FieldUser.Use [TraverseField]",
        "Field HarmonyMod.TargetI.count <- HarmonyMod.FieldUser.Use [TraverseField]",
        "Field HarmonyMod.TargetI.count <- HarmonyMod.FieldUser.Use [TraverseField]",
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
        // A lookup in a lambda, which the decompiler shows in the method it is written in.
        "Field HarmonyMod.TargetI.label <- HarmonyMod.AccessorFieldPatch.Postfix [AccessToolsField]",
        "Field HarmonyMod.TargetI.label <- HarmonyMod.FieldHolder..cctor [AccessToolsField]",
        "Field HarmonyMod.TargetI.label <- HarmonyMod.FieldHolder..cctor [AccessToolsField]",
        "Field HarmonyMod.TargetI.label <- HarmonyMod.FieldUser.Use [StaticFieldRefAccess]",
        "Field HarmonyMod.TargetI.label <- HarmonyMod.FieldUser.Use [GetField]",
        // A Traverse of a class's static fields, created from the class by typeof or as text.
        "Field HarmonyMod.TargetI.label <- HarmonyMod.FieldUser.Use [TraverseField]",
        "Field HarmonyMod.TargetI.label <- HarmonyMod.FieldUser.Use [TraverseField]",
        // A ___name parameter passed by ref, through which the patch writes the field.
        "Field HarmonyMod.TargetI.label <- HarmonyMod.InjectedFieldsPatch.Prefix [Injected]",
        "Field HarmonyMod.TargetI.total <- HarmonyMod.FieldHolder..cctor [AccessToolsField]",
        "Field HarmonyMod.TargetI.total <- HarmonyMod.FieldHolder..cctor [DeclaredField]",
        "Field HarmonyMod.TargetI.total <- HarmonyMod.FieldUser.Use [StaticFieldRefAccess]",
        "Field HarmonyMod.TargetI.total <- HarmonyMod.FieldUser.Use [TraverseField]",
        "Field HarmonyMod.TargetJ.? <- HarmonyMod.PartlyReadFields.Read [GetFields]",
        "Field HarmonyMod.TargetJ.x <- HarmonyMod.FieldUser.Use [StructFieldRefAccess]",
        // A nested class found by name through AccessTools.Inner or Type.GetNestedType, and a patch
        // class from GetType(), the class the calling member is declared in.
        "Postfix HarmonyMod.TargetK.Part.Move <- HarmonyMod.InstancePatcher.AfterAny [PatchCall in HarmonyMod.InstancePatcher.Apply]",
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
        // A method made generic for one type argument is named by the method it was made from, in a
        // Harmony.Patch call and in a TargetMethod's search.
        "Postfix HarmonyMod.TargetL.Parse <- HarmonyMod.GenericPatchCall.AfterParse [PatchCall in HarmonyMod.GenericPatchCall.Apply]",
        "Postfix HarmonyMod.TargetL.Parse <- HarmonyMod.GenericSearchPatch.Postfix [TargetMethodSearch]",
        // An indexer of a class in another assembly carries C#'s default name, and a setter's value
        // type is unknown.
        "Prefix System.Collections.Generic.List<System.Int32>.get_Item(int) <- HarmonyMod.ExternalIndexerGetterPatch.Prefix [TypedAttribute]",
        "Prefix System.Collections.Generic.List<System.Int32>.set_Item(int, ?) <- HarmonyMod.ExternalIndexerSetterPatch.Prefix [TypedAttribute]",
        // A type from another assembly is qualified from the assembly's reference to it.
        "Field System.Text.StringBuilder.m_ChunkChars <- HarmonyMod.FieldAccessPatch..cctor [AccessToolsField]",
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
            FixtureDependencies.Take(3).Append("... (153 more; raise limit to see all)"),
            text.Split('\n'));
    }

    [Theory]
    [InlineData("HarmonyMod.TargetC.Nested", "public class HarmonyMod.TargetC.Nested")]
    [InlineData("HarmonyMod.TargetD<System.Int32>", "public class HarmonyMod.TargetD<T>")]
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
