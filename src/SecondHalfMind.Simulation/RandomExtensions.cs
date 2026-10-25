namespace SecondHalfMind.Simulation;

internal static class RandomExtensions
{
    public static double NextGaussian(this Random random, double mean = 0, double stdDev = 1)
    {
        // Box-Muller transform.
        var u1 = 1.0 - random.NextDouble();
        var u2 = random.NextDouble();
        return mean + stdDev * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }

    public static int NextPoisson(this Random random, double lambda)
    {
        // Knuth's algorithm; fine for the small lambdas used in football.
        var limit = Math.Exp(-lambda);
        var k = 0;
        var p = 1.0;
        do
        {
            k++;
            p *= random.NextDouble();
        }
        while (p > limit);
        return k - 1;
    }

    public static bool Chance(this Random random, double probability) => random.NextDouble() < probability;

    public static T Pick<T>(this Random random, IReadOnlyList<T> items) => items[random.Next(items.Count)];

    public static T PickWeighted<T>(this Random random, IReadOnlyList<T> items, Func<T, double> weight)
    {
        var total = 0.0;
        foreach (var item in items)
        {
            total += weight(item);
        }

        var roll = random.NextDouble() * total;
        foreach (var item in items)
        {
            roll -= weight(item);
            if (roll <= 0)
            {
                return item;
            }
        }

        return items[^1];
    }

    public static double Clamp01(double value) => Math.Clamp(value, 0.0, 1.0);
}
