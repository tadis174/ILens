using System;

namespace InterfaceShapes.Conditions
{
    public interface ISlot { }

    public class Base { }

    // A method for each condition a type parameter can have beyond those CallShapes.Constraints
    // holds: unmanaged, notnull and allows ref struct, which C# 13 allows, alone and after another
    // condition; several type parameters each with a clause of its own, one that names another, and
    // a second type parameter with a condition beside a first without one.
    public class Forms
    {
        public void Plain<T>(T item) { }

        public void Raw<T>(T item) where T : unmanaged { }

        public void Filled<T>(T item) where T : notnull { }

        public void Borrowed<T>(T item) where T : allows ref struct { }

        public void Slotted<T>(T item) where T : ISlot, allows ref struct { }

        public void Valued<T>(T item) where T : struct, allows ref struct { }

        public void Made<T>() where T : new(), allows ref struct { }

        public void Pair<TKey, TValue>(TKey key, TValue value)
            where TKey : class, IComparable<TKey>, new()
            where TValue : struct { }

        public void Within<T, U>(T outer, U inner) where U : T { }

        public void Second<T, U>(T first, U second) where U : Base { }
    }

    // A generic class whose heading has a base class and a clause, a class nested in it whose
    // heading has a clause for its own type parameter alone, and in the class a method that uses
    // its type parameter and a generic method without a condition, neither of which has a clause.
    public class Holder<T> : Base where T : class, ISlot, new()
    {
        public void Put(T item) { }

        public void Move<U>(U item) { }

        public class Inner<U> where U : struct
        {
            public void Take(T outer, U inner) { }
        }
    }

    public interface IStore<T> where T : notnull
    {
        void Keep(T item);
    }

    public delegate T Maker<T>() where T : new();

    public interface ISorter
    {
        void Sort<T>(T item) where T : IComparable<T>;
    }

    public abstract class Shaper
    {
        public abstract T Shape<T>(T item) where T : class, ISlot;

        public virtual void Fit<T>(T item) where T : struct { }
    }

    // An override, a sealed override and an abstract one take the conditions of the method they
    // override, and an explicit implementation those of the interface's method, so their source
    // writes no where clause; an implicit implementation writes the interface method's conditions.
    public class Cutter : Shaper
    {
        public override T Shape<T>(T item) => item;

        public sealed override void Fit<T>(T item) { }
    }

    public abstract class Trimmer : Shaper
    {
        public abstract override T Shape<T>(T item);
    }

    public class Sorter : ISorter
    {
        void ISorter.Sort<T>(T item) { }
    }

    public class OpenSorter : ISorter
    {
        public void Sort<T>(T item) where T : IComparable<T> { }
    }

    // An extension method whose receiver may be a ref struct, for the mark of one found through a
    // type.
    public static class Touches
    {
        public static int Touch<T>(this T value) where T : allows ref struct => 1;
    }
}
