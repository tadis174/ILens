namespace ILens;

/// <summary>
/// Whether an exception thrown while a row is read through the decompiler's type system says
/// that the row cannot be decoded. Such a row comes from a tool that guards an assembly against
/// decompiling, or from a damaged file. A pass over the method bodies reads on past it.
/// </summary>
/// <remarks>
/// Library behavior, read in ICSharpCode.Decompiler 11.2.0.10050: the metadata reader throws
/// BadImageFormatException for a row it cannot read, but the type system's own types throw
/// ArgumentException for a signature they cannot be built from. <c>ArrayType</c>'s constructor
/// throws ArgumentOutOfRangeException for an array of rank 0, which
/// <c>TypeProvider.GetArrayType</c> passes on unchecked, and <c>ParameterizedType</c>'s throws
/// ArgumentException for a count of type arguments that does not match the generic type. The
/// runtime rejects an array of rank 0 as a bad image, and finds no method for a reference whose
/// signature holds one. ILSpy's own catches, as in <c>RequiredNamespaceCollector</c>, take
/// BadImageFormatException alone, so decompiling code that names such an array fails as a
/// whole, before the decompiler's own handling of code it cannot read. The cases: ZeroRankTests.
/// </remarks>
internal static class Decoding
{
    /// <summary>
    /// True where <paramref name="e"/>, thrown while a row was resolved or a signature decoded
    /// through the decompiler's type system, says that the row cannot be decoded.
    /// </summary>
    public static bool Failed(Exception e) => e is BadImageFormatException or ArgumentException;

    /// <summary>
    /// What <paramref name="resolve"/> gives, or null where it throws what <see cref="Failed"/>
    /// takes, so that a row that cannot be decoded names nothing. Only the resolution runs inside
    /// the catch, so that a fault in reading a declaration afterwards stays an error.
    /// </summary>
    public static T OrNull<T>(Func<T> resolve) where T : class
    {
        try
        {
            return resolve();
        }
        catch (Exception e) when (Failed(e))
        {
            return null;
        }
    }
}
