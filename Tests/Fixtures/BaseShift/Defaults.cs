namespace BaseShift.Defaults
{
    // BaseShift2 compiles this file with SHIFTED defined. Valves' members keep their parameters and
    // change a default: the constructor's, Open's, the indexer's, Name's and Pay's take another
    // value, Gain gains one and Lose loses one, while Same keeps its own. Pace gains a member before
    // Slow, which moves Slow's number, while Pick's default and Picks' constant Usual still name
    // Slow.
    public enum Pace
    {
        Stop,
#if SHIFTED
        Crawl,
#endif
        Slow,
        Fast,
    }

    public class Valves
    {
#if SHIFTED
        public Valves(int size = 2) { }

        public void Open(int width = 6) { }

        public void Gain(int width = 1) { }

        public void Lose(int width) { }

        public void Name(string name = "after") { }

        public void Pay(decimal amount = 2.5m) { }

        public int this[int row, int column = 1] => row + column;
#else
        public Valves(int size = 1) { }

        public void Open(int width = 5) { }

        public void Gain(int width) { }

        public void Lose(int width = 1) { }

        public void Name(string name = "before") { }

        public void Pay(decimal amount = 1.5m) { }

        public int this[int row, int column = 0] => row + column;
#endif

        public void Pick(Pace pace = Pace.Slow) { }

        public void Same(int width = 7) { }
    }

    public static class Picks
    {
        public const Pace Usual = Pace.Slow;
    }
}
