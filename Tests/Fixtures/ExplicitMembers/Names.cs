using System;
using System.Runtime.CompilerServices;

// Names a lookup has to read by a rule of C#: a name a class and its base class use for
// different kinds of member, an indexer the class gave another name than Item, a
// constructor asked for by the name of its type, a struct and an enum without one, and
// extension methods, which a name reaches only on the class their first parameter takes.
namespace ExplicitMembers.Names
{
#pragma warning disable CS0067 // The events are looked up, never raised.

    public class Base
    {
        public int Size;

        public int Count => 2;

        public event EventHandler Changed;

        public int Value() => 1;

        public void Run() { }
    }

    // One class for each kind of member that hides a member of another kind in Base.
    public class FieldOverMethod : Base
    {
        public new int Value = 3;
    }

    public class MethodOverProperty : Base
    {
        public new int Count() => 4;
    }

    public class PropertyOverField : Base
    {
        public new int Size => 5;
    }

    public class FieldOverEvent : Base
    {
        public new int Changed;
    }

    public class EventOverMethod : Base
    {
        public new event EventHandler Run;
    }

#pragma warning restore CS0067

    // Declares nothing, so every name still means Base's member.
    public class Middle : Base { }

    // Hides the method two levels up.
    public class Far : Middle
    {
        public new int Value = 6;
    }

    // A field beside an extension method of its name.
    public class Labeled
    {
        public int Label;
    }

    public static class LabeledExtensions
    {
        public static int Label(this Labeled labeled) => 1;
    }

    // Two extension methods of one name for Stamped, with one of that name for Labeled between
    // them, which a lookup on Stamped passes over, and a name only Labeled's extension method has.
    public class Stamped { }

    public static class StampExtensions
    {
        public static int Stamp(this Stamped stamped) => 1;

        public static int Stamp(this Labeled labeled) => 2;

        public static int Stamp(this Stamped stamped, string text) => text.Length;

        public static int Mark(this Labeled labeled) => 3;
    }

    public static class Reader
    {
        public static int Read(FieldOverMethod subject) => subject.Value + ((Base)subject).Value();
    }

    public class Renamed
    {
        [IndexerName("Entry")]
        public int this[int index]
        {
            get => index;
            set { }
        }
    }

    public class RenamedTwice
    {
        [IndexerName("Entry")]
        public int this[int index] => index;

        [IndexerName("Entry")]
        public int this[string key]
        {
            get => 0;
            set { }
        }
    }

    public class RenamedReadOnly
    {
        [IndexerName("Entry")]
        public int this[int index] => index;
    }

    public class DerivedOfRenamed : Renamed { }

    public class Ctors
    {
        static Ctors() { }

        public Ctors() { }

        public Ctors(int amount) { }
    }

    public class Single
    {
        public Single(string text) { }
    }

    // Has a constructor of its own and none named Ctors.
    public class Sub : Ctors
    {
        public Sub() : base(1) { }
    }

    public class Generic<T>
    {
        public Generic(T value) { }
    }

    public class Outer
    {
        public class Inner
        {
            public Inner(int amount) { }
        }
    }

    public static class OnlyStatic
    {
        public static int Total;

        static OnlyStatic() { Total = 1; }
    }

    // A struct and an enum declare no constructor of their own; the file holds none for them.
    public struct Spot
    {
        public int X;
    }

    public struct SpotWithCtor
    {
        public int X;

        public SpotWithCtor(int x) { X = x; }
    }

    public enum Tint
    {
        Light,
        Dark,
    }

    public static class SpotMaker
    {
        public static Spot Make() => new Spot();

        public static SpotWithCtor MakeDefault() => new SpotWithCtor();

        public static SpotWithCtor MakeWith() => new SpotWithCtor(2);
    }

    public static class Maker
    {
        public static object MakeCtors() => new Ctors(1);

        public static object MakeGeneric() => new Generic<int>(3);

        public static object MakeInner() => new Outer.Inner(2);
    }
}
