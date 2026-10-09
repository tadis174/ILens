using System;
using System.Collections.Generic;
using System.Threading;

namespace FieldAccess
{
    /// <summary>A struct whose methods each do one thing with the value they are called on.</summary>
    public struct Counter
    {
        public int Value;

        public int Peek() => Value;

        public readonly int PeekReadonly() => Value;

        public void Increment() { Value++; }

        // Writes the value without reading it.
        public void Reset() { this = default; }

        public void Nothing() { }
    }

    /// <summary>No method of a readonly struct can change its value.</summary>
    public readonly struct Frozen
    {
        public readonly int Value;

        public Frozen(int value) { Value = value; }

        public int Twice() => Value * 2;
    }

    /// <summary>Two methods that call each other on the same value.</summary>
    public struct Ring
    {
        public int Value;

        public void Spin(int turns) { if (turns > 0) Spun(turns - 1); }

        public void Spun(int turns) { if (turns > 0) Spin(turns - 1); }
    }

    public enum Shade
    {
        Light,
        Dark,
    }

    /// <summary>A field of a type parameter, whose methods could be any struct's.</summary>
    public class Box<T>
    {
        public T Item;

        public string ItemText() => Item.ToString();
    }

    /// <summary>
    /// Fields used through the address the compiler takes, one way per method. No method here
    /// loads or stores a field directly, except <see cref="SetAndAddShared"/>, which does both.
    /// </summary>
    public class Holder
    {
        public int Number;
        public static int Total;
        public Shade Tone;
        public Counter Tally;
        public Frozen Ice;
        public Ring Loop;
        public int? Maybe;
        public List<int>.Enumerator Cursor;
        public int Shared;
        public Counter Kept;

        // Read only.
        public string NumberText() => Number.ToString();
        public static string TotalText() => Total.ToString();
        public string ToneText() => Tone.ToString();
        public string TallyText() => Tally.ToString();
        public int TallyValue() => Tally.Value;
        public int PeekTally() => Tally.Peek();
        public int PeekTallyReadonly() => Tally.PeekReadonly();
        public int IceTwice() => Ice.Twice();
        public bool HasMaybe() => Maybe.HasValue;
        public int PassTallyIn() => ReadIn(in Tally);
        public int LookAtNumber() => Look(ref Number);
        public Func<string> NumberLater() => () => Number.ToString();

        // Written only.
        public void SetTallyValue() { Tally.Value = 5; }
        public void ClearTally() { Tally = default; }
        public void ClearMaybe() { Maybe = null; }
        public void ResetTally() { Tally.Reset(); }
        public void FillNumber() { Fill(out Number); }
        public void OverwriteNumber() { Overwrite(ref Number); }
        // Used twice, the reference stays in the local.
        public void SetThroughRefLocal(bool twice)
        {
            ref int number = ref Number;
            number = 7;
            if (twice)
                number = 8;
        }

        // Read and written.
        public void IncrementTally() { Tally.Increment(); }
        public void BumpNumber() { Bump(ref Number); }

        // Neither read nor written.
        public void IgnoreNumber() { Ignore(ref Number); }
        public void NothingOnTally() { Tally.Nothing(); }

        // Not known: the code that receives the address is in another assembly, calls back into
        // itself, or is not in this method.
        public bool AdvanceCursor() => Cursor.MoveNext();
        public void AddShared() { Interlocked.Increment(ref Shared); }
        public bool ParseNumber(string text) => int.TryParse(text, out Number);
        public void SpinLoop() { Loop.Spin(3); }
        public ref Counter KeptRef() => ref Kept;

        // A store, and a use not known to read or write.
        public void SetAndAddShared()
        {
            Shared = 1;
            Interlocked.Increment(ref Shared);
        }

        private static int ReadIn(in Counter counter) => counter.Value;
        private static int Look(ref int number) => number;
        private static void Fill(out int number) { number = 1; }
        private static void Overwrite(ref int number) { number = 2; }
        private static void Bump(ref int number) { number++; }
        private static void Ignore(ref int number) { }
    }
}
