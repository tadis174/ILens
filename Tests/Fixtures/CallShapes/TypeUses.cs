using System;

namespace CallShapes.TypeUses
{
    public class Part { }

    public class Bolt : Part { }

    public struct Slot
    {
        public int Index;
    }

    public class Rack<T> { }

    public static class Maker
    {
        public static Part Make() => new Part();

        public static void Take(Part part) { }
    }

    /// <summary>
    /// One method for each way code names a type without using a member of it, and two whose
    /// code names no type: one that has a local of a type and one that calls a method whose
    /// signature names a type.
    /// </summary>
    public class Namer
    {
        public object Cast(object value) => (Bolt)value;

        public bool Is(object value) => value is Bolt;

        public object As(object value) => value as Bolt;

        public object Array() => new Bolt[2];

        public Slot Default() => default(Slot);

        public Slot Unboxed(object value) => (Slot)value;

        public Type Token() => typeof(Bolt);

        public object Constructed(object value) => (Rack<Bolt>)value;

        public Slot? Nothing() => null;

        public object Framework(object value) => (IDisposable)value;

        public void LocalOnly()
        {
            Part part = Maker.Make();
            Maker.Take(part);
        }

        public void SignatureOnly() => Maker.Take(null);
    }
}
