namespace BaseShift.Diffs
{
    // BaseShift2 compiles this file with SHIFTED defined, for the tests on the diff compare
    // prints of two bodies. Settle gains a condition in its middle and multiplies by another
    // number in its last statement, two changes far enough apart for two hunks; the condition
    // moves every later instruction's offset. Route's third jump lands on the other label while
    // every instruction stays the same size, so only that jump's target differs.
    public class Ledger
    {
        private int _total;
        private int _count;

        public int Settle(int[] amounts, bool strict)
        {
            int sum = 0;
            int largest = 0;
            int smallest = int.MaxValue;
            int negatives = 0;
            foreach (int amount in amounts)
            {
                sum += amount;
                if (amount > largest)
                    largest = amount;
                if (amount < smallest)
                    smallest = amount;
                if (amount < 0)
                    negatives++;
            }
            _total += sum;
            _count += amounts.Length;
            int spread = largest - smallest;
            int average = amounts.Length == 0 ? 0 : sum / amounts.Length;
#if SHIFTED
            if (strict && negatives > 0)
                return -negatives;
#endif
            int score = spread * 2 + average;
            if (score > 1000)
                score = 1000;
            if (score < -1000)
                score = -1000;
            int bonus = negatives == 0 ? 10 : 0;
            score += bonus;
            int penalty = strict ? 5 : 1;
            score -= penalty;
            int rounded = score / 10 * 10;
            int remainder = score - rounded;
            if (remainder >= 5)
                rounded += 10;
            _total -= remainder;
#if SHIFTED
            return rounded + _count * 3;
#else
            return rounded + _count * 2;
#endif
        }

        public static int Route(int value)
        {
            if (value < -100)
                goto First;
            if (value > 100)
                goto Second;
#if SHIFTED
            if (value > 0)
                goto Second;
#else
            if (value > 0)
                goto First;
#endif
            value *= 3;
        First:
            value += 1;
        Second:
            value += 2;
            return value;
        }
    }
}
