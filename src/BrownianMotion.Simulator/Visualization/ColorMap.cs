using Raylib_cs;

namespace BrownianMotion.Simulator.Visualization;

/// <summary>
/// Перетворює щільність частинок на колір теплової карти, що сприймається як однорідний.
/// Використовує градієнт синій -> блакитний -> зелений -> жовтий -> червоний (науковий стандарт).
/// </summary>
public static class ColorMap
{
    /// <summary>
    /// Повертає колір Raylib для заданої кількості частинок, масштабований до maxDensity.
    /// 0 частинок = темний фон, maxDensity = насичений червоний.
    /// </summary>
    public static Color DensityToColor(int count, int maxDensity)
    {
        if (count <= 0) return new Color((byte)15, (byte)15, (byte)25, (byte)255); // майже чорний фон

        float t = Math.Clamp((float)count / Math.Max(maxDensity, 1), 0f, 1f);

        // 5-ти ступеневий градієнт: чорний -> синій -> блакитний -> зелений -> жовтий -> червоний
        return t switch
        {
            < 0.2f => Lerp(new Color((byte)15, (byte)15, (byte)80, (byte)255), new Color((byte)0, (byte)80, (byte)255, (byte)255), t / 0.2f),
            < 0.4f => Lerp(new Color((byte)0, (byte)80, (byte)255, (byte)255), new Color((byte)0, (byte)220, (byte)220, (byte)255), (t - 0.2f) / 0.2f),
            < 0.6f => Lerp(new Color((byte)0, (byte)220, (byte)220, (byte)255), new Color((byte)0, (byte)255, (byte)60, (byte)255), (t - 0.4f) / 0.2f),
            < 0.8f => Lerp(new Color((byte)0, (byte)255, (byte)60, (byte)255), new Color((byte)255, (byte)255, (byte)0, (byte)255), (t - 0.6f) / 0.2f),
            _ => Lerp(new Color((byte)255, (byte)255, (byte)0, (byte)255), new Color((byte)255, (byte)30, (byte)0, (byte)255), (t - 0.8f) / 0.2f),
        };
    }

    private static Color Lerp(Color a, Color b, float t) => new(
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t),
        (byte)255);

    public static Color ModeColor(Core.SimulationMode mode) => mode switch
    {
        Core.SimulationMode.Unsafe => new Color((byte)220, (byte)50, (byte)50, (byte)255),
        Core.SimulationMode.LockBased => new Color((byte)255, (byte)165, (byte)0, (byte)255),
        Core.SimulationMode.HighPerformance => new Color((byte)50, (byte)200, (byte)100, (byte)255),
        _ => Color.White
    };

    public static string ModeLabel(Core.SimulationMode mode) => mode switch
    {
        Core.SimulationMode.Unsafe => "UNSAFE (Race Conditions)",
        Core.SimulationMode.LockBased => "LOCK-BASED (Resource Ordering)",
        Core.SimulationMode.HighPerformance => "HIGH-PERF (Interlocked Atomic)",
        _ => "UNKNOWN"
    };
}
