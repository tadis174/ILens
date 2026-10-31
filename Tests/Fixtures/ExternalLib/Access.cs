namespace ExternalLib.Access
{
    // Members for each way a member can stand to ExternalUser, most of them named like an extension
    // method ExternalUser's Access.cs declares: Lantern's public field, its property of a delegate
    // type, its protected method, and a static and a protected method that take the parameters of
    // the extension methods of their names, receiver included; Gizmo's public method, which the
    // versions of this library that Tests/AccessLibraryAssembly.cs writes make internal, once
    // granting ExternalUser this library's internals and once not; Shelf's private, protected and
    // internal methods and its property whose setter is private, which a class of ExternalUser
    // inherits; and the property ITallStand inherits from IStand.
    public delegate void Signal();

    public class Lantern
    {
        public int Wick;

        public Signal Flicker { get; set; }

        protected void Polish() { }

        public static bool IsLit(Lantern lantern) => false;

        protected void Snuff(Lantern other) { }
    }

    public class Gizmo
    {
        public void Tune() { }
    }

    public class Shelf
    {
        private void Dust() { }

        private void Wipe() { }

        protected void Stack() { }

        internal void Sort() { }

        public int Depth { get; private set; }
    }

    public interface IStand
    {
        int Height { get; }
    }

    public interface ITallStand : IStand
    {
    }
}
