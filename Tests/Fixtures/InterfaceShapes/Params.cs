using System;
using System.Collections.Generic;

namespace InterfaceShapes.Params
{
    // Parameters that take any number of arguments: through a span, an enumerable and a list,
    // which C# 13 allows and the compiler marks with ParamCollectionAttribute, and through an
    // array, which it marks with ParamArrayAttribute; beside them a span without params and an
    // indexer over a params span.
    public class Gatherer
    {
        public int Count(params ReadOnlySpan<int> values) => values.Length;

        public int Join(string separator, params IEnumerable<string> parts) => separator.Length;

        public int Fill(params List<int> items) => items.Count;

        public int Sum(params int[] values) => values.Length;

        public int Plain(ReadOnlySpan<int> values) => values.Length;

        public int this[params ReadOnlySpan<int> keys] => keys.Length;
    }

    // Calls to them with arguments one by one, with none and with a whole collection. The
    // compiler builds the collection, and the decompiler, which knows no C# 13, passes it as one
    // argument, while a params array's call stays in its expanded form.
    public class Caller
    {
        public int Expanded(Gatherer gatherer, int first, string part) =>
            gatherer.Count(first, 2, 3) + gatherer.Join(",", part, "b") + gatherer.Fill(first, 5)
            + gatherer.Sum(first, 7);

        public int Indexed(Gatherer gatherer, int first) => gatherer[first, 2, 3];

        public int Empty(Gatherer gatherer) =>
            gatherer.Count() + gatherer.Join(",") + gatherer.Fill() + gatherer.Sum();

        public int Whole(Gatherer gatherer, int[] values, List<int> items) =>
            gatherer.Count(values) + gatherer.Fill(items) + gatherer.Sum(values);
    }
}
