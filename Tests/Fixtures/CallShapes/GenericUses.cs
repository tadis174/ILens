using System;
using System.Collections.Generic;

namespace CallShapes.GenericUses
{
    /// <summary>
    /// A generic class the others use: through a type parameter of their own, which has
    /// another name than this class's, and with a concrete type.
    /// </summary>
    public class Crate<TItem>
    {
        public TItem Content;

        public static TItem Make() => default;

        public void Fill(TItem item, List<TItem> more) { }
    }

    /// <summary>
    /// Uses its own field and method, another generic class and a dictionary, each through its
    /// own type parameter.
    /// </summary>
    public class Shelf<T>
    {
        private readonly Crate<T> _crate = new Crate<T>();
        private readonly Dictionary<T, List<T>> _index = new Dictionary<T, List<T>>();

        public int Count => _index.Count;

        public void Stock(T item)
        {
            _crate.Fill(item, null);
            _index[item] = new List<T>();
            Label(item);
        }

        public void Label(T item) { }

        // A generic method of a generic class. The iterator's class has the class's type
        // parameter and the method's, so it numbers the method's after the class's, where the
        // method itself numbers its own from zero.
        public IEnumerable<T> Each<U>(U marker)
        {
            Label(_crate.Content);
            yield return _crate.Content;
        }
    }

    public class Packer
    {
        // The body names Crate<U> through the method's type parameter, the lambda through the
        // type parameter of the class the compiler puts it in.
        public Func<U> Twice<U>()
        {
            Crate<U>.Make();
            return () => Crate<U>.Make();
        }

        public int Concrete()
        {
            var crate = new Crate<int>();
            crate.Fill(1, new List<int>());
            return Crate<int>.Make();
        }
    }

    /// <summary>A property of a generic class whose getter calls its own setter.</summary>
    public class Cached<T>
    {
        private T _value;
        private bool _made;

        public T Value
        {
            get
            {
                if (!_made)
                    Value = default;
                return _value;
            }
            set
            {
                _value = value;
                _made = true;
            }
        }
    }
}
