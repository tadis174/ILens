namespace BaseShift.Variance
{
    // Interfaces and delegates whose type parameter gains, loses or swaps its in or out between
    // this assembly and BaseShift2, and nothing else: ISource and Factory gain out, IProducer
    // loses out, Handler loses in, and ISwitch's in becomes out. compare lists these five with the
    // kind metadata and none of the types below them, which keep theirs: an interface with in, out
    // and an invariant type parameter beside a where clause, a delegate with in and out, and an
    // interface nested in a variant interface, whose heading carries its own in alone.
#if SHIFTED
    public interface ISource<out T>
    {
        T Get();
    }

    public interface IProducer<T>
    {
        T Make();
    }

    public interface ISwitch<out T>
    {
    }

    public delegate void Handler<T>(T item);

    public delegate T Factory<out T>();
#else
    public interface ISource<T>
    {
        T Get();
    }

    public interface IProducer<out T>
    {
        T Make();
    }

    public interface ISwitch<in T>
    {
    }

    public delegate void Handler<in T>(T item);

    public delegate T Factory<T>();
#endif

    public interface IMapper<in TIn, TKeep, out TOut> where TKeep : class
    {
        TOut Map(TIn item, TKeep keep);
    }

    public delegate TOut Mapping<in TIn, out TOut>(TIn item);

    public interface IOuter<out T>
    {
        public interface IInner<in U>
        {
            void Take(U item);
        }
    }
}
