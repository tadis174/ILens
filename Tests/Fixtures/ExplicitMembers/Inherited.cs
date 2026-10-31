using System;

namespace ExplicitMembers.Inherited
{
    public interface IPart
    {
        object Get();
        string Name { get; }
        event EventHandler Changed;
    }

    // Has no interface. A class below that lists IPart and declares nothing binds IPart's
    // members to these.
    public class PlainBase
    {
        public object Get() => null;
        public virtual string Name => "PlainBase";
        public virtual event EventHandler Changed { add { } remove { } }
    }

    // Lists IPart and declares none of its members, so each is bound to PlainBase's.
    public class Lister : PlainBase, IPart
    {
    }

    // Has IPart through Lister and overrides the property IPart.Name is bound to, so its Name
    // runs for it.
    public class ListerChild : Lister
    {
        public override string Name => "ListerChild";
    }

    // Overrides between the base class and a class that lists IPart, which finds this override
    // first on its way up.
    public class Middle : PlainBase
    {
        public override string Name => "Middle";
    }

    public class LateLister : Middle, IPart
    {
    }

    // Overrides the same property and never has the interface.
    public class Stranger : PlainBase
    {
        public override string Name => "Stranger";
    }

    // Lists IPart and declares its members.
    public class Holder : IPart
    {
        public object Get() => 1;
        public virtual string Name => "Holder";
        public event EventHandler Changed { add { } remove { } }
    }

    // Hides Holder's Get. For a Hider, IPart.Get stays with Holder's.
    public class Hider : Holder
    {
        public new object Get() => 2;
    }

    // Lists IPart again and declares nothing. C# binds IPart.Get to the Get found first on the
    // way up, Hider's, where the class above bound it to Holder's. For a binding that differs
    // from the inherited one, the compiler adds a method to this class that implements
    // IPart.Get by its row and passes the call on to Hider's.
    public class Relister : Hider, IPart
    {
    }

    // Lists IPart again and implements Name explicitly, which takes IPart.Name from Holder's
    // property for this class and every class below it.
    public class Renamer : Holder, IPart
    {
        string IPart.Name => "Renamer";
    }

    // Overrides Holder's Name, which IPart.Name is no longer bound to here.
    public class BelowRenamer : Renamer
    {
        public override string Name => "BelowRenamer";
    }

    public interface ITag
    {
        int Tag();
    }

    // Five classes list ITag and inherit Tag from a class without it.
    public class TagBase
    {
        public int Tag() => 0;
    }

    public class TagA : TagBase, ITag { }
    public class TagB : TagBase, ITag { }
    public class TagC : TagBase, ITag { }
    public class TagD : TagBase, ITag { }
    public class TagE : TagBase, ITag { }
}
