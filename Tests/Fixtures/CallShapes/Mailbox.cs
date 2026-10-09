using System;
using System.Collections.Generic;

namespace CallShapes
{
    /// <summary>The call-site search target: two overloads that share an optional parameter.</summary>
    public class Mailbox
    {
        public void Send(string label, string text = null, int delay = 0, bool sound = true) { }

        public void Send(Parcel parcel, int delay = 0) { }

        public void Tag(string label, params string[] tags) { }

        public static Mailbox operator +(Mailbox box, Parcel parcel) => box;
    }

    public class Parcel
    {
        public Parcel(string label, int weight = 1) { }
    }

    /// <summary>An extension method, whose first argument is the receiver of a call in its extension form.</summary>
    public static class Stamps
    {
        public static void Stamp(this Mailbox box, string mark, int copies = 1) { }
    }

    /// <summary>
    /// One call to <see cref="Mailbox.Send(string, string, int, bool)"/> or its overload per way
    /// an argument can reach its parameter: omitted, passed as its default, named, reordered,
    /// as a variable, through the other overload, and from inside an iterator and a lambda.
    /// </summary>
    public class Senders
    {
        public void Omitted(Mailbox box) => box.Send("omitted");

        public void ExplicitDefault(Mailbox box) => box.Send("explicit", null, 0);

        public void Named(Mailbox box) => box.Send("named", delay: 600);

        public void Reordered(Mailbox box) => box.Send(delay: 60, label: "reordered");

        public void Variable(Mailbox box, int ticks) => box.Send("variable", null, ticks);

        public void OtherOverload(Mailbox box) => box.Send(new Parcel("parcel"), 6);

        public IEnumerable<int> InIterator(Mailbox box)
        {
            box.Send("iterator", delay: 900);
            yield return 0;
        }

        public Action InLambda(Mailbox box) => () => box.Send("lambda");

        // A params parameter receives an empty expansion here and two values below.
        public void TagNone(Mailbox box) => box.Tag("none");

        public void TagTwo(Mailbox box) => box.Tag("two", "x", "y");

        // The decompiler shows this call to op_Addition as the + operator, not as a call.
        public Mailbox AsOperator(Mailbox box, Parcel parcel) => box + parcel;

        // Stamp called in its extension form, once with its optional argument left out, and in the
        // static form the compiler gives every call to it.
        public void Stamped(Mailbox box) => box.Stamp("stamped", 2);

        public void StampedOnce(Mailbox box) => box.Stamp("once");

        public void StampedStatically(Mailbox box) => Stamps.Stamp(box, "static", 3);
    }
}
