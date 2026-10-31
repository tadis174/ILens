using System;
using System.Collections;

namespace CallShapes.Forms
{
    /// <summary>A value with operators, each called by a method of <see cref="Sums"/>.</summary>
    public struct Coin
    {
        public int Cents;

        public Coin(int cents) { Cents = cents; }

        public static Coin operator +(Coin left, Coin right) => new Coin(left.Cents + right.Cents);

        public static Coin operator -(Coin coin) => new Coin(-coin.Cents);

        public static Coin operator ++(Coin coin) => new Coin(coin.Cents + 1);

        public static bool operator ==(Coin left, Coin right) => left.Cents == right.Cents;

        public static bool operator !=(Coin left, Coin right) => left.Cents != right.Cents;

        public static bool operator true(Coin coin) => coin.Cents != 0;

        public static bool operator false(Coin coin) => coin.Cents == 0;

        public static implicit operator Coin(int cents) => new Coin(cents);

        public static explicit operator string(Coin coin) => coin.Cents.ToString();

        public override bool Equals(object other) => other is Coin coin && coin.Cents == Cents;

        public override int GetHashCode() => Cents;
    }

    /// <summary>
    /// One call to an operator of <see cref="Coin"/> per syntax that calls one: a binary and a
    /// unary operator, a compound assignment, an increment, a comparison, a cast, a conversion
    /// the source does not write, as a returned value, as an operand and where it has to stay a
    /// cast, and the <c>true</c> operator a condition calls, which the decompiled C# gives no
    /// sign of.
    /// </summary>
    public class Sums
    {
        public Coin Add(Coin a, Coin b) => a + b;

        public Coin Negate(Coin a) => -a;

        public Coin AddTo(Coin a, Coin b) { a += b; return a; }

        public Coin Step(Coin a) { a++; return a; }

        public bool Same(Coin a, Coin b) => a == b;

        public string Text(Coin a) => (string)a;

        public Coin FromNumber(int cents) => cents;

        public Coin AddNumber(Coin a) => a + 5;

        public object Boxed(int cents) => (Coin)cents;

        public int IfSet(Coin a) { if (a) return 1; return 0; }
    }

    /// <summary>A text with a mark, which a string takes in with + and which converts back to a string.</summary>
    public struct Label
    {
        public string Text;

        public static Label operator +(string left, Label right) => new Label { Text = left + right.Text };

        public static implicit operator string(Label label) => label.Text;
    }

    /// <summary>A result that converts to a bool, so that a condition can test it.</summary>
    public struct Verdict
    {
        public bool Passed;

        public static implicit operator bool(Verdict verdict) => verdict.Passed;
    }

    /// <summary>
    /// Appends a <see cref="Label"/> to a string with +=, which calls the operator and converts
    /// its result back, and tests a <see cref="Verdict"/> in a condition as it is and negated.
    /// The decompiler marks neither the operator nor the negated conversion the usual way.
    /// </summary>
    public class Notes
    {
        public string Append(string text, Label label)
        {
            text += label;
            return text;
        }

        public int When(Verdict verdict)
        {
            if (verdict)
                return 1;
            return 0;
        }

        public int Unless(Verdict verdict)
        {
            if (!verdict)
                return 1;
            return 0;
        }
    }

    /// <summary>
    /// A collection whose enumerator the compiler disposes, so that the decompiler writes a loop
    /// over it as a foreach, with two Add methods for a collection initializer, properties for
    /// a property pattern and a nested initializer, and a Deconstruct method.
    /// </summary>
    public class Till : IEnumerable
    {
        private readonly Coin[] _coins = new Coin[4];

        public int Count { get; set; }

        public bool Empty => Count == 0;

        public Till Spare { get; set; }

        public void Add(Coin coin) { }

        public void Add(Coin coin, string note) { }

        public void Deconstruct(out int count, out bool empty) { count = Count; empty = Empty; }

        public Walker GetEnumerator() => new Walker(_coins);

        IEnumerator IEnumerable.GetEnumerator() => _coins.GetEnumerator();

        public struct Walker : IDisposable
        {
            private readonly Coin[] _coins;
            private int _at;

            public Walker(Coin[] coins) { _coins = coins; _at = -1; }

            public Coin Current => _coins[_at];

            public bool MoveNext() => ++_at < _coins.Length;

            public void Dispose() { }
        }
    }

    /// <summary>A collection with a type parameter.</summary>
    public class Tray<T> : IEnumerable
    {
        public void Add(T item) { }

        IEnumerator IEnumerable.GetEnumerator() => new T[0].GetEnumerator();
    }

    /// <summary>Holds a <see cref="Tray{T}"/> of coins in a property an initializer can fill.</summary>
    public class Cabinet
    {
        public Tray<Coin> Coins { get; } = new Tray<Coin>();
    }

    /// <summary>
    /// One use of <see cref="Till"/> per statement that calls its members without writing a
    /// call: a foreach, a collection initializer with an entry of one argument and one of two,
    /// an initializer that fills the collection a property holds, once with an entry that
    /// converts on its way in, a test the decompiler writes as a property pattern, and a
    /// deconstruction.
    /// </summary>
    public class Tellers
    {
        public int Each(Till till)
        {
            int sum = 0;
            foreach (Coin coin in till)
                sum += coin.Cents;
            return sum;
        }

        public Till Filled(Coin a, Coin b) => new Till { a, { b, "note" } };

        public Till WithSpare(Coin a, Coin b) => new Till { Count = 2, Spare = { a, b } };

        // The entry converts on its way in. For a collection with a type parameter the decompiler
        // writes the conversion as a cast and leaves the instruction that calls it on what is
        // cast, so that two nodes stand for the one call.
        public Cabinet Stocked(int cents) => new Cabinet { Coins = { cents } };

        public int Open(object drawer)
        {
            if (drawer is Till till && !till.Empty)
                return till.Count;
            return 0;
        }

        public int Split(Till till)
        {
            var (count, empty) = till;
            return empty ? 0 : count;
        }
    }

    /// <summary>A collection with a virtual property and a virtual Add method.</summary>
    public class Drawer : IEnumerable
    {
        public int Depth;

        public virtual bool Stuck => false;

        public virtual void Add(Coin coin) { }

        IEnumerator IEnumerable.GetEnumerator() => new Coin[0].GetEnumerator();
    }

    /// <summary>Overrides both, so that a call's receiver decides whether it is a call to the override.</summary>
    public class LockedDrawer : Drawer
    {
        public override bool Stuck => true;

        public override void Add(Coin coin) { }
    }

    /// <summary>
    /// Reads <see cref="Drawer.Stuck"/> in a test the decompiler writes as a property pattern,
    /// and calls <see cref="Drawer.Add"/> from a collection initializer, each once on the base
    /// class and once on the class that overrides. The IL names the base class's member in all
    /// four.
    /// </summary>
    public class Drawers
    {
        public int Shut(object drawer)
        {
            if (drawer is Drawer plain && !plain.Stuck)
                return plain.Depth;
            return 0;
        }

        public int ShutLocked(object drawer)
        {
            if (drawer is LockedDrawer locked && !locked.Stuck)
                return locked.Depth;
            return 0;
        }

        public Drawer Fill(Coin a) => new Drawer { a };

        public Drawer FillLocked(Coin a) => new LockedDrawer { a };
    }

    /// <summary>A base class whose constructor takes nothing.</summary>
    public class Desk
    {
        public Desk() { }
    }

    /// <summary>A base class whose constructor can be called without an argument.</summary>
    public class SizedDesk
    {
        public SizedDesk(int size = 3) { }
    }

    /// <summary>Two constructors that call the base constructor without writing it.</summary>
    public class FrontDesk : Desk
    {
        private int _number;

        public FrontDesk() { }

        public FrontDesk(int number) { _number = number; }
    }

    /// <summary>A class without a constructor of its own: the compiler writes one that calls the base constructor.</summary>
    public class SpareDesk : Desk
    {
    }

    /// <summary>One constructor that leaves the base constructor's argument at its default, and one that passes it.</summary>
    public class WideDesk : SizedDesk
    {
        public WideDesk() { }

        public WideDesk(int size) : base(size) { }
    }

    /// <summary>A delegate type, which code creates without writing a call of its constructor.</summary>
    public delegate int Rate(int cents);

    /// <summary>
    /// Creates a <see cref="Rate"/> once in one method and twice in another, from a method and
    /// from a lambda. The decompiled C# shows neither as a call.
    /// </summary>
    public class Rates
    {
        private static int Twice(int cents) => 2 * cents;

        public Rate One() => Twice;

        public int Two()
        {
            Rate first = Twice;
            Rate second = cents => cents + 1;
            return first(1) + second(2);
        }
    }
}
