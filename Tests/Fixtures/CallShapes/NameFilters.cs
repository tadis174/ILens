namespace CallShapes.NameFilters
{
    /// <summary>
    /// A class beside generic classes of its name, which differ in their count of type
    /// parameters, and beside a generic class whose name continues past it.
    /// </summary>
    public class Hive { }

    public class Hive<T> { }

    public class Hive<T, U> { }

    public class HiveFrame<T> { }

    /// <summary>
    /// A generic class with a constructor, a method beside generic methods of its name and a
    /// generic method whose name continues past it, and a nested class beside a generic nested
    /// class of its name, for the tests on how the name filters read brackets.
    /// </summary>
    public class Kiln<T>
    {
        public Kiln(T stock) { }

        public void Fire() { }

        public void Fire<U>(U item) { }

        public void Fire<U, V>(U item, V other) { }

        public void FireAgain<U>(U item) { }

        public class Batch { }

        public class Batch<U> { }
    }
}
