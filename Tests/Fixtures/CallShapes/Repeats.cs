using System;

namespace CallShapes.Repeats
{
    /// <summary>The method the callers below call once or several times.</summary>
    public class Bell
    {
        public void Ring(int times = 1) { }
    }

    /// <summary>
    /// Callers of <see cref="Bell.Ring(int)"/> that call it once, three times, and once in their
    /// own code and once in a lambda, for the tests on a caller the list of calls names once.
    /// </summary>
    public class Ringers
    {
        public void Once(Bell bell) => bell.Ring();

        public void Thrice(Bell bell)
        {
            bell.Ring(3);
            bell.Ring();
            bell.Ring(2);
        }

        public Action WithLambda(Bell bell)
        {
            bell.Ring(5);
            return () => bell.Ring(4);
        }
    }
}
