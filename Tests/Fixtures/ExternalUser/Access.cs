using ExternalLib.Access;

namespace ExternalUser.Access
{
    // Extension methods named like a member of a type of ExternalLib's Access.cs, for the tests on
    // which of the two a lookup through that type takes. C# binds a call to the extension method
    // where the member is a field, a property of a type that is no delegate, a static method
    // called on a value, a protected method called outside a class derived from its own, or a
    // private or internal method of a base class; it binds a call to a public method, and invokes
    // a property of a delegate type. IsLit and Snuff take the parameters of Lantern's static and
    // protected method of their names, so a hint fits both.
    public static class AccessExtensions
    {
        public static int Wick(this Lantern lantern) => 0;

        public static void Flicker(this Lantern lantern) { }

        public static void Polish(this Lantern lantern) { }

        public static bool IsLit(this Lantern lantern) => true;

        public static void Snuff(this Lantern lantern) { }

        public static void Tune(this Gizmo gizmo) { }

        public static int Height(this ITallStand stand) => 0;
    }

    // Inherits Shelf's methods and its property Depth. ShelfExtensions has an extension method named
    // after each of Shelf's methods but Wipe and Depth's accessors.
    public class OwnShelf : Shelf
    {
        // Inside a class derived from Shelf, the protected method binds.
        public void Fill() => Stack();
    }

    public static class ShelfExtensions
    {
        public static void Dust(this OwnShelf shelf) { }

        public static void Stack(this OwnShelf shelf) { }

        public static void Sort(this OwnShelf shelf) { }
    }

    // A caller for each name: Trim, Shine, Look, Smother, Measure and Tidy call the extension
    // methods, Adjust calls Gizmo's method, Check calls Lantern's static method by Lantern's name,
    // Flash invokes the property of a delegate type, and Ring calls the extension method of that
    // name in its static form. Read reads the field and the property the base interface declares.
    public class Keeper
    {
        public int Trim(Lantern lantern) => lantern.Wick();

        public void Flash(Lantern lantern) => lantern.Flicker();

        public void Ring(Lantern lantern) => AccessExtensions.Flicker(lantern);

        public void Shine(Lantern lantern) => lantern.Polish();

        public bool Check(Lantern lantern) => Lantern.IsLit(lantern);

        public bool Look(Lantern lantern) => lantern.IsLit();

        public void Smother(Lantern lantern) => lantern.Snuff();

        public void Adjust(Gizmo gizmo) => gizmo.Tune();

        public int Measure(ITallStand stand) => stand.Height();

        public int Read(Lantern lantern, ITallStand stand) => lantern.Wick + stand.Height;

        public void Tidy(OwnShelf shelf)
        {
            shelf.Dust();
            shelf.Sort();
        }
    }

    // Inside a class derived from Lantern, the protected methods bind.
    public class Lamplighter : Lantern
    {
        public void Clean() => Polish();

        public void Douse(Lantern other) => Snuff(other);
    }
}
