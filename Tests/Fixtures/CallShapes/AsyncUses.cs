using System;
using System.Threading.Tasks;

namespace CallShapes.AsyncUses
{
    public static class Ledger
    {
        public static int Entries;

        public static void Post(string entry) { }
    }

    /// <summary>
    /// Async methods in each shape that changes what the compiler adds to them, each named
    /// after its shape, and beside them a method that is not async and waits for a task by
    /// hand. Each posts its own name to <see cref="Ledger"/>, but for <c>AwaitOnly</c>, whose
    /// code uses nothing but the task it awaits.
    /// </summary>
    public class Clerk
    {
        public async Task OneAwait()
        {
            await Task.Yield();
            Ledger.Post("OneAwait");
        }

        public async Task<int> WithResult()
        {
            await Task.Yield();
            Ledger.Post("WithResult");
            return Ledger.Entries;
        }

        public async void WithoutTask()
        {
            await Task.Yield();
            Ledger.Post("WithoutTask");
        }

        // Two awaits with two kinds of awaiter.
        public async Task TwoAwaiters()
        {
            await Task.Delay(1).ConfigureAwait(false);
            await Task.Yield();
            Ledger.Post("TwoAwaiters");
        }

        // The lambda is the async method here: its class is started by the lambda, not by
        // the method the lambda is written in.
        public Func<Task> InLambda() => async () =>
        {
            await Task.Yield();
            Ledger.Post("InLambda");
        };

        public async Task AwaitOnly(Task pending)
        {
            await pending;
        }

        public void WaitsByHand()
        {
            Task.Delay(1).GetAwaiter().GetResult();
            Ledger.Post("WaitsByHand");
        }

        // The written GetAwaiter and GetResult are on the awaiter type the await uses too.
        public async Task AwaitsAndWaitsByHand()
        {
            await Task.Delay(1);
            Task.Delay(2).GetAwaiter().GetResult();
            Ledger.Post("AwaitsAndWaitsByHand");
        }
    }
}
