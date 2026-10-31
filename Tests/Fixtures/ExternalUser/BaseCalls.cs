using System;
using ExternalLib.BaseCalls;

namespace ExternalUser.BaseCalls
{
    /// <summary>
    /// Calls a method of <see cref="Hatch"/> through <c>base.</c> in a lambda that captures a
    /// parameter. The compiler moves the lambda into a class of its own and has it call a
    /// method it adds to this class, which makes the call.
    /// </summary>
    public class Trapdoor : Hatch
    {
        public Action Later(int height) => () => base.Lift(height);
    }
}
