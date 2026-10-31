using System;

namespace CallShapes.Events
{
    /// <summary>
    /// Events declared the short way, whose subscribers the compiler keeps in a private field of
    /// the event's name, for the tests on the fields the compiler adds: one of a generic class,
    /// whose field the code names with the class's type parameter, and a static one, which one
    /// method raises and another clears.
    /// </summary>
    public class Feed<T>
    {
        public event Action<T> Posted;

        public void Post(T item) => Posted?.Invoke(item);
    }

    public static class Bulletin
    {
        public static event EventHandler Announced;

        public static void Announce() => Announced?.Invoke(null, EventArgs.Empty);

        public static void Silence() => Announced = null;
    }
}
