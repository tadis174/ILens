using System;
using System.Collections.Generic;

namespace CallShapes.Namesakes
{
    /// <summary>
    /// Methods that share their name and parameters with another method of the class and differ
    /// in their type parameters: a method beside a generic method, and two generic methods that
    /// differ in their count of type parameters alone. <see cref="Put{T}"/> is a generic method
    /// beside a method of its name with another parameter, and <see cref="Only{T}"/> one without
    /// a method of its name.
    /// </summary>
    public class Store
    {
        public object Get(string key) => null;

        public T Get<T>(string key) => default(T);

        public void Run() { }

        public void Run<T>() { }

        public void Take<T>(T item) { }

        public void Take<T, U>(T item) { }

        public void Put(string key) { }

        public void Put<T>(T item) { }

        public void Only<T>(T item) { }
    }

    /// <summary>Conversion operators that differ in their return type alone.</summary>
    public struct Amount
    {
        public static explicit operator int(Amount amount) => 0;

        public static explicit operator long(Amount amount) => 0;

        public static explicit operator decimal(Amount amount) => 0;
    }

    /// <summary>
    /// A generic method that <see cref="Cupboard"/>'s method of its name and parameters leaves
    /// callable, a method that <see cref="Cupboard"/>'s generic method of its name leaves
    /// callable, and a virtual method <see cref="Cupboard"/> overrides, which hides it.
    /// </summary>
    public class Shelf
    {
        public T Find<T>(string key) => default(T);

        public void Clear() { }

        public virtual void Open() { }
    }

    public class Cupboard : Shelf
    {
        public object Find(string key) => null;

        public void Clear<T>() { }

        public override void Open() { }
    }

    /// <summary>
    /// Calls each method of <see cref="Store"/>, a generic one made for the type arguments the
    /// code writes or the compiler infers, and a method of another assembly's class beside its
    /// generic namesake, which this assembly knows from its references alone.
    /// </summary>
    public class Customer
    {
        public void Use(Store store)
        {
            store.Get<int>("a");
            store.Get("b");
            store.Run<List<int>>();
            store.Run();
            store.Take(1);
            store.Take<string, int>("c");
            store.Only(2);
        }

        public object Create() => Activator.CreateInstance(typeof(Store));

        public Store CreateGeneric() => Activator.CreateInstance<Store>();
    }

    /// <summary>
    /// Conversion operators that differ in their return type alone, whose return types share
    /// a short name, <see cref="Alpha.Badge"/> and <see cref="Beta.Badge"/>, and two that
    /// convert from int and from int?.
    /// </summary>
    public struct Voucher
    {
        public static explicit operator Alpha.Badge(Voucher voucher) => null;

        public static explicit operator Beta.Badge(Voucher voucher) => null;

        public static implicit operator Voucher(int cents) => default(Voucher);

        public static implicit operator Voucher(int? cents) => default(Voucher);
    }

    /// <summary>
    /// Three pairs of methods, each a method that takes a value beside one that takes the value
    /// by reference (<c>Truncate</c>), as an out parameter (<c>Pass</c>) or as a nullable value
    /// (<c>Shift</c>). The value's type, written without a keyword, fits both methods of a pair
    /// when ILens matches it loosely.
    /// </summary>
    public static class Scales
    {
        public static decimal Truncate(decimal value)
        {
            Truncate(ref value);
            return value;
        }

        private static void Truncate(ref decimal value) => value = decimal.Truncate(value);

        public static int Pass(int count) => count;

        public static int Pass(out int count)
        {
            count = 1;
            return count;
        }

        public static int Shift(int count) => count;

        public static int Shift(int? count) => count ?? 0;
    }

    /// <summary>Two classes of one name in two enclosing classes.</summary>
    public class Box
    {
        public class Entry { }
    }

    public class Bag
    {
        public class Entry { }
    }

    /// <summary>
    /// Overloads whose parameter types share a short name: classes of one name in two
    /// namespaces, classes of one name in two enclosing classes, and lists of the first two.
    /// </summary>
    public class Basket
    {
        public void Take(Alpha.Badge badge) { }

        public void Take(Beta.Badge badge) { }

        public void Take(Box.Entry entry) { }

        public void Take(Bag.Entry entry) { }

        public void Hold(List<Alpha.Badge> badges) { }

        public void Hold(List<Beta.Badge> badges) { }
    }

    /// <summary>
    /// A method whose type parameter hides its class's beside one whose type parameter does
    /// not, both taking a parameter of the type named T: the method's and the class's.
    /// </summary>
    public class Hamper<T>
    {
        public void Take<U>(T item) { }

#pragma warning disable CS0693 // The method's type parameter hides the class's, which is the case under test.
        public void Take<T>(T item) { }
#pragma warning restore CS0693
    }

    public class Visitor { }

    public class Visitor<TResult> { }

    /// <summary>A method beside one whose parameter type is a generic class of the same name.</summary>
    public class Kiosk<T>
    {
        public void Accept(Visitor visitor) { }

        public void Accept(Visitor<T> visitor) { }
    }

    /// <summary>
    /// A base class's methods below a derived class's namesakes that take the value by
    /// reference and as a nullable value, which a parameter type written loosely fits first.
    /// </summary>
    public class Counter
    {
        public void Mark(int count) { }

        public void Fill(int count) { }
    }

    public class Booth : Counter
    {
        public void Mark(ref int count) { }

        public void Fill(int? count) { }
    }

    /// <summary>
    /// Converts with <see cref="Amount"/>'s operators to int and to long, and a decimal of the
    /// framework to each, whose operators this assembly knows from its references alone;
    /// converts with each of <see cref="Voucher"/>'s operators to a badge; and passes a badge
    /// and a list of badges of each namespace to <see cref="Basket"/>.
    /// </summary>
    public class Cashier
    {
        public int WholeAmount(Amount amount) => (int)amount;

        public long WideAmount(Amount amount) => (long)amount;

        public int WholeTotal(decimal total) => (int)total;

        public long WideTotal(decimal total) => (long)total;

        public void Redeem(Voucher voucher, List<object> into)
        {
            into.Add((Alpha.Badge)voucher);
            into.Add((Beta.Badge)voucher);
        }

        public void Sort(Basket basket)
        {
            basket.Take(new Alpha.Badge());
            basket.Take(new Beta.Badge());
        }

        public void Stock(Basket basket)
        {
            basket.Hold(new List<Alpha.Badge>());
            basket.Hold(new List<Beta.Badge>());
        }
    }
}

namespace CallShapes.Namesakes.Alpha
{
    /// <summary>A class that shares its name with a class of another namespace.</summary>
    public class Badge { }
}

namespace CallShapes.Namesakes.Beta
{
    public class Badge { }
}
