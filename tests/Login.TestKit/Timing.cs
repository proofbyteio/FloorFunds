namespace Cachd.Login.TestKit;

/// <summary>Small statistics helpers for the no-enumeration timing test. The pass threshold is NOT specified anywhere (OQ-SSE2-11).</summary>
public static class Timing
{
    public static double Median(IReadOnlyList<double> xs)
    {
        if (xs.Count == 0) throw new ArgumentException("empty");
        var s = xs.OrderBy(x => x).ToArray();
        return s.Length % 2 == 1 ? s[s.Length / 2] : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2.0;
    }

    /// <summary>Mann-Whitney U normal approximation, two-sided p-value (ties get average ranks; no tie correction).</summary>
    public static double MannWhitneyP(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        var all = a.Select(x => (x, g: 0)).Concat(b.Select(x => (x, g: 1))).OrderBy(t => t.x).ToArray();
        var ranks = new double[all.Length];
        for (int i = 0; i < all.Length;)
        {
            int j = i; while (j + 1 < all.Length && all[j + 1].x == all[i].x) j++;
            for (int k = i; k <= j; k++) ranks[k] = (i + j) / 2.0 + 1;
            i = j + 1;
        }
        double r1 = 0; for (int i = 0; i < all.Length; i++) if (all[i].g == 0) r1 += ranks[i];
        double n1 = a.Count, n2 = b.Count;
        double u = r1 - n1 * (n1 + 1) / 2, mu = n1 * n2 / 2, sd = Math.Sqrt(n1 * n2 * (n1 + n2 + 1) / 12);
        if (sd == 0) return 1;
        double z = Math.Abs((u - mu) / sd);
        return 2 * (1 - Phi(z));
    }

    private static double Phi(double z) => 0.5 * (1 + Erf(z / Math.Sqrt(2)));
    private static double Erf(double x)
    {   // Abramowitz-Stegun 7.1.26
        double t = 1 / (1 + 0.3275911 * Math.Abs(x));
        double y = 1 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-x * x);
        return x >= 0 ? y : -y;
    }
}
