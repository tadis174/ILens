using System;
using System.Collections.Generic;

namespace CallShapes.Nullables
{
    /// <summary>
    /// Overloads that differ in a type C# writes with '?' after it, beside the type without it: a
    /// nullable value as a parameter, as a type argument, as an array's element, as an out
    /// parameter and as a conversion operator's return type; a type parameter's nullable form
    /// beside the type parameter, where that is a class (Keep) and where it is a struct (Hold); a
    /// string parameter, after which C# reads '?' as an annotation; and a nullable field, property
    /// and event.
    /// </summary>
    public class Meter
    {
        public int? Limit;

        public int? Reading { get; set; }

        public event Action<int?> Changed { add { } remove { } }

        public int Read(int value) => value;

        public int Read(int? value) => value ?? 0;

        public void Log(List<int> values) { }

        public void Log(List<int?> values) { }

        public void Fill(int[] values) { }

        public void Fill(int?[] values) { }

        public void Store(out int value) => value = 0;

        public void Store(out int? value) => value = null;

        public void Keep<T>(T value) where T : class { }

        public void Keep<T>(T? value) where T : struct { }

        public void Hold<T>(T value) where T : struct { }

        public void Hold<T>(T? value) where T : struct { }

        public void Label(string text) { }

        public static explicit operator int(Meter meter) => 0;

        public static explicit operator int?(Meter meter) => null;
    }

    /// <summary>Calls each of <see cref="Meter"/>'s nullable overloads.</summary>
    public class Reader
    {
        public int Total(Meter meter, int? start)
        {
            meter.Log(new List<int?> { start });
            meter.Fill(new int?[] { start });
            meter.Store(out int? stored);
            meter.Keep<int>(stored);
            meter.Hold<int>(start);
            int? converted = (int?)meter;
            return meter.Read(start) + meter.Read(converted.GetValueOrDefault());
        }
    }
}
