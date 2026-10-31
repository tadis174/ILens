namespace BaseShift
{
    // BaseShift2 compiles this file with SHIFTED defined. Each Mover then has another base
    // class or interface, one that differs in its arity or in its type argument, or in
    // BaseShift.Rebased a class of the same name in another namespace, and nothing else in
    // this file differs between the two assemblies.

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

namespace BaseShift.Rebased
{
    // Mover's base class gives way to a class of the same name in another namespace, so its
    // declaration reads alike in both assemblies unless its types are written in full. Sealer's
    // base class gives way the same way while Sealer becomes sealed, so its declaration reads
    // otherwise, but as if its base class stayed the same, unless its types are written in full.
    namespace Alpha
    {
        public class Origin { }
    }

    namespace Beta
    {
        public class Origin { }
    }

#if SHIFTED
    public class Mover : Beta.Origin { }

    public sealed class Sealer : Beta.Origin { }
#else
    public class Mover : Alpha.Origin { }

    public class Sealer : Alpha.Origin { }
#endif
}
