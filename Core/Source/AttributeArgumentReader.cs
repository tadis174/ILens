using System.Reflection.Metadata;
using ICSharpCode.Decompiler.TypeSystem;

namespace ILens;

/// <summary>
/// Reads whether a custom attribute's arguments name one type: a <c>typeof</c> of it or of a
/// type built from it, or a value of it when it is an enum. The caller says what the type is:
/// by its reference rows and name for a type from another assembly, by its definition for one
/// the assembly defines. It also reads which fields and properties an attribute sets by name
/// (<see cref="NamedArgumentsOf"/>).
/// </summary>
/// <remarks>
/// An attribute's bytes (ECMA-335 II.23.3) hold an enum's value without saying how many bytes
/// it takes. That is the enum's underlying type, which only the assembly that defines the enum
/// records, and a value read with the wrong width shifts everything after it. For an enum the
/// inspected assembly defines, the type system has the width. For any other the reader tries
/// one, two, four and eight bytes and keeps the readings that end exactly where the bytes end,
/// as the right one does, so the answer does not depend on whether the enum's assembly can be
/// loaded. The decoder of System.Reflection.Metadata cannot serve here: it wants the width
/// before it reads, and does not say whether bytes were left over. An attribute without such
/// an enum has one reading, which counts with bytes left over as well, as that decoder
/// counts it.
///
/// More than one reading can fit: a value read too wide swallows the length of a text or type
/// name that follows it, and a character of that text, read as its length, can happen to equal
/// what is left of it. An attribute whose readings then disagree on whether it names the type
/// is not decided and is kept in <see cref="Undecided"/>. On 1,064,580 attributes of 1,044
/// assemblies, 79,718 of them with an enum's value, every one had exactly one reading that
/// fits (measured 2026-10-10).
/// </remarks>
public sealed class AttributeArgumentReader
{
    /// <summary>An attribute whose bytes did not decide whether it names the type.</summary>
    /// <param name="Enum">The enum whose width the readings differ in, as an answer prints a type.</param>
    public readonly record struct UndecidedAttribute(
        EntityHandle Parent, EntityHandle Constructor, BlobHandle Value, string Enum);

    /// <summary>A field or a property an attribute sets by name.</summary>
    public readonly record struct NamedArgument(bool IsField, string Name);

    /// <summary>
    /// The fields and properties an attribute sets by name: those every reading that fits
    /// has, and those only some of them have, with the enum whose width those readings differ
    /// in, as an answer prints a type.
    /// </summary>
    public sealed record NamedArguments(
        IReadOnlyList<NamedArgument> Set, IReadOnlyList<NamedArgument> MaySet, string Enum)
    {
        public static readonly NamedArguments None = new([], [], null);
    }

    private enum Kind { Primitive, Text, SystemType, Boxed, Enum, Array }

    /// <summary>
    /// An argument's type, as far as reading its value takes: the bytes of a primitive, the
    /// element of an array, and for an enum its bytes when they are known, or else what
    /// identifies it among the widths a reading assumes, its reference row or its name.
    /// </summary>
    private sealed record ArgumentType(Kind Kind, int Size = 0, object Enum = null, ArgumentType Element = null);

    /// <summary>A constructor's parameters, and whether one of them is of the type.</summary>
    private sealed record Parameters(ArgumentType[] Types, bool OneIsOfTheType);

    /// <summary>What a type name written into an attribute's bytes resolves to.</summary>
    private readonly record struct NamedType(bool NamesTheType, int EnumSize, IType Type);

    /// <summary>
    /// How a reading ended: at the end of the bytes, before it, in bytes that are no attribute
    /// read this way, or at an enum it has no width for.
    /// </summary>
    private enum Outcome { Fits, BytesLeft, Misfit, NeedsWidth }

    private static readonly ArgumentType Text = new(Kind.Text);
    private static readonly ArgumentType SystemType = new(Kind.SystemType);
    private static readonly ArgumentType Boxed = new(Kind.Boxed);
    private static readonly ArgumentType OneByte = new(Kind.Primitive, 1);
    private static readonly ArgumentType TwoBytes = new(Kind.Primitive, 2);
    private static readonly ArgumentType FourBytes = new(Kind.Primitive, 4);
    private static readonly ArgumentType EightBytes = new(Kind.Primitive, 8);

    private static readonly int[] Widths = [1, 2, 4, 8];

    // Four enums of unknown width in one attribute make 256 readings; a search this long has
    // left what a compiler writes.
    private const int MostReadings = 4096;

    private readonly MetadataModule _module;
    private readonly MetadataReader _metadata;
    private readonly Func<EntityHandle, bool> _isTheType;
    private readonly Func<IType, bool> _names;
    private readonly SimpleTypeResolveContext _context;

    // An assembly's attributes share few constructors and few type names.
    private readonly Dictionary<EntityHandle, Parameters> _parameters = new();
    private readonly Dictionary<string, NamedType> _named = new();

    // What the reading under way has met.
    private readonly List<string> _typeofs = new();
    private readonly List<NamedArgument> _set = new();
    private bool _namedAsEnum;
    private object _unsized;

    // Whether a reading decodes the names of the members set by name, which only
    // NamedArgumentsOf asks for.
    private bool _readsNames;

    /// <summary>For reading which members attributes set by name, where no type is asked about.</summary>
    public AttributeArgumentReader(MetadataModule module) : this(module, _ => false, _ => false)
    {
    }

    /// <param name="module">The module the attributes are read from.</param>
    /// <param name="isTheType">True for a definition or reference row that is the type.</param>
    /// <param name="names">
    /// True for a type that is the type or built from it. Asked about each type name written
    /// into an attribute's bytes: a <c>typeof</c> argument, or the type of an argument passed
    /// as an object.
    /// </param>
    public AttributeArgumentReader(MetadataModule module, Func<EntityHandle, bool> isTheType, Func<IType, bool> names)
    {
        _module = module;
        _metadata = module.MetadataFile.Metadata;
        _isTheType = isTheType;
        _names = names;
        _context = new SimpleTypeResolveContext(module);
    }

    /// <summary>The attributes <see cref="NameTheType"/> answered false for without knowing.</summary>
    public List<UndecidedAttribute> Undecided { get; } = new();

    /// <summary>
    /// True if one of the attribute's arguments names the type. False also for an attribute
    /// whose readings disagree, which is then added to <see cref="Undecided"/>, and for one no
    /// reading fits.
    /// </summary>
    public bool NameTheType(CustomAttribute attribute)
    {
        var parameters = ParametersOf(attribute.Constructor);
        // The constructor's signature says so, whatever the bytes hold.
        if (parameters.OneIsOfTheType)
            return true;

        // Most attributes hold no enum of unknown width, and one reading settles them. Bytes
        // after what it reads do not unsettle it, with no width to tell by them.
        var outcome = Read(attribute.Value, parameters.Types, []);
        if (outcome != Outcome.NeedsWidth)
            return outcome is Outcome.Fits or Outcome.BytesLeft && ReadingNamesTheType();

        (object Enum, int Width)[] naming = null, other = null;
        var searched = EachReadingThatFits(attribute.Value, parameters.Types, widths =>
        {
            if (ReadingNamesTheType())
                naming ??= widths;
            else
                other ??= widths;
        });
        if (!searched)
        {
            Undecided.Add(new(attribute.Parent, attribute.Constructor, attribute.Value, DisplayOf(_unsized)));
            return false;
        }

        if (naming is null || other is null)
            return naming is not null;
        Undecided.Add(new(attribute.Parent, attribute.Constructor, attribute.Value,
            DisplayOf(PartingEnum(naming, other))));
        return false;
    }

    /// <summary>
    /// The fields and properties the attribute sets by name, as in <c>[Label(Text = "x")]</c>.
    /// Where more than one reading fits and they differ in what they set, a member only some
    /// of them set comes back as one the attribute may set. None for an attribute no reading
    /// fits.
    /// </summary>
    public NamedArguments NamedArgumentsOf(CustomAttribute attribute)
    {
        var parameters = ParametersOf(attribute.Constructor).Types;
        _readsNames = true;
        try
        {
            var outcome = Read(attribute.Value, parameters, []);
            if (outcome != Outcome.NeedsWidth)
                return outcome is Outcome.Fits or Outcome.BytesLeft && _set.Count > 0
                    ? new([.. _set], [], null)
                    : NamedArguments.None;

            var readings = new List<((object Enum, int Width)[] Widths, NamedArgument[] Set)>();
            if (!EachReadingThatFits(attribute.Value, parameters, widths => readings.Add((widths, [.. _set]))))
                return new([], readings.SelectMany(reading => reading.Set).Distinct().ToList(), DisplayOf(_unsized));
            if (readings.Count == 0)
                return NamedArguments.None;

            var first = readings[0];
            var inAll = first.Set.Where(member => readings.All(reading => reading.Set.Contains(member))).Distinct().ToList();
            var inSome = readings.SelectMany(reading => reading.Set).Distinct().Where(member => !inAll.Contains(member)).ToList();
            if (inSome.Count == 0)
                return inAll.Count == 0 ? NamedArguments.None : new(inAll, [], null);
            var differing = readings.First(reading => inSome.Any(member => reading.Set.Contains(member) != first.Set.Contains(member)));
            return new(inAll, inSome, DisplayOf(PartingEnum(first.Widths, differing.Widths)));
        }
        finally
        {
            _readsNames = false;
        }
    }

    /// <summary>
    /// Reads the attribute with every combination of widths for its enums of unknown width,
    /// starting from the one a reading without widths stopped at, and calls
    /// <paramref name="fits"/> after each reading that ends where the bytes end, with the
    /// widths it assumed. False when the search was given up as too long.
    /// </summary>
    private bool EachReadingThatFits(BlobHandle value, ArgumentType[] parameters, Action<(object Enum, int Width)[]> fits)
    {
        var open = new Stack<(object Enum, int Width)[]>();
        foreach (var width in Widths)
            open.Push([(_unsized, width)]);
        for (var readings = 1; open.Count > 0; readings++)
        {
            var widths = open.Pop();
            switch (Read(value, parameters, widths))
            {
                case Outcome.Fits:
                    fits(widths);
                    break;
                case Outcome.NeedsWidth when readings >= MostReadings:
                    return false;
                case Outcome.NeedsWidth:
                    foreach (var width in Widths)
                        open.Push([.. widths, (_unsized, width)]);
                    break;
            }
        }
        return true;
    }

    /// <summary>
    /// The enum two readings part at: they assume the same widths up to it.
    /// </summary>
    private static object PartingEnum((object Enum, int Width)[] one, (object Enum, int Width)[] other)
    {
        for (var i = 0; i < Math.Min(one.Length, other.Length); i++)
        {
            if (one[i].Width != other[i].Width)
                return one[i].Enum;
        }
        return one[0].Enum;
    }

    /// <summary>True if the reading <see cref="Read"/> just made, which fits, names the type.</summary>
    private bool ReadingNamesTheType()
    {
        if (_namedAsEnum)
            return true;
        foreach (var name in _typeofs)
        {
            if (Named(name).NamesTheType)
                return true;
        }
        return false;
    }

    // --- The bytes ---------------------------------------------------------------

    /// <summary>
    /// Reads an attribute's bytes with <paramref name="widths"/> for the enums of unknown width,
    /// and leaves in <see cref="_typeofs"/> and <see cref="_namedAsEnum"/> what a reading that
    /// fits names, and in <see cref="_set"/> the members it sets by name when
    /// <see cref="_readsNames"/>. A reading that meets an enum without a width stops and leaves
    /// it in <see cref="_unsized"/>.
    /// </summary>
    private Outcome Read(BlobHandle value, ArgumentType[] parameters, (object Enum, int Width)[] widths)
    {
        _typeofs.Clear();
        _set.Clear();
        _namedAsEnum = false;
        var reader = _metadata.GetBlobReader(value);
        if (reader.RemainingBytes < 2 || reader.ReadUInt16() != 1)
            return Outcome.Misfit;

        foreach (var parameter in parameters)
        {
            var outcome = ReadValue(ref reader, parameter, widths);
            if (outcome != Outcome.Fits)
                return outcome;
        }

        if (reader.RemainingBytes < 2)
            return Outcome.Misfit;
        var named = reader.ReadUInt16();
        for (var i = 0; i < named; i++)
        {
            // A field or a property, then its type, its name and its value.
            if (reader.RemainingBytes < 1)
                return Outcome.Misfit;
            var member = reader.ReadByte();
            if (member is not (0x53 or 0x54))
                return Outcome.Misfit;
            var type = ReadStatedType(ref reader, boxed: false, element: false);
            if (type is null || !TryReadText(ref reader, out var memberName, decode: _readsNames))
                return Outcome.Misfit;
            if (memberName is not null)
                _set.Add(new(member == 0x53, memberName));
            var outcome = ReadValue(ref reader, type, widths);
            if (outcome != Outcome.Fits)
                return outcome;
        }
        return reader.RemainingBytes == 0 ? Outcome.Fits : Outcome.BytesLeft;
    }

    private Outcome ReadValue(ref BlobReader reader, ArgumentType type, (object Enum, int Width)[] widths)
    {
        switch (type.Kind)
        {
            case Kind.Primitive:
                return Skip(ref reader, type.Size);
            case Kind.Text:
                return TryReadText(ref reader, out _, decode: false) ? Outcome.Fits : Outcome.Misfit;
            case Kind.SystemType:
                if (!TryReadText(ref reader, out var name, decode: true))
                    return Outcome.Misfit;
                if (name is not null)
                    _typeofs.Add(name);
                return Outcome.Fits;
            case Kind.Boxed:
                var stated = ReadStatedType(ref reader, boxed: true, element: false);
                return stated is null ? Outcome.Misfit : ReadValue(ref reader, stated, widths);
            case Kind.Enum:
                var size = type.Size;
                if (size == 0)
                {
                    foreach (var assumed in widths)
                    {
                        if (assumed.Enum.Equals(type.Enum))
                            size = assumed.Width;
                    }
                    if (size == 0)
                    {
                        _unsized = type.Enum;
                        return Outcome.NeedsWidth;
                    }
                }
                return Skip(ref reader, size);
            default:
                if (reader.RemainingBytes < 4)
                    return Outcome.Misfit;
                var count = reader.ReadUInt32();
                // A null array.
                if (count == uint.MaxValue)
                    return Outcome.Fits;
                // Every element takes a byte or more.
                if (count > reader.RemainingBytes)
                    return Outcome.Misfit;
                for (var i = 0u; i < count; i++)
                {
                    var outcome = ReadValue(ref reader, type.Element, widths);
                    if (outcome != Outcome.Fits)
                        return outcome;
                }
                return Outcome.Fits;
        }
    }

    /// <summary>
    /// A type the bytes state themselves: of a named argument, of a value passed as an object,
    /// and of the elements of an array of either. Null for bytes that state none.
    /// </summary>
    private ArgumentType ReadStatedType(ref BlobReader reader, bool boxed, bool element)
    {
        if (reader.RemainingBytes < 1)
            return null;
        var code = reader.ReadByte();
        switch (code)
        {
            case 0x0E:
                return Text;
            case 0x50:
                return SystemType;
            // A value passed as an object states the type it has, which is not object again.
            case 0x51 when !boxed || element:
                return Boxed;
            case 0x1D when !element:
                var elementType = ReadStatedType(ref reader, boxed, element: true);
                return elementType is null ? null : new(Kind.Array, Element: elementType);
            case 0x55:
                if (!TryReadText(ref reader, out var name, decode: true) || name is null)
                    return null;
                var named = Named(name);
                _namedAsEnum |= named.NamesTheType;
                return new(Kind.Enum, named.EnumSize, name);
            default:
                return PrimitiveOf(code);
        }
    }

    /// <summary>
    /// A text as the bytes hold it, a length and that many bytes of UTF-8, or the one byte FF
    /// for null. False when the bytes hold neither.
    /// </summary>
    private static bool TryReadText(ref BlobReader reader, out string text, bool decode)
    {
        text = null;
        if (reader.RemainingBytes < 1)
            return false;
        if (reader.ReadByte() == 0xFF)
            return true;
        reader.Offset--;
        if (!reader.TryReadCompressedInteger(out var length) || length > reader.RemainingBytes)
            return false;
        if (decode)
            text = reader.ReadUTF8(length);
        else
            reader.Offset += length;
        return true;
    }

    private static Outcome Skip(ref BlobReader reader, int bytes)
    {
        if (reader.RemainingBytes < bytes)
            return Outcome.Misfit;
        reader.Offset += bytes;
        return Outcome.Fits;
    }

    /// <summary>The primitive type an element type code stands for, or null for any other code.</summary>
    private static ArgumentType PrimitiveOf(int code) => code switch
    {
        0x02 or 0x04 or 0x05 => OneByte,
        0x03 or 0x06 or 0x07 => TwoBytes,
        0x08 or 0x09 or 0x0C => FourBytes,
        0x0A or 0x0B or 0x0D => EightBytes,
        _ => null,
    };

    // --- The constructor's signature ---------------------------------------------

    private Parameters ParametersOf(EntityHandle constructor)
    {
        if (_parameters.TryGetValue(constructor, out var known))
            return known;

        BlobReader signature;
        BlobReader typeArguments = default;
        switch (constructor.Kind)
        {
            case HandleKind.MethodDefinition:
                signature = _metadata.GetBlobReader(
                    _metadata.GetMethodDefinition((MethodDefinitionHandle)constructor).Signature);
                break;
            case HandleKind.MemberReference:
                var reference = _metadata.GetMemberReference((MemberReferenceHandle)constructor);
                signature = _metadata.GetBlobReader(reference.Signature);
                if (reference.Parent.Kind == HandleKind.TypeSpecification)
                    typeArguments = TypeArgumentsOf((TypeSpecificationHandle)reference.Parent);
                break;
            default:
                throw new BadImageFormatException();
        }

        var header = signature.ReadSignatureHeader();
        if (header.Kind != SignatureKind.Method || header.IsGeneric)
            throw new BadImageFormatException();
        var count = signature.ReadCompressedInteger();
        if (signature.ReadSignatureTypeCode() != SignatureTypeCode.Void)
            throw new BadImageFormatException();

        var types = new ArgumentType[count];
        var nameTheType = false;
        for (var i = 0; i < count; i++)
            types[i] = ReadParameterType(ref signature, typeArguments, element: false, ref nameTheType);
        return _parameters[constructor] = new(types, nameTheType);
    }

    /// <summary>
    /// The type arguments of a constructed attribute class, from their count on, which a
    /// constructor's parameter of the class's type parameter is read from. Empty for a
    /// specification that constructs no type.
    /// </summary>
    private BlobReader TypeArgumentsOf(TypeSpecificationHandle specification)
    {
        var arguments = _metadata.GetBlobReader(_metadata.GetTypeSpecification(specification).Signature);
        if (arguments.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance)
            return default;
        // A class or a value type, then the generic type.
        arguments.ReadCompressedInteger();
        arguments.ReadTypeHandle();
        return arguments;
    }

    private ArgumentType ReadParameterType(
        ref BlobReader signature, BlobReader typeArguments, bool element, ref bool nameTheType)
    {
        var code = signature.ReadSignatureTypeCode();
        switch (code)
        {
            case SignatureTypeCode.String:
                return Text;
            case SignatureTypeCode.Object:
                return Boxed;
            case SignatureTypeCode.SZArray when !element:
                return new(Kind.Array,
                    Element: ReadParameterType(ref signature, typeArguments, element: true, ref nameTheType));
            // System.Type or an enum: no other class or value type is an attribute's parameter.
            case SignatureTypeCode.TypeHandle:
                var handle = signature.ReadTypeHandle();
                nameTheType |= _isTheType(handle);
                return IsSystemType(handle) ? SystemType : EnumOf(handle);
            case SignatureTypeCode.GenericTypeParameter:
                var index = signature.ReadCompressedInteger();
                if (typeArguments.Length == 0 || index >= typeArguments.ReadCompressedInteger())
                    throw new BadImageFormatException();
                for (; index > 0; index--)
                    SkipType(ref typeArguments);
                return ReadParameterType(ref typeArguments, default, element, ref nameTheType);
            default:
                return PrimitiveOf((int)code) ?? throw new BadImageFormatException();
        }
    }

    private ArgumentType EnumOf(EntityHandle handle) => handle.Kind switch
    {
        HandleKind.TypeDefinition =>
            new(Kind.Enum, SizeOf(_module.GetDefinition((TypeDefinitionHandle)handle)), handle),
        HandleKind.TypeReference => new(Kind.Enum, 0, handle),
        _ => throw new BadImageFormatException(),
    };

    /// <summary>True for System.Type, which a core library defines itself.</summary>
    private bool IsSystemType(EntityHandle handle)
    {
        StringHandle @namespace, name;
        switch (handle.Kind)
        {
            case HandleKind.TypeDefinition:
                var definition = _metadata.GetTypeDefinition((TypeDefinitionHandle)handle);
                (@namespace, name) = (definition.Namespace, definition.Name);
                break;
            case HandleKind.TypeReference:
                var reference = _metadata.GetTypeReference((TypeReferenceHandle)handle);
                (@namespace, name) = (reference.Namespace, reference.Name);
                break;
            default:
                return false;
        }
        return _metadata.StringComparer.Equals(@namespace, "System") && _metadata.StringComparer.Equals(name, "Type");
    }

    /// <summary>Moves past one type of a signature (ECMA-335 II.23.2.12).</summary>
    private static void SkipType(ref BlobReader signature)
    {
        switch (signature.ReadCompressedInteger())
        {
            // A pointer, a reference, a one-dimensional array.
            case 0x0F or 0x10 or 0x1D:
                SkipType(ref signature);
                break;
            // A class or a value type.
            case 0x11 or 0x12:
                signature.ReadTypeHandle();
                break;
            // A type parameter of a type or of a method.
            case 0x13 or 0x1E:
                signature.ReadCompressedInteger();
                break;
            // An array with a shape: its rank, its sizes and its lower bounds.
            case 0x14:
                SkipType(ref signature);
                signature.ReadCompressedInteger();
                for (var sizes = signature.ReadCompressedInteger(); sizes > 0; sizes--)
                    signature.ReadCompressedInteger();
                for (var bounds = signature.ReadCompressedInteger(); bounds > 0; bounds--)
                    signature.ReadCompressedSignedInteger();
                break;
            // A constructed type: class or value type, the generic type, its arguments.
            case 0x15:
                signature.ReadCompressedInteger();
                signature.ReadTypeHandle();
                for (var arguments = signature.ReadCompressedInteger(); arguments > 0; arguments--)
                    SkipType(ref signature);
                break;
            // A function pointer, whose signature this does not read.
            case 0x1B:
                throw new BadImageFormatException();
            // A modifier before the type.
            case 0x1F or 0x20:
                signature.ReadTypeHandle();
                SkipType(ref signature);
                break;
        }
    }

    // --- Types ------------------------------------------------------------------

    private NamedType Named(string name)
    {
        if (_named.TryGetValue(name, out var known))
            return known;
        NamedType named = default;
        try
        {
            var type = ReflectionHelper.ParseReflectionName(name).Resolve(_context);
            named = new(_names(type), SizeOf(type.GetDefinition()), type);
        }
        catch (ReflectionNameParseException)
        {
        }
        return _named[name] = named;
    }

    /// <summary>
    /// The bytes a value of an enum the type system holds takes, or 0 for any other type and
    /// for one it does not hold.
    /// </summary>
    private static int SizeOf(ITypeDefinition type) => type?.EnumUnderlyingType?.GetDefinition()?.KnownTypeCode switch
    {
        KnownTypeCode.Boolean or KnownTypeCode.SByte or KnownTypeCode.Byte => 1,
        KnownTypeCode.Char or KnownTypeCode.Int16 or KnownTypeCode.UInt16 => 2,
        KnownTypeCode.Int32 or KnownTypeCode.UInt32 => 4,
        KnownTypeCode.Int64 or KnownTypeCode.UInt64 => 8,
        _ => 0,
    };

    private string DisplayOf(object @enum) => @enum switch
    {
        EntityHandle handle => ReferenceFormatter.FormatTypeName(_module.ResolveType(handle, default)),
        string name when Named(name).Type is { } type => ReferenceFormatter.FormatTypeName(type),
        _ => @enum.ToString(),
    };
}
