using System;

namespace ExternalLib
{
    public class Widget
    {
        public int Size;
        public static int Count;

        public Widget() { }
        public Widget(int size) { Size = size; }

        public string Label { get; set; }
        public event EventHandler Changed;

        public void Paint() { }
        public void Paint(int times) { }
        public virtual void Draw() { }

        // Raises Changed. ExternalUser never calls it, so ILens never sees it.
        public void Touch() => Changed?.Invoke(this, EventArgs.Empty);
    }

    public class Gadget<T>
    {
        public void Add(T item) { }
    }

    public interface IShape
    {
        double Area();
    }

    public class Canvas : IShape
    {
        public double Area() => 0;
    }

    public struct Point
    {
        public int X;
    }

    public struct Spot
    {
        public int X;

        public void Move() { X++; }
    }

    // Fields ExternalUser uses through their address.
    public class Meter
    {
        public int Reading;
        public Spot Place;
    }

    // Overloads that differ only in taking a signed or an unsigned native-sized integer.
    public static class Native
    {
        public static void Release(IntPtr handle) { }
        public static void Release(UIntPtr handle) { }
    }

    public enum Shade
    {
        Light,
        Dark,
    }

    // Constants, whose values the compiler copies into each place that uses them. Rate, a
    // decimal, is compiled as a static field the static constructor sets.
    public static class Limits
    {
        public const int Most = 10;
        public const decimal Rate = 1.5m;

        public static class Deep
        {
            public const int Floor = 3;
        }
    }

    public class BaseLimits
    {
        public const int Least = 1;
        public const int Step = 2;
    }

    // Inherits Least, and hides the constant Step with a field that is not one.
    public class DerivedLimits : BaseLimits
    {
        public new static int Step;
    }

    [AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
    public sealed class MarkAttribute : Attribute
    {
        public MarkAttribute() { }
        public MarkAttribute(Type type) { }
        public MarkAttribute(Shade shade) { }
    }

    // An interface ExternalUser implements: a method, a property and an event.
    public interface IGauge
    {
        int Read();
        string Unit { get; }
        event EventHandler Moved;
    }

    // An attribute class ExternalUser derives an attribute of its own from.
    public class TagAttribute : Attribute { }

    [Tag]
    public class Tagged { }

    // Named by ExternalUser only as a typeof argument of an attribute, which the compiler
    // writes into the attribute's blob rather than into a reference row.
    public class OnlyInTypeof { }

    // An attribute ExternalUser applies to its assembly and to its module, where it belongs to
    // no type or member.
    [AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Module, AllowMultiple = true)]
    public sealed class ScopeAttribute : Attribute
    {
        public ScopeAttribute(Type type) { }
    }

    // Each named by ExternalUser only as a typeof argument of one such attribute.
    public class OnlyOnAssembly { }

    public class OnlyOnModule { }

    // Named by ExternalUser only in an attribute on its module, in an array of types that
    // follows a value of an enum of ExternalUser's own.
    public class OnlyAfterEnum { }

    // Named by ExternalUser as the type of an argument of an attribute on its assembly.
    public enum Tint
    {
        Pale,
        Deep,
    }

    // Named by ExternalUser as the type of an argument of attributes on its types and members.
    public enum Finish
    {
        Matte,
        Gloss,
    }

    // Enums whose values take one byte and eight, where most enums' take four. ExternalUser
    // gives their values to attributes, whose bytes hold a value without saying how many bytes
    // it takes.
    public enum Grain : byte
    {
        Coarse,
        Fine,
    }

    public enum Reach : long
    {
        Near,
        Far,
    }

    // Named by ExternalUser only as a typeof argument of an attribute, beside a value of Grain.
    public class OnlyBesideGrain { }

    // Named by ExternalUser only as a typeof argument that follows a value of Grain. The
    // attribute's bytes spell the class with its assembly, as "ExternalLib.NamedWhereEightBytesFits,
    // ExternalLib, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null", which is 104
    // characters. Read with eight bytes for the value where it takes one, the value swallows
    // the length and the name's first six characters; the seventh, 'a', is then read as the
    // length 97, and 97 characters are left, so that reading fits the bytes as well. Another
    // name, namespace or assembly version ends that.
    public class NamedWhereEightBytesFits { }

    // Named by ExternalUser's TypeNames.Namer alone, each only as a type and through no member.
    public class Casing { }

    public struct Notch
    {
        public int Depth;
    }

    public class Tray<T> { }

    // Called by ExternalUser's Keywords.Clerk alone: a parameter passed by reference with each
    // keyword, which only this assembly's metadata says.
    public class Vault
    {
        public bool TryTake(int key, out string item)
        {
            item = null;
            return false;
        }

        public int Weigh(in int load, ref int total) => total += load;

        public int Peek(ref readonly int slot) => slot;
    }

    // Used by ExternalUser's Applied.Sealed alone, which applies it through two of its
    // constructors and creates it through one of them in code.
    public sealed class SealAttribute : Attribute
    {
        public SealAttribute() { }
        public SealAttribute(int rank) { }
    }

    // An attribute class whose members ExternalUser's SetByName.cs sets by name, where an
    // attribute names a member by its name alone: a property code sets as well, a property only
    // attributes set, a field, and a property nothing sets.
    public class TraitAttribute : Attribute
    {
        public string Kind { get; set; }
        public string Caption { get; set; }
        public int Rank;
        public string Unset { get; set; }
    }

    // Inherits every member, so an attribute of this class sets TraitAttribute's.
    public class SubTraitAttribute : TraitAttribute { }

    // Declares Kind anew, so an attribute of this class sets its own.
    public class OwnTraitAttribute : TraitAttribute
    {
        public new string Kind { get; set; }
    }

    // No attribute class, with a property of the name of one TraitAttribute has.
    public class Plaque
    {
        public string Caption { get; set; }
    }

    // An enum whose values take one byte, which SetByName.cs alone gives to an attribute.
    public enum Pitch : byte
    {
        Low,
        High,
    }
}
