using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using EmitOpCode = System.Reflection.Emit.OpCode;
using EmitOpCodes = System.Reflection.Emit.OpCodes;
using FlowControl = System.Reflection.Emit.FlowControl;
using OpCodeType = System.Reflection.Emit.OpCodeType;
using OperandType = System.Reflection.Emit.OperandType;
using StackBehaviour = System.Reflection.Emit.StackBehaviour;

namespace ILens;

/// <summary>
/// Finds the methods that read a field and those that write it, for read_by and assigned_by,
/// whether the assembly defines the field or only references it.
/// </summary>
/// <remarks>
/// A load or a store names the field directly. Taking its address says only that something
/// needed a reference to it: C# takes a value-type field's address to call a method on it, to
/// pass it by reference, or to read or write one of its members. Each such address is followed
/// through the method's stack and locals to the instructions that consume it, and what those do
/// decides whether the method reads or writes the field. A call on a struct method, or a
/// reference passed to a method, that the assembly defines is decided by what that method's
/// body does with the address, followed the same way. What no body in the assembly shows, such
/// as a call into another assembly, counts as both a read and a write, and the answer names
/// what received the address. Not thread-safe: the caller holds the host's lock.
/// </remarks>
public sealed class FieldAccessScanner
{
    public enum AccessKind { Read, Write }

    /// <summary>
    /// A method that reads or writes the field: a method, or the property or event whose
    /// accessor it is. <paramref name="Uncertainty"/> is null when it certainly does.
    /// </summary>
    public sealed record Access(ISymbol Symbol, Uncertainty Uncertainty);

    /// <summary>
    /// What received the field's address in a method not known to read, or to write, the field:
    /// the methods called on it, those it is passed to by reference, whether a reference to it
    /// outlives the method, and whether the address is used in a way not followed at all.
    /// </summary>
    public sealed record Uncertainty(
        IReadOnlyList<string> Calls, IReadOnlyList<string> Passes, bool KeepsReference, bool Untraced)
    {
        /// <summary>
        /// One uncertainty for a member several methods report under, or null when any of them
        /// certainly reads or writes the field.
        /// </summary>
        public static Uncertainty Merge(IEnumerable<Uncertainty> uncertainties)
        {
            var all = uncertainties.ToList();
            if (all.Any(u => u is null))
                return null;
            return new Uncertainty(
                all.SelectMany(u => u.Calls).Distinct().ToList(),
                all.SelectMany(u => u.Passes).Distinct().ToList(),
                all.Any(u => u.KeepsReference),
                all.Any(u => u.Untraced));
        }

        /// <summary>
        /// "calls X.M() on it; passes it by ref to Y.N(ref int); keeps a reference to it".
        /// </summary>
        public override string ToString()
        {
            var parts = new List<string>();
            if (Calls.Count > 0)
                parts.Add($"calls {string.Join(", ", Calls)} on it");
            if (Passes.Count > 0)
                parts.Add($"passes it by ref to {string.Join(", ", Passes)}");
            if (KeepsReference)
                parts.Add("keeps a reference to it");
            if (Untraced)
                parts.Add("takes its address");
            return string.Join("; ", parts);
        }
    }

    private readonly MetadataFile _file;
    private readonly MetadataReader _metadata;
    private readonly MetadataModule _module;
    private readonly Func<EntityHandle, bool> _namesTheField;

    /// <summary>
    /// What a method of the assembly does with the address it receives as one of its arguments.
    /// Null while it is being decided, so that a cycle counts as not known.
    /// </summary>
    private readonly Dictionary<(MethodDefinitionHandle Method, int Argument), (Verdict Read, Verdict Write)?> _argumentUses = new();

    /// <param name="namesTheField">True for a token that names the field in a load or store.</param>
    public FieldAccessScanner(MetadataFile file, MetadataModule module, Func<EntityHandle, bool> namesTheField)
    {
        _file = file;
        _metadata = file.Metadata;
        _module = module;
        _namesTheField = namesTheField;
    }

    /// <summary>Every method that reads, or that writes, the field.</summary>
    public IReadOnlyList<Access> Find(AccessKind kind) => Find(kind, _metadata.MethodDefinitions);

    /// <summary>Those of <paramref name="methods"/> that read, or that write, the field.</summary>
    public IReadOnlyList<Access> Find(AccessKind kind, IEnumerable<MethodDefinitionHandle> methods)
    {
        var accesses = new List<Access>();
        foreach (var handle in methods)
        {
            var (listed, uncertainty) = Examine(handle, kind);
            if (listed)
                accesses.Add(new Access(SymbolOf(_module.GetDefinition(handle)), uncertainty));
        }
        return accesses;
    }

    /// <summary>A method as the analyzers report it: an accessor as its property or event.</summary>
    private static ISymbol SymbolOf(IMethod method) => (ISymbol)method.AccessorOwner ?? method;

    // --- One method ------------------------------------------------------------

    /// <summary>
    /// Whether the method reads, or writes, the field, and when that is not known, what received
    /// its address. A direct load or store settles it; otherwise every address the method takes
    /// is followed to what consumes it.
    /// </summary>
    private (bool Listed, Uncertainty Uncertainty) Examine(MethodDefinitionHandle handle, AccessKind kind)
    {
        var method = _metadata.GetMethodDefinition(handle);
        if (method.RelativeVirtualAddress == 0)
            return (false, null);

        bool direct = false, address = false;
        try
        {
            var reader = _file.GetMethodBody(method.RelativeVirtualAddress).GetILReader();
            while (reader.RemainingBytes > 0 && !direct)
            {
                var code = ReadOpCode(ref reader);
                if (code.OperandType != OperandType.InlineField)
                {
                    SkipOperand(ref reader, code);
                    continue;
                }
                if (!_namesTheField(MetadataTokens.EntityHandle(reader.ReadInt32())))
                    continue;
                var op = (ILOpCode)(ushort)code.Value;
                if (op is ILOpCode.Ldflda or ILOpCode.Ldsflda)
                    address = true;
                else if ((op is ILOpCode.Ldfld or ILOpCode.Ldsfld) == (kind == AccessKind.Read))
                    direct = true;
            }
        }
        catch (BadImageFormatException)
        {
            return (false, null);
        }
        if (direct)
            return (true, null);
        if (!address)
            return (false, null);

        var flow = Trace(handle, IsAddressOfTheField);
        if (flow is null)
            return (true, new Uncertainty([], [], false, Untraced: true));

        var calls = new List<string>();
        var passes = new List<string>();
        bool kept = false, untraced = false;
        // In IL order, so a line names what received the address in the order the code does.
        foreach (var use in flow.Uses.OrderBy(use => use.Consumer).ThenBy(use => use.Operand))
        {
            var effect = Classify(flow, use);
            switch (kind == AccessKind.Read ? effect.Read : effect.Write)
            {
                case Verdict.Yes:
                    return (true, null);
                case Verdict.Unknown:
                    switch (effect.Receiver.Kind)
                    {
                        case ReceiverKind.Calls: calls.Add(effect.Receiver.Name); break;
                        case ReceiverKind.Passes: passes.Add(effect.Receiver.Name); break;
                        case ReceiverKind.Keeps: kept = true; break;
                        default: untraced = true; break;
                    }
                    break;
            }
        }
        return calls.Count + passes.Count > 0 || kept || untraced
            ? (true, new Uncertainty(calls.Distinct().ToList(), passes.Distinct().ToList(), kept, untraced))
            : (false, null);

        bool IsAddressOfTheField(Instruction instruction) =>
            instruction.OpCode is ILOpCode.Ldflda or ILOpCode.Ldsflda
            && _namesTheField(MetadataTokens.EntityHandle(instruction.Token));
    }

    // --- What a use of an address does -----------------------------------------

    /// <summary>Whether code reads, or writes, a value: certainly not, certainly, or not known.</summary>
    private enum Verdict { No, Yes, Unknown }

    private enum ReceiverKind { None, Calls, Passes, Keeps, Untraced }

    /// <summary>What received an address whose use is not known: a method's name, or nothing.</summary>
    private readonly record struct Receiver(ReceiverKind Kind, string Name = null);

    private readonly record struct Effect(Verdict Read, Verdict Write, Receiver Receiver = default);

    private static readonly Effect Reads = new(Verdict.Yes, Verdict.No);
    private static readonly Effect Writes = new(Verdict.No, Verdict.Yes);
    private static readonly Effect Neither = new(Verdict.No, Verdict.No);
    private static readonly Effect Kept = new(Verdict.Unknown, Verdict.Unknown, new Receiver(ReceiverKind.Keeps));
    private static readonly Effect Untraced = new(Verdict.Unknown, Verdict.Unknown, new Receiver(ReceiverKind.Untraced));

    private static Effect Unknown(ReceiverKind kind, string name) =>
        new(Verdict.Unknown, Verdict.Unknown, new Receiver(kind, name));

    /// <summary>
    /// What the instruction that consumes an address does with the value at it, given the
    /// operand the address fills, counted from the deepest.
    /// </summary>
    private Effect Classify(Flow flow, Use use)
    {
        var instruction = flow.Code[use.Consumer];
        var operand = use.Operand;
        switch (instruction.OpCode)
        {
            case ILOpCode.Ldfld or ILOpCode.Ldobj
                or >= ILOpCode.Ldind_i1 and <= ILOpCode.Ldind_ref:
                return Reads;
            case ILOpCode.Stfld or ILOpCode.Stobj
                or >= ILOpCode.Stind_ref and <= ILOpCode.Stind_r8 or ILOpCode.Stind_i:
                // The address is where the value goes, or else it is the value being stored.
                return operand == 0 ? Writes : Kept;
            case ILOpCode.Initobj or ILOpCode.Initblk:
                return operand == 0 ? Writes : Untraced;
            case ILOpCode.Cpobj or ILOpCode.Cpblk:
                return operand == 0 ? Writes : operand == 1 ? Reads : Untraced;
            case ILOpCode.Pop:
                return Neither;
            case ILOpCode.Ret or ILOpCode.Starg or ILOpCode.Starg_s:
                return Kept;
            case ILOpCode.Call or ILOpCode.Callvirt or ILOpCode.Newobj:
                return ClassifyCall(instruction, operand, flow.Context);
            default:
                return Untraced;
        }
    }

    /// <summary>What a call does with an address it receives as <c>this</c> or as an argument.</summary>
    private Effect ClassifyCall(Instruction instruction, int operand, GenericContext context)
    {
        IMethod callee;
        try
        {
            callee = _module.ResolveMethod(MetadataTokens.EntityHandle(instruction.Token), context);
        }
        catch (Exception e) when (e is BadImageFormatException or ArgumentException)
        {
            return Untraced;
        }
        if (callee is null)
            return Untraced;

        var takesThis = instruction.OpCode != ILOpCode.Newobj && !callee.IsStatic;
        if (takesThis && operand == 0)
            return ClassifyReceiver(instruction, callee, context);

        var index = operand - (takesThis ? 1 : 0);
        if (index >= callee.Parameters.Count)
            return Untraced;
        var passing = ParameterPassing.Of(callee.Parameters[index]);
        if (passing.ReadOnly)
            return Reads;
        if (passing.Keyword == ReferenceKind.Out && OwnMethod(callee) is not null)
            return Writes;

        var name = ReferenceFormatter.FormatMethodReference(callee);
        var body = OwnMethod(callee);
        if (body is null || !RunsItsOwnBody(instruction, callee))
            return Unknown(ReceiverKind.Passes, name);
        var (read, write) = ArgumentUse(body.Value, callee.IsStatic ? index : index + 1);
        return new Effect(read, write, new Receiver(ReceiverKind.Passes, name));
    }

    /// <summary>
    /// What a method called on the value at an address does with it: a constructor writes it, a
    /// method of a type that never changes its value reads it, and a method of the assembly's
    /// own struct does what its body does with <c>this</c>.
    /// </summary>
    private Effect ClassifyReceiver(Instruction instruction, IMethod callee, GenericContext context)
    {
        if (callee.IsConstructor)
            return Writes;

        IType type;
        IMethod runs;
        if (instruction.Constrained != 0)
        {
            // A constrained call runs the struct's own implementation, or else a boxed copy.
            try
            {
                type = _module.ResolveType(MetadataTokens.EntityHandle(instruction.Constrained), context);
            }
            catch (Exception e) when (e is BadImageFormatException or ArgumentException)
            {
                return Untraced;
            }
            var name = $"{ReferenceFormatter.FormatTypeName(type)}.{callee.Name}{ReferenceFormatter.FormatTypeArguments(callee)}(" +
                $"{string.Join(", ", callee.Parameters.Select(ReferenceFormatter.FormatParameterType))})";
            if (type.Kind == TypeKind.TypeParameter)
                return Unknown(ReceiverKind.Calls, name);
            if (NeverChanges(type))
                return Reads;
            if (OwnDefinition(type) is not { } definition)
                return Unknown(ReceiverKind.Calls, name);
            if (definition.Kind is TypeKind.Enum or TypeKind.Class or TypeKind.Interface or TypeKind.Delegate)
                return Reads;
            runs = ImplementationIn(definition, callee);
            if (runs is null)
                return Reads;
            return ReceiverEffect(definition, runs, name);
        }

        type = callee.DeclaringType;
        if (NeverChanges(type))
            return Reads;
        var reference = ReferenceFormatter.FormatMethodReference(callee);
        if (OwnDefinition(type) is not { Kind: TypeKind.Struct } own)
            return Unknown(ReceiverKind.Calls, reference);
        return ReceiverEffect(own, callee, reference);
    }

    /// <summary>What a method of the assembly's own struct does with the value it is called on.</summary>
    private Effect ReceiverEffect(ITypeDefinition type, IMethod method, string name)
    {
        if (type.IsReadOnly || method.ThisIsRefReadOnly)
            return Reads;
        if (OwnMethod(method) is not { } body)
            return Unknown(ReceiverKind.Calls, name);
        var (read, write) = ArgumentUse(body, 0);
        return new Effect(read, write, new Receiver(ReceiverKind.Calls, name));
    }

    /// <summary>
    /// True for a type whose methods never change its value: the built-in value types, and the
    /// framework structs below, none of whose methods assigns a field of its own.
    /// </summary>
    private static bool NeverChanges(IType type)
    {
        if (type.Kind is TypeKind.NInt or TypeKind.NUInt)
            return true;
        var generic = type is ParameterizedType constructed ? constructed.GenericType : type;
        return ReferenceFormatter.FullTypeNameOf(generic) is { IsNested: false } name
            && UnchangingTypes.Contains(name.ReflectionName);
    }

    private static readonly HashSet<string> UnchangingTypes = new(StringComparer.Ordinal)
    {
        "System.Boolean", "System.Char", "System.SByte", "System.Byte", "System.Int16", "System.UInt16",
        "System.Int32", "System.UInt32", "System.Int64", "System.UInt64", "System.IntPtr", "System.UIntPtr",
        "System.Single", "System.Double",
        "System.Decimal", "System.Nullable`1", "System.DateTime", "System.DateTimeOffset", "System.TimeSpan",
        "System.Guid", "System.Collections.Generic.KeyValuePair`2",
    };

    /// <summary>The definition of a type the assembly defines, or null for any other type.</summary>
    private ITypeDefinition OwnDefinition(IType type) =>
        type.GetDefinition() is { } definition && definition.ParentModule == _module ? definition : null;

    /// <summary>The handle of a method the assembly defines, or null for one another assembly defines.</summary>
    private MethodDefinitionHandle? OwnMethod(IMethod method) =>
        method.MemberDefinition is IMethod { ParentModule: var module } definition && module == _module
            && definition.MetadataToken.Kind == HandleKind.MethodDefinition && !definition.MetadataToken.IsNil
            ? (MethodDefinitionHandle)definition.MetadataToken
            : null;

    /// <summary>
    /// False for a virtual call an override may answer instead: its body is not the one that
    /// runs, since another assembly, or a class here, can override the method.
    /// </summary>
    private static bool RunsItsOwnBody(Instruction instruction, IMethod callee) =>
        instruction.OpCode != ILOpCode.Callvirt
        || !(callee.IsVirtual || Overriding.Of(callee) || callee.IsAbstract)
        || Overriding.IsSealed(callee)
        || callee.DeclaringTypeDefinition is { IsSealed: true };

    /// <summary>
    /// The method <paramref name="type"/> declares that a constrained call to
    /// <paramref name="method"/> runs: one overriding it, or implementing it, by name and
    /// parameter count, and among several, the one whose parameter types match. Null when the
    /// type declares none, so the call runs on a boxed copy.
    /// </summary>
    private static IMethod ImplementationIn(ITypeDefinition type, IMethod method)
    {
        var candidates = type.DeclaredMethods()
            .Where(candidate => !candidate.IsStatic
                && candidate.Parameters.Count == method.Parameters.Count
                && (candidate.Name == method.Name
                    || candidate.ExplicitlyImplementedInterfaceMembers.Any(member => member.Name == method.Name)))
            .ToList();
        return candidates.FirstOrDefault(candidate => candidate.Parameters
                .Select(parameter => parameter.Type.ReflectionName)
                .SequenceEqual(method.Parameters.Select(parameter => parameter.Type.ReflectionName)))
            ?? candidates.FirstOrDefault();
    }

    /// <summary>
    /// Whether a method of the assembly reads, or writes, the value at the address it receives
    /// as argument <paramref name="argument"/>, where argument 0 of an instance method is
    /// <c>this</c>.
    /// </summary>
    private (Verdict Read, Verdict Write) ArgumentUse(MethodDefinitionHandle method, int argument)
    {
        var key = (method, argument);
        if (_argumentUses.TryGetValue(key, out var known))
            return known ?? (Verdict.Unknown, Verdict.Unknown);
        if (_depth >= MaxDepth)
            return (Verdict.Unknown, Verdict.Unknown);
        _argumentUses[key] = null;

        (Verdict Read, Verdict Write) result = (Verdict.No, Verdict.No);
        _depth++;
        try
        {
            var flow = Trace(method, instruction => IsArgumentLoad(instruction, argument));
            if (flow is null)
            {
                result = (Verdict.Unknown, Verdict.Unknown);
            }
            else
            {
                foreach (var use in flow.Uses)
                {
                    var effect = Classify(flow, use);
                    result = (Join(result.Read, effect.Read), Join(result.Write, effect.Write));
                }
            }
        }
        finally
        {
            _depth--;
        }
        _argumentUses[key] = result;
        return result;
    }

    /// <summary>
    /// How many bodies <see cref="ArgumentUse"/> is reading inside one another. A longer chain of
    /// methods passing the address on than <see cref="MaxDepth"/> counts as not known rather than
    /// followed, since each body read deepens the stack.
    /// </summary>
    private int _depth;

    private const int MaxDepth = 64;

    private static Verdict Join(Verdict a, Verdict b) =>
        a == Verdict.Yes || b == Verdict.Yes ? Verdict.Yes
        : a == Verdict.Unknown || b == Verdict.Unknown ? Verdict.Unknown
        : Verdict.No;

    // --- Following an address --------------------------------------------------

    private sealed class Instruction
    {
        public int Offset;
        public ILOpCode OpCode;
        public EmitOpCode Info;
        public int Token;
        public int Index;
        public int[] Targets;
        public int Constrained;
    }

    /// <summary>One consumption of a tracked address: the instruction and the operand it fills.</summary>
    private readonly record struct Use(int Origin, int Consumer, int Operand);

    private sealed class Flow
    {
        public List<Instruction> Code;
        public HashSet<Use> Uses;

        /// <summary>The type parameters of the method and its type, which its tokens may name.</summary>
        public GenericContext Context;
    }

    /// <summary>The tracked addresses a stack slot or a local may hold, as their instructions' indices.</summary>
    private static readonly ImmutableHashSet<int> None = ImmutableHashSet<int>.Empty;

    private sealed class State
    {
        public ImmutableHashSet<int>[] Stack;
        public ImmutableHashSet<int>[] Locals;
    }

    /// <summary>
    /// Every place the method consumes an address pushed by an instruction
    /// <paramref name="isOrigin"/> picks: an abstract interpretation of the evaluation stack and
    /// the locals over the method's control flow, in which an address moves with <c>dup</c>, a
    /// local and the address of one of its members. Null when the method has no body, or IL the
    /// interpretation cannot follow.
    /// </summary>
    private Flow Trace(MethodDefinitionHandle handle, Func<Instruction, bool> isOrigin)
    {
        var method = _metadata.GetMethodDefinition(handle);
        if (method.RelativeVirtualAddress == 0)
            return null;
        MethodBodyBlock body;
        List<Instruction> code;
        int localCount;
        bool returnsVoid;
        try
        {
            body = _file.GetMethodBody(method.RelativeVirtualAddress);
            code = Decode(body.GetILReader());
            localCount = body.LocalSignature.IsNil ? 0 : LocalCount(body.LocalSignature);
            returnsVoid = Shape(_metadata.GetBlobReader(method.Signature)).ReturnsVoid;
        }
        catch (BadImageFormatException)
        {
            return null;
        }
        if (!code.Any(isOrigin))
            return new Flow { Code = code, Uses = [], Context = ContextOf(handle) };

        var indexOf = new Dictionary<int, int>();
        for (var i = 0; i < code.Count; i++)
            indexOf[code[i].Offset] = i;

        var regions = body.ExceptionRegions;
        var entry = new State[code.Count];
        var work = new Queue<int>();
        var queued = new bool[code.Count];
        var uses = new HashSet<Use>();
        var failed = false;

        void Merge(int offset, IReadOnlyList<ImmutableHashSet<int>> stack, ImmutableHashSet<int>[] locals)
        {
            if (!indexOf.TryGetValue(offset, out var target))
            {
                failed = true;
                return;
            }
            var current = entry[target];
            var changed = false;
            if (current is null)
            {
                entry[target] = new State { Stack = stack.ToArray(), Locals = (ImmutableHashSet<int>[])locals.Clone() };
                changed = true;
            }
            else
            {
                if (current.Stack.Length != stack.Count)
                {
                    failed = true;
                    return;
                }
                for (var k = 0; k < stack.Count; k++)
                    changed |= Widen(ref current.Stack[k], stack[k]);
                for (var k = 0; k < locals.Length; k++)
                    changed |= Widen(ref current.Locals[k], locals[k]);
            }
            if (changed && !queued[target])
            {
                queued[target] = true;
                work.Enqueue(target);
            }
        }

        var noLocals = Enumerable.Repeat(None, localCount).ToArray();
        Merge(0, [], noLocals);
        foreach (var region in regions)
            EnterHandler(region, noLocals);

        while (work.Count > 0 && !failed)
        {
            var i = work.Dequeue();
            queued[i] = false;
            var instruction = code[i];
            var stack = new List<ImmutableHashSet<int>>(entry[i].Stack);
            var state = (ImmutableHashSet<int>[])entry[i].Locals.Clone();

            int pops, pushes;
            try
            {
                (pops, pushes) = StackEffect(instruction, returnsVoid);
            }
            catch (BadImageFormatException)
            {
                failed = true;
                break;
            }
            if (pops > stack.Count)
            {
                failed = true;
                break;
            }
            var popped = stack.GetRange(stack.Count - pops, pops);
            stack.RemoveRange(stack.Count - pops, pops);

            var op = instruction.OpCode;
            var moves = op is ILOpCode.Dup or ILOpCode.Ldflda || IsLocalStore(op);
            if (!moves)
            {
                for (var k = 0; k < pops; k++)
                    foreach (var origin in popped[k])
                        uses.Add(new Use(origin, i, k));
            }

            if (op == ILOpCode.Dup)
            {
                stack.Add(popped[0]);
                stack.Add(popped[0]);
            }
            else if (op == ILOpCode.Ldflda)
                stack.Add(isOrigin(instruction) ? popped[0].Add(i) : popped[0]);
            else if (IsLocalStore(op))
            {
                if (instruction.Index < state.Length)
                    state[instruction.Index] = popped[0];
            }
            else if (IsLocalLoad(op))
                stack.Add(instruction.Index < state.Length ? state[instruction.Index] : None);
            else
            {
                for (var k = 0; k < pushes; k++)
                    stack.Add(k == 0 && isOrigin(instruction) ? None.Add(i) : None);
            }

            if (op is ILOpCode.Leave or ILOpCode.Leave_s)
                stack.Clear();
            var flow = instruction.Info.FlowControl;
            if (flow is FlowControl.Branch or FlowControl.Cond_Branch)
                foreach (var target in instruction.Targets)
                    Merge(target, stack, state);
            var fallsThrough = flow is FlowControl.Next or FlowControl.Call or FlowControl.Meta
                or FlowControl.Break or FlowControl.Cond_Branch && op != ILOpCode.Jmp;
            if (fallsThrough && i + 1 < code.Count)
                Merge(code[i + 1].Offset, stack, state);

            foreach (var region in regions)
            {
                if (instruction.Offset >= region.TryOffset && instruction.Offset < region.TryOffset + region.TryLength)
                    EnterHandler(region, state);
            }
        }

        return failed ? null : new Flow { Code = code, Uses = uses, Context = ContextOf(handle) };

        void EnterHandler(ExceptionRegion region, ImmutableHashSet<int>[] state)
        {
            // A catch or filter block starts with the exception on the stack.
            ImmutableHashSet<int>[] stack = region.Kind is ExceptionRegionKind.Catch or ExceptionRegionKind.Filter ? [None] : [];
            Merge(region.HandlerOffset, stack, state);
            if (region.Kind == ExceptionRegionKind.Filter)
                Merge(region.FilterOffset, [None], state);
        }
    }

    private GenericContext ContextOf(MethodDefinitionHandle handle)
    {
        var method = _module.GetDefinition(handle);
        return new GenericContext(method.DeclaringTypeDefinition?.TypeParameters, method.TypeParameters);
    }

    private static bool Widen(ref ImmutableHashSet<int> set, ImmutableHashSet<int> more)
    {
        var union = set.Union(more);
        if (union.Count == set.Count)
            return false;
        set = union;
        return true;
    }

    private static bool IsLocalStore(ILOpCode op) =>
        op is ILOpCode.Stloc_0 or ILOpCode.Stloc_1 or ILOpCode.Stloc_2 or ILOpCode.Stloc_3 or ILOpCode.Stloc_s or ILOpCode.Stloc;

    private static bool IsLocalLoad(ILOpCode op) =>
        op is ILOpCode.Ldloc_0 or ILOpCode.Ldloc_1 or ILOpCode.Ldloc_2 or ILOpCode.Ldloc_3 or ILOpCode.Ldloc_s or ILOpCode.Ldloc;

    private static bool IsArgumentLoad(Instruction instruction, int argument) =>
        instruction.OpCode is ILOpCode.Ldarg_0 or ILOpCode.Ldarg_1 or ILOpCode.Ldarg_2 or ILOpCode.Ldarg_3
            or ILOpCode.Ldarg_s or ILOpCode.Ldarg
        && instruction.Index == argument;

    /// <summary>How many values the instruction pops and how many it pushes.</summary>
    private (int Pops, int Pushes) StackEffect(Instruction instruction, bool returnsVoid)
    {
        var info = instruction.Info;
        if (info.StackBehaviourPop != StackBehaviour.Varpop && info.StackBehaviourPush != StackBehaviour.Varpush)
            return (Count(info.StackBehaviourPop), Count(info.StackBehaviourPush));
        if (instruction.OpCode == ILOpCode.Ret)
            return (returnsVoid ? 0 : 1, 0);

        var shape = Shape(_metadata.GetBlobReader(SignatureOf(MetadataTokens.EntityHandle(instruction.Token))));
        var pops = shape.Parameters + (shape.HasThis ? 1 : 0);
        return instruction.OpCode switch
        {
            ILOpCode.Newobj => (shape.Parameters, 1),
            ILOpCode.Calli => (pops + 1, shape.ReturnsVoid ? 0 : 1),
            _ => (pops, shape.ReturnsVoid ? 0 : 1),
        };
    }

    private BlobHandle SignatureOf(EntityHandle token) => token.Kind switch
    {
        HandleKind.MethodDefinition => _metadata.GetMethodDefinition((MethodDefinitionHandle)token).Signature,
        HandleKind.MemberReference => _metadata.GetMemberReference((MemberReferenceHandle)token).Signature,
        HandleKind.MethodSpecification => SignatureOf(_metadata.GetMethodSpecification((MethodSpecificationHandle)token).Method),
        HandleKind.StandaloneSignature => _metadata.GetStandaloneSignature((StandaloneSignatureHandle)token).Signature,
        _ => throw new BadImageFormatException($"A call cannot name a {token.Kind}."),
    };

    /// <summary>
    /// What a method signature says about the stack: whether a call passes <c>this</c>, how
    /// many parameters it takes, and whether it returns nothing.
    /// </summary>
    private static (bool HasThis, int Parameters, bool ReturnsVoid) Shape(BlobReader blob)
    {
        var header = blob.ReadSignatureHeader();
        if (header.IsGeneric)
            blob.ReadCompressedInteger();
        var parameters = blob.ReadCompressedInteger();
        var returned = blob.ReadSignatureTypeCode();
        while (returned is SignatureTypeCode.RequiredModifier or SignatureTypeCode.OptionalModifier)
        {
            blob.ReadTypeHandle();
            returned = blob.ReadSignatureTypeCode();
        }
        return (header.IsInstance && !header.HasExplicitThis, parameters, returned == SignatureTypeCode.Void);
    }

    private int LocalCount(StandaloneSignatureHandle handle)
    {
        var blob = _metadata.GetBlobReader(_metadata.GetStandaloneSignature(handle).Signature);
        blob.ReadSignatureHeader();
        return blob.ReadCompressedInteger();
    }

    private static int Count(StackBehaviour behavior) => behavior switch
    {
        StackBehaviour.Pop0 or StackBehaviour.Push0 => 0,
        StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref
            or StackBehaviour.Push1 or StackBehaviour.Pushi or StackBehaviour.Pushi8
            or StackBehaviour.Pushr4 or StackBehaviour.Pushr8 or StackBehaviour.Pushref => 1,
        StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_pop1 or StackBehaviour.Popi_popi
            or StackBehaviour.Popi_popi8 or StackBehaviour.Popi_popr4 or StackBehaviour.Popi_popr8
            or StackBehaviour.Popref_pop1 or StackBehaviour.Popref_popi or StackBehaviour.Push1_push1 => 2,
        StackBehaviour.Popi_popi_popi or StackBehaviour.Popref_popi_popi or StackBehaviour.Popref_popi_popi8
            or StackBehaviour.Popref_popi_popr4 or StackBehaviour.Popref_popi_popr8
            or StackBehaviour.Popref_popi_popref or StackBehaviour.Popref_popi_pop1 => 3,
        _ => throw new BadImageFormatException($"Unexpected stack behavior {behavior}."),
    };

    // --- Decoding ----------------------------------------------------------------

    private static readonly EmitOpCode[] OneByteOpCodes = new EmitOpCode[0x100];
    private static readonly EmitOpCode[] TwoByteOpCodes = new EmitOpCode[0x100];

    static FieldAccessScanner()
    {
        foreach (var field in typeof(EmitOpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var code = (EmitOpCode)field.GetValue(null)!;
            var value = (ushort)code.Value;
            if (code.Size == 1)
                OneByteOpCodes[value] = code;
            else
                TwoByteOpCodes[value & 0xFF] = code;
        }
    }

    private static EmitOpCode ReadOpCode(ref BlobReader reader)
    {
        var first = reader.ReadByte();
        var code = first == 0xFE ? TwoByteOpCodes[reader.ReadByte()] : OneByteOpCodes[first];
        return code.Size == 0 ? throw new BadImageFormatException("Unknown opcode.") : code;
    }

    private static void SkipOperand(ref BlobReader reader, EmitOpCode code)
    {
        switch (code.OperandType)
        {
            case OperandType.InlineNone:
                break;
            case OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar:
                reader.ReadByte();
                break;
            case OperandType.InlineVar:
                reader.ReadUInt16();
                break;
            case OperandType.InlineI8 or OperandType.InlineR:
                reader.ReadInt64();
                break;
            case OperandType.InlineSwitch:
                var count = reader.ReadInt32();
                for (var k = 0; k < count; k++)
                    reader.ReadInt32();
                break;
            default:
                reader.ReadInt32();
                break;
        }
    }

    private static List<Instruction> Decode(BlobReader reader)
    {
        var code = new List<Instruction>();
        var constrained = 0;
        while (reader.RemainingBytes > 0)
        {
            var instruction = new Instruction { Offset = reader.Offset };
            var info = ReadOpCode(ref reader);
            instruction.Info = info;
            instruction.OpCode = (ILOpCode)(ushort)info.Value;
            switch (info.OperandType)
            {
                case OperandType.InlineNone:
                    instruction.Index = MacroIndex(instruction.OpCode);
                    break;
                case OperandType.ShortInlineBrTarget:
                {
                    int delta = reader.ReadSByte();
                    instruction.Targets = [reader.Offset + delta];
                    break;
                }
                case OperandType.InlineBrTarget:
                {
                    var delta = reader.ReadInt32();
                    instruction.Targets = [reader.Offset + delta];
                    break;
                }
                case OperandType.InlineSwitch:
                {
                    var deltas = new int[reader.ReadInt32()];
                    for (var k = 0; k < deltas.Length; k++)
                        deltas[k] = reader.ReadInt32();
                    var next = reader.Offset;
                    instruction.Targets = deltas.Select(delta => next + delta).ToArray();
                    break;
                }
                case OperandType.ShortInlineVar:
                    instruction.Index = reader.ReadByte();
                    break;
                case OperandType.InlineVar:
                    instruction.Index = reader.ReadUInt16();
                    break;
                case OperandType.ShortInlineI:
                    reader.ReadByte();
                    break;
                case OperandType.InlineI8 or OperandType.InlineR:
                    reader.ReadInt64();
                    break;
                case OperandType.InlineI or OperandType.ShortInlineR:
                    reader.ReadInt32();
                    break;
                default:
                    instruction.Token = reader.ReadInt32();
                    break;
            }
            if (instruction.OpCode == ILOpCode.Constrained)
                constrained = instruction.Token;
            else if (info.OpCodeType != OpCodeType.Prefix)
            {
                instruction.Constrained = constrained;
                constrained = 0;
            }
            code.Add(instruction);
        }
        return code;
    }

    /// <summary>The local or argument a short form such as <c>ldloc.2</c> names in its opcode.</summary>
    private static int MacroIndex(ILOpCode op) => op switch
    {
        >= ILOpCode.Ldarg_0 and <= ILOpCode.Ldarg_3 => op - ILOpCode.Ldarg_0,
        >= ILOpCode.Ldloc_0 and <= ILOpCode.Ldloc_3 => op - ILOpCode.Ldloc_0,
        >= ILOpCode.Stloc_0 and <= ILOpCode.Stloc_3 => op - ILOpCode.Stloc_0,
        _ => 0,
    };
}
