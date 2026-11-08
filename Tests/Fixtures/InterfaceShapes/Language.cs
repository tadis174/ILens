using System;
using System.Collections.Generic;

namespace InterfaceShapes.Language
{
    // Code whose decompiled form turns on the C# version the decompiler writes. Level is written
    // with C# 14's field keyword, which a decompiler for an earlier version writes over the
    // backing field the compiler declares. Trimmed calls AsSpan on the receiver and Same on an
    // argument, which C# 14's first-class span conversions would let a decompiler write as a cast
    // or leave out.
    public class Gauge
    {
        public int Level
        {
            get => field;
            set => field = Math.Max(0, value);
        }

        public int Trimmed(string text) => text.AsSpan().Trim().Length;

        public bool Same(char[] left, string right) => left.AsSpan().SequenceEqual(right.AsSpan());
    }

    // A struct whose readonly property has a getter and no setter: the decompiler writes the
    // readonly on the property, which the getter decompiled alone still carries.
    public struct Meter
    {
        private int _reading;

        public readonly int Reading
        {
            get { return _reading; }
        }

        public void Add(int amount) => _reading += amount;
    }

    // Extension blocks of C# 14, which a decompiler for an earlier version writes as the skeleton
    // the compiler stores beside the method it makes of each member, get_Doubled(Gauge) for
    // Doubled's getter. The members beside Doubled give each shape a block's member takes: a
    // property with both accessors, a method, a static method and an internal one. A second block
    // has a type parameter and a condition on it. A third is for Gauge as well, with an attribute
    // on its receiver: the compiler keeps it apart from the first, while a line that writes no
    // attribute reads alike the first's.
    public static class GaugeExtensions
    {
        extension(Gauge gauge)
        {
            public int Doubled => gauge.Level * 2;

            public int Offset
            {
                get => gauge.Level - 1;
                set => gauge.Level = value + 1;
            }

            public void Reset() => gauge.Level = 0;

            public static Gauge Empty() => new Gauge();

            internal void Calibrate() => gauge.Level = 1;
        }

        extension<T>(List<T> readings) where T : IComparable<T>
        {
            public T First() => readings[0];
        }

        extension([System.Diagnostics.CodeAnalysis.NotNull] Gauge gauge)
        {
            public bool IsZero() => gauge.Level == 0;
        }
    }
}
