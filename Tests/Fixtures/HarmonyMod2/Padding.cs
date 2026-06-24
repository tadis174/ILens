namespace HarmonyMod
{
    // Bulk filler. Pushes everything declared after it in the linked
    // Patches.cs into different file positions, so HarmonyMod2.dll's
    // methods land at different RVAs than HarmonyMod.dll's even when
    // the source they share is byte-identical.
    internal static class Padding
    {
        public static int M00() => 0;
        public static int M01() => 1;
        public static int M02() => 2;
        public static int M03() => 3;
        public static int M04() => 4;
        public static int M05() => 5;
        public static int M06() => 6;
        public static int M07() => 7;
        public static int M08() => 8;
        public static int M09() => 9;
        public static int M10() => 10;
        public static int M11() => 11;
        public static int M12() => 12;
        public static int M13() => 13;
        public static int M14() => 14;
        public static int M15() => 15;
    }
}
