using System;
using System.Collections.Generic;

namespace InterfaceShapes.Returns
{
    // An override may return a more specific type than the member it overrides. The compiler
    // writes such an override as a new virtual member with a row that names the member it
    // overrides, so its flags do not say that it overrides, and a call on an object of its
    // class names the override, since that call returns the override's type.

    public interface IPrintable
    {
        Sheet Copy();
        object Stamp();
    }

    // Declares the members the classes below override.
    public class Sheet : IPrintable
    {
        public virtual Sheet Copy() => new Sheet();
        public virtual Sheet Next => this;
        public virtual object Stamp() => null;
        public virtual Sheet Fold() => this;
    }

    // Overrides each with a more specific return type, and Stamp with the same one, as the
    // control.
    public class Letter : Sheet
    {
        public override Letter Copy() => new Letter();
        public override Letter Next => this;
        public override object Stamp() => null;
        public override Letter Fold() => this;
    }

    // Declares none of them.
    public class Memo : Letter
    {
    }

    // Overrides Copy and Next with the return type Letter's have, so these two are overrides by
    // their flags beneath one that is not, and Fold with a more specific type a second time.
    public class Note : Memo
    {
        public override Letter Copy() => new Note();
        public sealed override Letter Next => this;
        public override Note Fold() => this;
    }

    // Overrides Note's Copy with a more specific return type and calls it through base.
    public class Slip : Note
    {
        public override Slip Copy() => (Slip)base.Copy();
    }

    // A sealed override and an abstract one, each with a more specific return type.
    public class Seal : Sheet
    {
        public sealed override Seal Copy() => this;
    }

    public abstract class Draft : Sheet
    {
        public abstract override Draft Copy();
    }

    public class Sketch : Draft
    {
        public override Sketch Copy() => this;
    }

    // The control: a Copy declared anew, which overrides nothing, and an override of that one,
    // which does not override Sheet's.
    public class Blank : Sheet
    {
        public new virtual Sheet Copy() => this;
    }

    public class Card : Blank
    {
        public override Sheet Copy() => this;
    }

    // In a class with a type parameter, overridden by a class that gives the type argument and
    // by one that passes its own type parameter on.
    public class Tray<T>
    {
        public virtual IEnumerable<T> Items() => null;
    }

    public class NumberTray : Tray<int>
    {
        public override List<int> Items() => null;
    }

    public class ListTray<T> : Tray<T>
    {
        public override List<T> Items() => null;
    }

    // A record that inherits from a record: the compiler overrides the copy method it generates
    // with the derived record as the return type.
    public record Form(int Pages);

    public record Invoice(int Lines) : Form(1);

    // Overrides a method of a class another assembly defines.
    public class Jam : Exception
    {
        public override Jam GetBaseException() => this;
    }

    // One use of each member on a receiver of each class.
    public class Clerk
    {
        public Sheet OnSheet(Sheet sheet) => sheet.Copy();
        public Letter OnLetter(Letter letter) => letter.Copy();
        public Letter OnMemo(Memo memo) => memo.Copy();
        public Letter OnNote(Note note) => note.Copy();
        public Slip OnSlip(Slip slip) => slip.Copy();
        public Seal OnSeal(Seal seal) => seal.Copy();
        public Draft OnDraft(Draft draft) => draft.Copy();
        public Sketch OnSketch(Sketch sketch) => sketch.Copy();
        public Sheet OnBlank(Blank blank) => blank.Copy();
        public Sheet OnCard(Card card) => card.Copy();
        public Sheet ThroughInterface(IPrintable printable) => printable.Copy();
        public Func<Letter> Later(Letter letter) => letter.Copy;

        public Letter InLambda(Letter letter)
        {
            Func<Letter> later = () => letter.Copy();
            return later();
        }

        public Sheet NextOfSheet(Sheet sheet) => sheet.Next;
        public Letter NextOfLetter(Letter letter) => letter.Next;
        public Letter NextOfNote(Note note) => note.Next;
        public Sheet FoldSheet(Sheet sheet) => sheet.Fold();
        public Letter FoldLetter(Letter letter) => letter.Fold();
        public Note FoldNote(Note note) => note.Fold();
        public IEnumerable<int> ItemsOfTray(Tray<int> tray) => tray.Items();
        public List<int> ItemsOfNumberTray(NumberTray tray) => tray.Items();
        public List<string> ItemsOfListTray(ListTray<string> tray) => tray.Items();
        public Jam OnJam(Jam jam) => jam.GetBaseException();
    }
}
