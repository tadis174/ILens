using System;
using System.Collections;
using System.Collections.Generic;

namespace ExplicitMembers
{
    public interface IShape
    {
        double Area();
        string Name { get; }
        event EventHandler Changed;
    }

    public interface ISized
    {
        int Size { get; }
    }

    // Implements IShape explicitly throughout, so no ordinary member has the names of its
    // members. Size is both an ordinary field and an explicit property, and Equals both an
    // ordinary override and an explicit IEquatable<Circle>.Equals.
    public class Circle : IShape, ISized, IEquatable<Circle>
    {
        public int Size;

        double IShape.Area() => 3.14;

        string IShape.Name => "circle";

        event EventHandler IShape.Changed
        {
            add { }
            remove { }
        }

        int ISized.Size => Size;

        public override bool Equals(object obj) => obj is Circle other && ((IEquatable<Circle>)this).Equals(other);

        bool IEquatable<Circle>.Equals(Circle other) => other != null && other.Size == Size;

        public override int GetHashCode() => Size;
    }

    // Uses IShape through the interface, the only way code reaches Circle's implementations.
    public static class ShapeUser
    {
        public static double Measure(IShape shape) => shape.Area();

        public static string Label(IShape shape) => shape.Name;
    }

    // Declares nothing, so every explicit implementation it has is Circle's.
    public class Ring : Circle { }

    // GetEnumerator is declared both ordinarily and explicitly, with the same parameters.
    public class Bag : IEnumerable<int>
    {
        public IEnumerator<int> GetEnumerator() => new List<int>().GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    // Both GetEnumerator methods are explicit, so the plain name cannot tell them apart.
    public class Vault : IEnumerable<int>
    {
        IEnumerator<int> IEnumerable<int>.GetEnumerator() => new List<int>().GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable<int>)this).GetEnumerator();
    }
}
