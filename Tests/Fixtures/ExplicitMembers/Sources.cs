namespace ExplicitMembers
{
    public interface ISource
    {
        object Get();
    }

    // Declares Get again, which hides ISource.Get and implements nothing.
    public interface IRenewedSource : ISource
    {
        new object Get();
    }

    // Implements ISource.Get with a virtual method.
    public class Source : ISource
    {
        public virtual object Get() => null;
    }

    // Overrides the method that implements ISource.Get, so its Get implements it as well.
    public class OverridingSource : Source
    {
        public override object Get() => 1;
    }

    // Hides Source's Get without overriding it, so ISource.Get stays with Source's.
    public class HidingSource : Source
    {
        public new object Get() => 2;
    }

    // Lists ISource again, but its Get returns another type, so Source's Get implements
    // ISource.Get for it too.
    public class TypedSource : Source, ISource
    {
        public new string Get() => "";
    }

    // Lists ISource again with a Get that is not public, which cannot implement it.
    public class InternalSource : Source, ISource
    {
        internal new object Get() => 3;
    }

    // Overrides a Get that only hides Source's, so nothing it overrides implements ISource.Get.
    public class HidingBase : Source
    {
        public new virtual object Get() => 4;
    }

    public class OverridingHidden : HidingBase
    {
        public override object Get() => 5;
    }

    // An explicit ISource.Get beside an ordinary Get of the same parameters.
    public class SplitSource : ISource
    {
        object ISource.Get() => 6;

        public virtual object Get() => 7;
    }

    // Overrides SplitSource's ordinary Get, which the explicit one keeps from ISource.Get.
    public class OverridingSplit : SplitSource
    {
        public override object Get() => 8;
    }
}
