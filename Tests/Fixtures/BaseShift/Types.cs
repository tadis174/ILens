namespace BaseShift
{
    // BaseShift2 compiles this file with SHIFTED defined. Each Mover then has another base
    // class or interface, one that differs in its arity or in its type argument, and
    // nothing else in this file differs between the two assemblies.

    public class Owner { }

    public class Owner<T> { }

    public interface IOwner { }

    public interface IOwner<T> { }

#if SHIFTED
    public class BaseArityMover : Owner<int> { }

    public class BaseArgumentMover : Owner<string> { }

    public class InterfaceArityMover : IOwner<int> { }

    public class InterfaceArgumentMover : IOwner<string> { }
#else
    public class BaseArityMover : Owner { }

    public class BaseArgumentMover : Owner<int> { }

    public class InterfaceArityMover : IOwner { }

    public class InterfaceArgumentMover : IOwner<int> { }
#endif

    // The same in both assemblies, with generic bases like the Movers', so a comparison
    // must not report it.
    public class Unmoved : Owner<int>, IOwner<int> { }
}
