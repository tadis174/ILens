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

    [AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
    public sealed class MarkAttribute : Attribute
    {
        public MarkAttribute() { }
        public MarkAttribute(Type type) { }
        public MarkAttribute(Shade shade) { }
    }

    // Named by ExternalUser only as a typeof argument of an attribute, which the compiler
    // writes into the attribute's blob rather than into a reference row.
    public class OnlyInTypeof { }
}
