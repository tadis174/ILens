using System;
using ExternalLib;

// An attribute class of this assembly and one of ExternalLib, each applied through two of its
// constructors, one of which code also calls. Applying an attribute uses its constructor
// without any call, so only the attribute itself shows which constructor a place uses.
namespace ExternalUser.Applied
{
    public sealed class StampAttribute : Attribute
    {
        public StampAttribute() { }
        public StampAttribute(int rank) { }

        // Neither applied nor called.
        public StampAttribute(string name) { }
    }

    [Stamp]
    public class Plain { }

    // The constructor with a rank on each thing an attribute can sit on.
    [Stamp(1)]
    public class Ranked
    {
        [Stamp(2)]
        public int Field;

        [Stamp(3)]
        public int Property { get; set; }

        public int Accessor { [Stamp(4)] get; set; }

        public void Parameter([Stamp(5)] int value) { }

        [return: Stamp(6)]
        public int Return() => 0;
    }

    public class InCode
    {
        // Calls the constructor and carries no attribute.
        public Attribute Make() => new StampAttribute(7);

        // Carries the attribute and calls its constructor.
        [Stamp(8)]
        public Attribute Both() => new StampAttribute(9);

        // The attribute sits on the method the compiler generates for the lambda, and on the
        // one it generates for the local function.
        public Func<int> OnLambda() => [Stamp(10)] () => 0;

        public int OnLocalFunction()
        {
            return Local();

            [Stamp(11)]
            int Local() => 0;
        }
    }

    [Seal]
    public class Sealed
    {
        [Seal(1)]
        public void Method() { }

        public Attribute Make() => new SealAttribute(2);
    }

#nullable enable
    // The compiler marks the row that says Keyed implements the interface with an attribute of
    // a class it adds to this assembly, since the interface's type argument may be null. That
    // row belongs to no type or member, so nothing carries the attribute.
    public class Keyed : IEquatable<string?>
    {
        public bool Equals(string? other) => false;
    }
#nullable restore
}
