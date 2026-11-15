using System;
using System.Collections;

namespace CallShapes.LongCalls
{
    /// <summary>What a call below passes, filled by an object initializer.</summary>
    public class Settings
    {
        public string Title;
        public string Subtitle;
        public int Width;
        public int Height;
        public bool Visible;
        public string Footer;
    }

    /// <summary>A class an initializer fills with a label and a lambda.</summary>
    public class Holder
    {
        public string Label;
        public Action Work;
    }

    /// <summary>A collection a collection initializer fills with lambdas, through its Add method.</summary>
    public class Jobs : IEnumerable
    {
        public void Add(Action job) { }

        public IEnumerator GetEnumerator() => null;
    }

    /// <summary>
    /// A delegate with parameters passed by reference, for which the decompiler writes an
    /// anonymous method with the keyword delegate rather than a lambda.
    /// </summary>
    public delegate bool Reader(ref int position, out string text);

    /// <summary>The methods the callers below call.</summary>
    public static class Target
    {
        public static void Run(string label, Action action, bool flag) { }

        public static void Read(string label, Reader reader, bool flag) { }

        public static void Fill(Settings settings, int count) { }

        public static void Hold(Holder holder) { }

        public static void Words(params string[] words) { }

        public static void Say(string text) { }

        public static void Make(Func<object> make, bool flag) { }

        public static void Log(Action action, string text) { }

        public static void Note(string text) { }
    }

    /// <summary>
    /// Calls whose code runs past the length at which call_sites shortens a line, each long for
    /// one reason: a lambda's body, an anonymous method's body, an initializer's entries, a lambda
    /// inside an initializer, a lambda that makes an anonymous object, a lambda a collection
    /// initializer adds, many short values, one long text whose cut lands right after a dot, and
    /// a short lambda before a text too long for leaving it out to help; beside a short call that
    /// stays whole, which comes first among the callers by its name, and two calls of exactly the
    /// length and of one character more.
    /// </summary>
    public class Callers
    {
        public void Brief() => Target.Run("brief", () => Console.WriteLine("brief"), true);

        public void LongLambda() => Target.Run("lambda", () =>
        {
            int total = 0;
            for (int i = 0; i < 10; i++)
                total += i * i;
            Console.WriteLine("The sum of the squares of the numbers below ten is " + total + ".");
            Console.WriteLine("This line only makes the body of the lambda long enough to be shortened.");
            Console.WriteLine("And this one makes sure of it, whatever the decompiler writes.");
        }, true);

        public void LongDelegate() => Target.Read("delegate", delegate (ref int position, out string text)
        {
            text = "An anonymous method keeps its parameters when its body is left out.";
            position += text.Length;
            Console.WriteLine("Its body is long enough to be left out when the line is shortened.");
            Console.WriteLine("And this line adds to it, whatever the decompiler writes.");
            return position > 0;
        }, false);

        public void LongInitializer() => Target.Fill(new Settings
        {
            Title = "A title long enough to count toward the length of the line",
            Subtitle = "A subtitle that does the same for the entries of the initializer",
            Width = 640,
            Height = 480,
            Visible = true,
            Footer = "A footer that makes sure the line runs past three hundred characters",
        }, 3);

        public void LambdaInInitializer() => Target.Hold(new Holder
        {
            Label = "held",
            Work = () =>
            {
                Console.WriteLine("The lambda in this initializer is left out before the initializer itself.");
                Console.WriteLine("So the line keeps the label and shows that the work is a lambda.");
                Console.WriteLine("This line only adds to the length of the body.");
            },
        });

        public void ManyWords() => Target.Words(
            "alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india",
            "juliett", "kilo", "lima", "mike", "november", "oscar", "papa", "quebec", "romeo",
            "sierra", "tango", "uniform", "victor", "whiskey", "xray", "yankee", "zulu",
            "alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india");

        // The cut, 297 characters into the call's line, falls right after the dot that ends the
        // fourth sentence.
        public void Sentence() => Target.Say(
            "A call that passes one long text is cut where its line reaches the length. " +
            "The text of this call is written so that the cut lands right after a dot. " +
            "The line then ends in the three dots of the marker and in no fourth one. " +
            "What follows the dot is left out, as the rest of a cut line is. " +
            "This last sentence only makes the text long enough.");

        public void AnonymousObject() => Target.Make(() => new
        {
            Title = "An anonymous object made by a lambda, whose body is left out as a whole",
            Subtitle = "so that the line shows the lambda and what follows it, not the object",
            Footer = "and this entry only makes the line run past three hundred characters",
            Note = "as does this one, whatever the decompiler writes",
        }, true);

        public Jobs JobList() => new Jobs
        {
            () =>
            {
                Console.WriteLine("A lambda that a collection initializer adds is an entry of its own.");
                Console.WriteLine("Its line is the entry in braces, and the lambda's body is left out.");
                Console.WriteLine("This line only adds to the length of the body.");
                Console.WriteLine("And so does this one, whatever the decompiler writes.");
            },
        };

        // Leaving the lambda out cannot make this line fit, so the line is cut as it stands.
        public void ShortLambdaLongText() => Target.Log(() => Console.WriteLine("short"),
            "A text so long that leaving the short lambda before it out could not make the line fit, " +
            "so the line is cut as it stands and keeps the lambda whole, where leaving it out would " +
            "lose its body for no more than a few characters of the text at the end of the line, " +
            "which this sentence only makes long enough");

        // The call's line is exactly 300 characters long.
        public void AtTheLength() => Target.Note(
            "This text makes the line of its call exactly as long as the length at which a line of " +
            "call_sites is shortened, so that the line stays whole, while the same text with one more " +
            "letter makes it one character longer, so that the line is cut and the answer counts it " +
            "as shortened every time");

        // The call's line is 301 characters long.
        public void PastTheLength() => Target.Note(
            "This text makes the line of its call exactly as long as the length at which a line of " +
            "call_sites is shortened, so that the line stays whole, while the same text with one more " +
            "letter makes it one character longer, so that the line is cut and the answer counts it " +
            "as shortened at any time");
    }
}
