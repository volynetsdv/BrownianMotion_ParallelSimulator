using System.Diagnostics;
using BrownianMotion.Simulator.Core;

namespace BrownianMotion.Simulator.Benchmarks;

/// <summary>
/// Тест продуктивності, що порівнює три стратегії паралелізму.
/// 
/// Тести:
///   A. 1 потік на частинку (наївний підхід) - ілюструє, чому це не масштабується
///   B. Пул робочих процесів із фрагментацією -рекомендований підхід
///   C. Parallel.ForEach - керований паралельний цикл .NET
/// 
/// Виводить відформатовану таблицю порівняння з показниками пропускної здатності.
/// </summary>
public static class PerformanceBenchmark
{
    private record BenchmarkResult(
        string Name,
        int Workers,
        int ParticleCount,
        int Ticks,
        double ElapsedMs,
        long ParticleMoves,
        double TicksPerSecond,
        double MovesPerSecond,
        bool MassConserved,
        long FinalSum);

    public static void Run(SimulationConfig baseConfig)
    {
        Console.WriteLine("\n" + new string('═', 80));
        Console.WriteLine("  BROWNIAN MOTION PERFORMANCE BENCHMARK");
        Console.WriteLine(new string('═', 80));
        Console.WriteLine($"  Grid: {baseConfig.GridWidth}×{baseConfig.GridHeight} | Particles: {baseConfig.ParticleCount:N0} | Ticks: {baseConfig.BenchmarkTicks}");
        Console.WriteLine($"  CPU cores: {Environment.ProcessorCount}");
        Console.WriteLine(new string('─', 80) + "\n");

        var results = new List<BenchmarkResult>();

         // Тест A: Пул робочих процесів (HighPerf, змінна кількість робочих процесів) 
        foreach (int workers in new[] { 1, 2, Environment.ProcessorCount / 2, Environment.ProcessorCount })
        {
            if (workers < 1) continue;
            var config = baseConfig with { WorkerCount = workers, EnableVisualization = false };
            var result = RunWorkerPoolBenchmark(config, $"WorkerPool ({workers}w)", workers);
            results.Add(result);
            PrintResult(result);
        }

         // Тест B: Parallel.ForEach (доручаємо .NET управління розподілом даних) 
        {
            var result = RunParallelForEachBenchmark(baseConfig);
            results.Add(result);
            PrintResult(result);
        }

         // Тест C: 1 потік на частинку (можливо лише для малих значень K)
        int safeParticleCount = Math.Min(baseConfig.ParticleCount, 1000);
        {
            var result = RunOneThreadPerParticleBenchmark(baseConfig, safeParticleCount);
            results.Add(result);
            PrintResult(result);
        }

        PrintSummaryTable(results, baseConfig);
    }

    private static BenchmarkResult RunWorkerPoolBenchmark(SimulationConfig config, string name, int workers)
    {
        var grid = new Grid(config.GridWidth, config.GridHeight);
        var particles = InitParticles(config, grid);

        var cts = new CancellationTokenSource();
        int ticksDone = 0;
        long totalMoves = 0;

        var barrier = new Barrier(workers, _ =>
        {
            Interlocked.Increment(ref ticksDone);
        });

        var sw = Stopwatch.StartNew();
        int chunkSize = (int)Math.Ceiling((double)config.ParticleCount / workers);

        var tasks = Enumerable.Range(0, workers).Select(w =>
        {
            int start = w * chunkSize;
            int end = Math.Min(start + chunkSize, config.ParticleCount);
            return Task.Run(() =>
            {
                long localMoves = 0;
                while (Volatile.Read(ref ticksDone) < config.BenchmarkTicks)
                {
                    for (int i = start; i < end; i++)
                    {
                        ref Particle p = ref particles[i];
                        var dir = RngFactory.NextDirection(config.ProbUp, config.ProbDown, config.ProbLeft, config.ProbRight);
                        ApplyMove(ref p, dir, config, grid);
                        if (dir != Direction.Stay) localMoves++;
                    }
                    try { barrier.SignalAndWait(); }
                    catch { break; }
                }
                Interlocked.Add(ref totalMoves, localMoves);
            });
        }).ToArray();

        Task.WhenAll(tasks).Wait();
        sw.Stop();

        var (sum, valid) = grid.ValidateParticleCount(config.ParticleCount);
        return new BenchmarkResult(name, workers, config.ParticleCount, config.BenchmarkTicks,
            sw.Elapsed.TotalMilliseconds, totalMoves,
            config.BenchmarkTicks / (sw.Elapsed.TotalSeconds + 1e-9),
            totalMoves / (sw.Elapsed.TotalSeconds + 1e-9), valid, sum);
    }

    private static BenchmarkResult RunParallelForEachBenchmark(SimulationConfig config)
    {
        var grid = new Grid(config.GridWidth, config.GridHeight);
        var particles = InitParticles(config, grid);
        long totalMoves = 0;

        var sw = Stopwatch.StartNew();
        for (int tick = 0; tick < config.BenchmarkTicks; tick++)
        {
            long tickMoves = 0;
            Parallel.For(0, config.ParticleCount, i =>
            {
                ref Particle p = ref particles[i];
                var dir = RngFactory.NextDirection(config.ProbUp, config.ProbDown, config.ProbLeft, config.ProbRight);
                ApplyMove(ref p, dir, config, grid);
                if (dir != Direction.Stay) Interlocked.Increment(ref tickMoves);
            });
            totalMoves += tickMoves;
        }
        sw.Stop();

        var (sum, valid) = grid.ValidateParticleCount(config.ParticleCount);
        return new BenchmarkResult("Parallel.For", Environment.ProcessorCount, config.ParticleCount,
            config.BenchmarkTicks, sw.Elapsed.TotalMilliseconds, totalMoves,
            config.BenchmarkTicks / sw.Elapsed.TotalSeconds,
            totalMoves / sw.Elapsed.TotalSeconds, valid, sum);
    }

    private static BenchmarkResult RunOneThreadPerParticleBenchmark(SimulationConfig config, int particleCount)
    {
        var grid = new Grid(config.GridWidth, config.GridHeight);
        var limitedConfig = config with { ParticleCount = particleCount };
        var particles = InitParticles(limitedConfig, grid);

        long totalMoves = 0;
        int ticks = Math.Min(config.BenchmarkTicks, 50); // cap: too slow otherwise

        var sw = Stopwatch.StartNew();
        for (int tick = 0; tick < ticks; tick++)
        {
            var threads = new Thread[particleCount];
            long tickMoves = 0;

            for (int i = 0; i < particleCount; i++)
            {
                int idx = i;
                threads[i] = new Thread(() =>
                {
                    ref Particle p = ref particles[idx];
                    var dir = RngFactory.NextDirection(config.ProbUp, config.ProbDown, config.ProbLeft, config.ProbRight);
                    ApplyMove(ref p, dir, limitedConfig, grid);
                    if (dir != Direction.Stay) Interlocked.Increment(ref tickMoves);
                });
                threads[i].IsBackground = true;
                threads[i].Start();
            }

            foreach (var t in threads) t.Join();
            totalMoves += tickMoves;
        }
        sw.Stop();

        var (sum, valid) = grid.ValidateParticleCount(particleCount);
        string name = $"1T/Particle ({particleCount}p, {ticks} ticks)";
        return new BenchmarkResult(name, particleCount, particleCount, ticks,
            sw.Elapsed.TotalMilliseconds, totalMoves,
            ticks / sw.Elapsed.TotalSeconds,
            totalMoves / sw.Elapsed.TotalSeconds, valid, sum);
    }

    private static Particle[] InitParticles(SimulationConfig config, Grid grid)
    {
        RngFactory.Initialize(config.Seed);
        var rng = new Random(config.Seed ?? 0);
        var particles = new Particle[config.ParticleCount];
        for (int i = 0; i < config.ParticleCount; i++)
        {
            int x = rng.Next(config.GridWidth);
            int y = rng.Next(config.GridHeight);
            particles[i] = new Particle(i, x, y);
            grid.Set(x, y, grid.Get(x, y) + 1);
        }
        return particles;
    }

    private static void ApplyMove(ref Particle p, Direction dir, SimulationConfig config, Grid grid)
    {
        int nx = p.X, ny = p.Y;
        switch (dir)
        {
            case Direction.Up: ny--; break;
            case Direction.Down: ny++; break;
            case Direction.Left: nx--; break;
            case Direction.Right: nx++; break;
            default: return;
        }
        nx = Math.Clamp(nx, 0, config.GridWidth - 1);
        ny = Math.Clamp(ny, 0, config.GridHeight - 1);
        grid.MoveParticleAtomic(p.X, p.Y, nx, ny);
        p.X = nx; p.Y = ny;
    }

    private static void PrintResult(BenchmarkResult r)
    {
        Console.ForegroundColor = r.MassConserved ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine($"  [{r.Name}]");
        Console.ResetColor();
        Console.WriteLine($"    Workers: {r.Workers} | Particles: {r.ParticleCount:N0} | Ticks: {r.Ticks}");
        Console.WriteLine($"    Time: {r.ElapsedMs:F0}ms | TPS: {r.TicksPerSecond:F0} | Moves/s: {r.MovesPerSecond:N0}");
        Console.WriteLine($"    Mass: ΣCells={r.FinalSum}/{r.ParticleCount} [{(r.MassConserved ? "OK" : "DRIFT")}]\n");
    }

    private static void PrintSummaryTable(List<BenchmarkResult> results, SimulationConfig config)
    {
        Console.WriteLine(new string('═', 80));
        Console.WriteLine("  SUMMARY TABLE");
        Console.WriteLine(new string('─', 80));
        Console.WriteLine($"  {"Strategy",-35} {"Workers",7} {"TPS",10} {"Moves/s",15} {"Mass",6}");
        Console.WriteLine(new string('─', 80));

        var baselineTps = results.FirstOrDefault(r => r.Name.Contains("1w"))?.TicksPerSecond ?? 1;

        foreach (var r in results)
        {
            double speedup = r.TicksPerSecond / (baselineTps + 1e-9);
            Console.ForegroundColor = r.MassConserved ? ConsoleColor.White : ConsoleColor.Red;
            Console.WriteLine($"  {r.Name,-35} {r.Workers,7} {r.TicksPerSecond,10:F0} {r.MovesPerSecond,15:N0} {(r.MassConserved ? "  OK" : "FAIL"),6}  ×{speedup:F2}");
        }

        Console.ResetColor();
        Console.WriteLine(new string('═', 80) + "\n");
    }
}
