using ICSharpCode.Decompiler.TypeSystem;

namespace ILens;

/// <summary>
/// The rule of the name filters, <c>name_contains</c> and <c>type_name_contains</c>: a
/// case-insensitive part of a name as ILens writes it. A text matches a name that holds it as it
/// stands, which finds a name that bears brackets itself, as C++/CLI names the class of a
/// template instance <c>NativePointerWrapper&lt;_GUID&gt;</c>, and a compiler-generated one such
/// as <c>&lt;Main&gt;b__0_0</c>. A text that ends in angle brackets after a name, as ILens writes
/// a generic type or method, <c>List&lt;&gt;</c>, <c>Get&lt;T&gt;</c> or <c>Get&lt;int&gt;</c>,
/// also matches a name that ends in the text before them where the type or method declares as
/// many type parameters of its own as they count, read as <c>member_name</c> reads them
/// (<see cref="SymbolResolver.ReadTypeArgumentCount"/>). The names or types in the brackets are
/// not compared, since a caller copies type arguments from one answer and the names of type
/// parameters from another. The text before the brackets matches the end of the name, so
/// <c>Fire&lt;int&gt;</c> leaves out <c>FireAgain&lt;U&gt;</c>, whose name as ILens writes it does
/// not hold <c>Fire&lt;</c>.
/// </summary>
public sealed class NameFilter
{
    /// <summary>The text, or null where none was given, which filters nothing.</summary>
    private readonly string _text;

    /// <summary>The text before the brackets that end it, or null where none end it.</summary>
    private readonly string _bare;

    /// <summary>How many type arguments the brackets that end the text count.</summary>
    private readonly int _count;

    private NameFilter(string text)
    {
        _text = string.IsNullOrEmpty(text) ? null : text;
        if (_text != null && SymbolResolver.ReadTypeArgumentCount(_text) is (var bare, { } count))
            (_bare, _count) = (bare, count);
    }

    /// <summary>
    /// The filter a parameter's text gives; a null or empty text, which a caller passes to leave
    /// the filter out, takes every name.
    /// </summary>
    public static NameFilter Of(string text) => new(text);

    /// <summary>True if the filter takes <paramref name="entity"/> by its own name.</summary>
    public bool Matches(IEntity entity) =>
        _text == null
        || entity.Name.Contains(_text, StringComparison.OrdinalIgnoreCase)
        || (_bare != null
            && entity.Name.EndsWith(_bare, StringComparison.OrdinalIgnoreCase)
            && OwnTypeParameterCount(entity) == _count);

    /// <summary>
    /// How many type parameters ILens writes after an entity's name: a method's own, and those a
    /// type's own name states (<see cref="ParsedTypeName.SegmentArities"/>), so that a class nested
    /// in a generic class counts none of the enclosing class's, as <c>Dictionary&lt;,&gt;.Enumerator</c>
    /// is written.
    /// </summary>
    private static int OwnTypeParameterCount(IEntity entity) => entity switch
    {
        IMethod method => method.TypeParameters.Count,
        ITypeDefinition type => ParsedTypeName.SegmentArities(type.FullTypeName)[^1],
        _ => 0,
    };
}
