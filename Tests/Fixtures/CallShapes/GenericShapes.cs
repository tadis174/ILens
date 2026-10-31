using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CallShapes.Generics
{
    /// <summary>
    /// What the shapes in this file use. Each shape reads and writes the field of its own
    /// name, so an answer about one field names one shape. A shape of <c>Shapes&lt;T&gt;</c>
    /// also calls <see cref="Note"/> with that name and creates a <see cref="Token"/>; the
    /// other classes use their fields alone.
    /// </summary>
    public static class Marks
    {
        public static int Plain, Lambda, CapturingLambda, LocalFunction, LocalFunctionUsingThis,
            CapturingLocalFunction, LocalFunctionAsDelegate, LocalFunctionCalledByLambda,
            LambdaWithinLocalFunction, Iterator, IteratorLocalFunction, IteratorLambda, IteratorCapturingLambda,
            AsyncMethod, AsyncLocalFunction, AsyncLambda, FinallyBlock, GenericLocalFunction, NestedLocalFunction,
            FirstLambda, SecondLambda, ThirdLambda, NestedClassLocalFunction, StructLocalFunction,
            MethodIteratorLambda, MethodAsyncLambda, MethodLambdaUsingThis, MethodLocalFunctionCalledByLambda;

        public static void Note(string shape) { }
    }

    public class Token
    {
        public Token(string shape) { }
    }

    /// <summary>
    /// One method per shape the compiler moves code out of the method it is written in, each
    /// named after its shape, in a class with a type parameter. Inside such a class an
    /// instruction names a member of the class through a reference on the constructed type and
    /// not through the member's definition, the local function a method calls included.
    /// </summary>
    public class Shapes<T>
    {
        private int _state = 1;

        public void Plain()
        {
            Marks.Note("Plain"); Marks.Plain++; new Token("Plain");
        }

        public Action Lambda() => () =>
        {
            Marks.Note("Lambda"); Marks.Lambda++; new Token("Lambda");
        };

        public Action CapturingLambda(int offset) => () =>
        {
            Marks.Note("CapturingLambda"); Marks.CapturingLambda += offset; new Token("CapturingLambda");
        };

        public void LocalFunction()
        {
            Local();

            void Local()
            {
                Marks.Note("LocalFunction"); Marks.LocalFunction++; new Token("LocalFunction");
            }
        }

        public void LocalFunctionUsingThis()
        {
            Local();

            void Local()
            {
                Marks.Note("LocalFunctionUsingThis"); Marks.LocalFunctionUsingThis += _state;
                new Token("LocalFunctionUsingThis");
            }
        }

        public void CapturingLocalFunction(int offset)
        {
            Local();

            void Local()
            {
                Marks.Note("CapturingLocalFunction"); Marks.CapturingLocalFunction += offset;
                new Token("CapturingLocalFunction");
            }
        }

        public Action LocalFunctionAsDelegate()
        {
            return Local;

            void Local()
            {
                Marks.Note("LocalFunctionAsDelegate"); Marks.LocalFunctionAsDelegate++;
                new Token("LocalFunctionAsDelegate");
            }
        }

        public Action LocalFunctionCalledByLambda()
        {
            return () => Local();

            void Local()
            {
                Marks.Note("LocalFunctionCalledByLambda"); Marks.LocalFunctionCalledByLambda++;
                new Token("LocalFunctionCalledByLambda");
            }
        }

        public void LambdaWithinLocalFunction()
        {
            Local();

            void Local()
            {
                Action lambda = () =>
                {
                    Marks.Note("LambdaWithinLocalFunction"); Marks.LambdaWithinLocalFunction++;
                    new Token("LambdaWithinLocalFunction");
                };
                lambda();
            }
        }

        public IEnumerable<int> Iterator()
        {
            Marks.Note("Iterator"); Marks.Iterator++; new Token("Iterator");
            yield return 1;
        }

        public IEnumerable<int> IteratorLocalFunction()
        {
            Local();
            yield return 1;

            void Local()
            {
                Marks.Note("IteratorLocalFunction"); Marks.IteratorLocalFunction++;
                new Token("IteratorLocalFunction");
            }
        }

        // The classes for these lambdas are nested in Shapes<T>, beside the class of the
        // iterator or async method that takes their address, not inside it.
        public IEnumerable<int> IteratorLambda()
        {
            Action lambda = () =>
            {
                Marks.Note("IteratorLambda"); Marks.IteratorLambda++; new Token("IteratorLambda");
            };
            lambda();
            yield return 1;
        }

        public IEnumerable<int> IteratorCapturingLambda(int offset)
        {
            Action lambda = () =>
            {
                Marks.Note("IteratorCapturingLambda"); Marks.IteratorCapturingLambda += offset;
                new Token("IteratorCapturingLambda");
            };
            lambda();
            yield return 1;
        }

        public async Task AsyncLambda(int offset)
        {
            await Task.Yield();
            Action lambda = () =>
            {
                Marks.Note("AsyncLambda"); Marks.AsyncLambda += offset; new Token("AsyncLambda");
            };
            lambda();
        }

        public async Task AsyncMethod()
        {
            await Task.Yield();
            Marks.Note("AsyncMethod"); Marks.AsyncMethod++; new Token("AsyncMethod");
        }

        public async Task AsyncLocalFunction()
        {
            await Task.Yield();
            Local();

            void Local()
            {
                Marks.Note("AsyncLocalFunction"); Marks.AsyncLocalFunction++; new Token("AsyncLocalFunction");
            }
        }

        public IEnumerable<int> FinallyBlock()
        {
            try
            {
                yield return 1;
            }
            finally
            {
                Marks.Note("FinallyBlock"); Marks.FinallyBlock++; new Token("FinallyBlock");
            }
        }

        public void GenericLocalFunction<U>(U value)
        {
            Local(value);

            void Local<V>(V passed)
            {
                Marks.Note("GenericLocalFunction"); Marks.GenericLocalFunction++;
                new Token("GenericLocalFunction");
            }
        }

        public void NestedLocalFunction()
        {
            First();

            void First()
            {
                Second();

                void Second()
                {
                    Marks.Note("NestedLocalFunction"); Marks.NestedLocalFunction++;
                    new Token("NestedLocalFunction");
                }
            }
        }

        /// <summary>
        /// Has no type parameter of its own and still takes the one of the class around it.
        /// </summary>
        public class Inner
        {
            public void NestedClassLocalFunction()
            {
                Local();

                void Local()
                {
                    Marks.NestedClassLocalFunction++;
                }
            }
        }
    }

    /// <summary>
    /// Three methods with a lambda each that captures nothing. The compiler puts all three
    /// lambdas into the one class "&lt;&gt;c" nested in this class.
    /// </summary>
    public class Lambdas<T>
    {
        public Action FirstLambda() => () => Marks.FirstLambda++;

        public Action SecondLambda() => () => Marks.SecondLambda++;

        public Action ThirdLambda() => () => Marks.ThirdLambda++;
    }

    /// <summary>
    /// Methods with a type parameter in a class without one. A lambda in such a method is
    /// generic itself or lies in a generic class, so its address is taken through a reference
    /// and not through its definition.
    /// </summary>
    public class GenericMethods
    {
        private int _state = 1;

        public IEnumerable<int> MethodIteratorLambda<U>()
        {
            Action lambda = () => Marks.MethodIteratorLambda++;
            lambda();
            yield return 1;
        }

        public async Task MethodAsyncLambda<U>(int offset)
        {
            await Task.Yield();
            Action lambda = () => Marks.MethodAsyncLambda += offset;
            lambda();
        }

        // Uses nothing but this, so the lambda becomes a generic method of this class.
        public Action MethodLambdaUsingThis<U>() => () => Marks.MethodLambdaUsingThis += _state;

        public Action MethodLocalFunctionCalledByLambda<U>()
        {
            return () => Local();

            void Local()
            {
                Marks.MethodLocalFunctionCalledByLambda += _state;
            }
        }
    }

    public struct Pair<T>
    {
        public void StructLocalFunction()
        {
            Local();

            void Local()
            {
                Marks.StructLocalFunction++;
            }
        }
    }
}
