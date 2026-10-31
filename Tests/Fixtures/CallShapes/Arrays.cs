using System;
using System.Collections.Generic;

namespace CallShapes.Arrays
{
    // Extension methods for arrays, for the tests on finding one through an array type, on the
    // list extension_methods gives for an array and on the mark that names the receiver: for an
    // array of one element type, of a class, of two dimensions and of arrays; for an array of a
    // type parameter, with and without constraints, and of two dimensions; and for what an array
    // implements or derives from, IReadOnlyCollection<T> among it. Reel derives from Spool, so an
    // array of Reel converts to the receiver of Wind and of Gauge, and Shingle does not meet the
    // constraints of Crest. CopyTo is named like an instance method of System.Array, which comes
    // first without a hint, and Reverse like a static one, which does not. Length, for an array of
    // Bobbin, is named like a property of System.Array, which C# does not call.
    public class Spool
    {
    }

    public class Bobbin
    {
    }

    public class Reel : Spool
    {
    }

    public struct Shingle
    {
    }

    public static class ArrayExtensions
    {
        public static string Hex(this byte[] bytes) => bytes.Length.ToString();

        public static int Wind(this Spool[] spools) => spools.Length;

        public static int Squares(this int[,] grid) => grid.Length;

        public static int Tiers(this int[][] jagged) => jagged.Length;

        public static bool InBounds<T>(this T[] array, int index) => index >= 0 && index < array.Length;

        public static T Crest<T>(this T[] values) where T : struct, IComparable<T> => values[0];

        public static int Tiles<T>(this T[,] grid) => grid.Length;

        public static T Front<T>(this IEnumerable<T> items)
        {
            foreach (var item in items)
                return item;
            return default(T);
        }

        public static int Tally<T>(this IReadOnlyCollection<T> items) => items.Count;

        public static int Gauge(this IReadOnlyCollection<Spool> spools) => spools.Count;

        public static int Breadth(this Array array) => array.Length;

        public static string Caption(this object value) => value.ToString();

        public static void CopyTo(this int[] values, List<int> target) => target.AddRange(values);

        public static int[] Reverse(this int[] values) => values;

        public static int Length(this Bobbin[] bobbins) => bobbins.Length;
    }

    public static class Users
    {
        public static void Use(byte[] bytes, Spool[] spools, Reel[] reels, int[,] grid, int[][] jagged,
            Shingle[] shingles, int[] numbers, List<int> list)
        {
            bytes.Hex();
            spools.Wind();
            reels.Wind();
            grid.Squares();
            jagged.Tiers();
            shingles.InBounds(0);
            numbers.Crest();
            grid.Tiles();
            bytes.Front();
            bytes.Tally();
            reels.Gauge();
            bytes.Breadth();
            bytes.Caption();
            numbers.CopyTo(list);
            numbers.Reverse();
        }

        public static int Wound(Bobbin[] bobbins) => bobbins.Length();
    }
}
