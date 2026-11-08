namespace SearchText
{
    /// <summary>
    /// Code that makes the compiler add top-level classes of its own, which the decompiler writes
    /// no declaration for in the decompiled assembly: an anonymous type, and the helper class
    /// <c>&lt;PrivateImplementationDetails&gt;</c>, which holds the data of a constant array
    /// in a field and the type of that field nested in it.
    /// </summary>
    public class Generated
    {
        public object Pair(int left, string right)
        {
            return new { Left = left, Right = right };
        }

        public static readonly byte[] Table = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };
    }
}
