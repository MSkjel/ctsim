namespace CicodeSim.Values
{
    /// <summary>
    /// Max / min of two doubles that keeps the first unless the second is strictly greater /
    /// smaller.
    /// </summary>
    public static class FloatOrder
    {
        public static double Max(double a, double b) => b > a ? b : a;

        public static double Min(double a, double b) => b < a ? b : a;
    }
}
