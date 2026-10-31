namespace BaseShift.Modifiers
{
    // Structs, classes and records whose heading changes between this assembly and BaseShift2 in a
    // word before the type's name: Frozen becomes readonly and Pinned a ref struct, with nothing
    // else changed; Point becomes a record and Pair a record struct, so both gain the members a
    // record brings, and Ticket stops being a record and loses them. compare lists these five with
    // the kind metadata and none of the types below them, which keep their words: a readonly
    // struct, a ref struct and a record.
#if SHIFTED
    public readonly struct Frozen
    {
        public readonly int Value;
    }

    public ref struct Pinned
    {
        public int Value;
    }

    public record Point
    {
        public int X { get; set; }
    }

    public record struct Pair
    {
        public int Left { get; set; }
    }

    public class Ticket
    {
        public int Number { get; set; }
    }
#else
    public struct Frozen
    {
        public readonly int Value;
    }

    public struct Pinned
    {
        public int Value;
    }

    public class Point
    {
        public int X { get; set; }
    }

    public struct Pair
    {
        public int Left { get; set; }
    }

    public record Ticket
    {
        public int Number { get; set; }
    }
#endif

    public readonly struct Steady
    {
        public readonly int Value;
    }

    public ref struct Cursor
    {
        public int Position;
    }

    public record Entry
    {
        public string Name { get; set; }
    }
}
