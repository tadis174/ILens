using ExternalLib.Arrays;

namespace ExternalUser.Arrays
{
    // Extension methods for arrays of ExternalLib's classes, for the tests on finding them through
    // Beam[] with and without ExternalLib: Stack for an array of Beam itself, and once for a string,
    // which no array reaches; Saw for an array of its base class Plank; Glue once for each array
    // under one name; and Sand for an array of a type parameter that asks for Plank. Without
    // ExternalLib what Beam derives from is not known, so only the methods for Beam[] itself are
    // found, and the answers say what was not read.
    public static class PlankExtensions
    {
        public static int Stack(this Beam[] beams) => beams.Length;

        public static int Stack(this string label) => label.Length;

        public static int Saw(this Plank[] planks) => planks.Length;

        public static int Glue(this Beam[] beams) => beams.Length;

        public static int Glue(this Plank[] planks, int coats) => planks.Length * coats;

        public static int Sand<T>(this T[] items) where T : Plank => items.Length;
    }

    // Extension methods for an array of nint and of dynamic, which the decompiled code writes as C#
    // does, for the tests on a question that names the array by System.IntPtr or object. Nothing in
    // this assembly names System.Array, for the tests on a member of it asked through an array.
    public static class NativeArrays
    {
        public static int Sum(this nint[] values) => values.Length;

        public static int Dyn(this dynamic[] values) => values.Length;
    }

    public static class Joinery
    {
        public static void Build(Beam[] beams)
        {
            beams.Stack();
            beams.Saw();
            beams.Glue();
            beams.Glue(2);
            beams.Sand();
        }

        public static int Count(nint[] values, object[] items, string label) => values.Sum() + items.Dyn() + label.Stack();
    }
}
