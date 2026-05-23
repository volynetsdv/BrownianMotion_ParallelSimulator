namespace BrownianMotion.Simulator.Core;

/// <summary>
/// Центральна конфігурація для моделювання. Тут містяться всі параметри, які можна налаштовувати.
/// </summary>
public sealed record SimulationConfig
{
    // розмір сітки
    public int GridWidth { get; init; } = 120;
    public int GridHeight { get; init; } = 80;

    // кількість частинок
    public int ParticleCount { get; init; } = 10_000;

    // Початкове значення для відтворюваності генератора випадкових чисел (null = random seed)
    public int? Seed { get; init; } = 42;

    // Імовірності переміщення (must sum to <= 1.0; remainder = stay)
    public double ProbUp { get; init; } = 0.25;
    public double ProbDown { get; init; } = 0.25;
    public double ProbLeft { get; init; } = 0.25;
    public double ProbRight { get; init; } = 0.25;

    // Паралельність
    public SimulationMode Mode { get; init; } = SimulationMode.HighPerformance;
    public int WorkerCount { get; init; } = Environment.ProcessorCount;
    public int ChunkSize { get; init; } = 256; // частинок на робочий блок

    // Інтервал знімків у тіках симуляції
    public int SnapshotIntervalTicks { get; init; } = 1;

    // Максимальна кількість тіків симуляції перед автоматичуприпиненням (0 = нескінченно)
    public long MaxTicks { get; init; } = 0;

    // Benchmark: скільки тактів потрібно виконати для порівняння продуктивності
    public int BenchmarkTicks { get; init; } = 500;

    // Візуалізація
    public int TargetFps { get; init; } = 60;
    public int CellPixelSize { get; init; } = 8;
    public bool EnableVisualization { get; init; } = true;

    // Logging
    public int ConsoleLogIntervalTicks { get; init; } = 100;
}

public enum SimulationMode
{
    /// <summary>Без синхронізації. Демонстрації гонитви.</summary>
    Unsafe,

    /// <summary>Блокування на рівні комірок з упорядкуванням ресурсів для запобігання дедлокам.</summary>
    LockBased,

    /// <summary>Атомарні операції Interlocked - висока продуктивність, без блокування.</summary>
    HighPerformance
}
