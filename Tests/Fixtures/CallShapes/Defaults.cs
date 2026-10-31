using System;

namespace CallShapes.Defaults
{
    // Parameters that take a default of each kind C# writes, and constants beside them, for the
    // tests on how list_members writes a default and a constant's value and on how call_sites
    // writes the default a call leaves out. Pace's members are numbered by the compiler, Edges is
    // a set of flags, and Locker's Tier is nested in a class. Blank declares no member, so it holds
    // nothing but the field the compiler adds to every enum for its value.
    public enum Pace
    {
        Stop,
        Slow,
        Fast,
    }

    public enum Blank
    {
    }

    [Flags]
    public enum Edges
    {
        None = 0,
        Left = 1,
        Right = 2,
        Top = 4,
    }

    public struct Dot
    {
        public int X;
    }

    public class Locker
    {
        public enum Tier
        {
            Low = 1,
            High = 2,
        }
    }

    public class Presets
    {
        public const bool On = true;
        public const bool Off = false;
        public const long Big = 5000000000;
        public const float Half = 0.5f;
        public const double Whole = 2;
        public const decimal Price = 1.25m;
        public const char Tab = '\t';
        public const string Path = "a\"b\\c\n";
        public const int Most = int.MaxValue;
        public const Pace Usual = Pace.Slow;
        public const Edges Sides = Edges.Left | Edges.Right;
    }

    public class Fallbacks
    {
        public Fallbacks(int size = 1) { }

        public int this[int row, int column = 0] => row + column;

        public void Count(int count = 5) { }

        public void Flag(bool flag = true) { }

        public void Letter(char letter = '\'') { }

        public void Text(string text = "a\"b") { }

        public void NoText(string text = null) { }

        public void Place(Dot dot = default(Dot)) { }

        public void NoPace(Pace? pace = null) { }

        public void Scale(float scale = 0.5f) { }

        public void Ratio(double ratio = 2) { }

        public void Pay(decimal amount = 1.5m) { }

        public void Wide(long value = 5000000000) { }

        public void Most(int value = int.MaxValue) { }

        public void Missing(double value = double.NaN) { }

        public void Go(Pace pace = Pace.Fast) { }

        public void Frame(Edges edges = Edges.Left | Edges.Top) { }

        public void Odd(Pace pace = (Pace)7) { }

        public void Stack(Locker.Tier tier = Locker.Tier.High) { }

        public void Maybe(Pace? pace = Pace.Slow) { }

        public void Read(in int value = 3) { }

        public void Calls()
        {
            Go();
            Go(Pace.Slow);
            Go(Pace.Fast);
        }
    }
}
