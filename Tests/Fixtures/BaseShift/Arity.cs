namespace BaseShift.Arity
{
    // Classes whose name stands for a class with other type parameters in BaseShift2, in each shape
    // where the other assembly's own lookup of a name compare's list of changed types writes finds
    // a class of that name with other type parameters: Gains gains a type parameter; a static
    // class Pile joins the generic Pile<T>; Reader's nested Cursor moves into a generic Reader<T>
    // beside a static Reader without nested classes, with the class nested in Cursor; Host's
    // nested Step gains a type parameter; and Command gives way to two generic classes of its
    // name. Crate<T> gives way to Crate<T, U>, so the name Crate without brackets fits neither
    // class exactly. Box<T> gains a member and stays one class, which its name without brackets
    // finds in both, and so does System.Version<T> at the end of the file, which BaseShift2's own
    // lookup of the name passes over for the core library's Version that BaseShift2 references.
#if SHIFTED
    public class Gains<T>
    {
        public T Item;
    }

    public static class Pile
    {
        public static Pile<T> Of<T>(T top) => new Pile<T> { Top = top };
    }

    public static class Reader
    {
    }

    public class Reader<T>
    {
        public class Cursor
        {
            public bool Next() => true;

            public class Mark
            {
                public int Line;
            }
        }
    }

    public static class Host
    {
        public class Step<T>
        {
            public T Value;
        }
    }

    public class Command<T>
    {
        public T Argument;
    }

    public class Command<T, U>
    {
        public T First;
        public U Second;
    }

    public class Crate<T, U>
    {
        public T Left;
        public U Right;
    }

    public class Box<T>
    {
        public T Item;
        public int Count;
    }
#else
    public class Gains
    {
        public int Count;
    }

    public class Reader
    {
        public class Cursor
        {
            public bool Next() => false;

            public class Mark
            {
                public int Line;
            }
        }
    }

    public static class Host
    {
        public class Step
        {
            public int Value;
        }
    }

    public class Command
    {
        public void Run()
        {
        }
    }

    public class Crate<T>
    {
        public T Item;
    }

    public class Box<T>
    {
        public T Item;
    }
#endif

    public class Pile<T>
    {
        public T Top;
    }

    // In BaseShift2 alone a field names the core library's System.Version, beside the generic
    // System.Version<T> both assemblies define below.
    public class Clock
    {
        public int Ticks;
#if SHIFTED
        public System.Version Stamp;
#endif
    }
}

namespace System
{
    // A generic class named like the core library's Version, which BaseShift2 references, so that
    // BaseShift2's own lookup reads the name System.Version as that class; it gains a field there.
    public class Version<T>
    {
        public int Major;
#if SHIFTED
        public int Minor;
#endif
    }
}
