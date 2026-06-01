using BrownianMotion.Simulator.Benchmarks;
using BrownianMotion.Simulator.Concurrency;
using BrownianMotion.Simulator.Core;
using BrownianMotion.Simulator.Visualization;
using Raylib_cs;

/// <summary>
/// Симулятор паралелізму на прикладі Броунівського руху - Entry Point
/// 
/// Usage:
///   dotnet run                         -> HighPerformance mode з візуалізацією
///   dotnet run -- --mode unsafe        -> Race condition
///   dotnet run -- --mode lock          -> На основі блокування з упорядкуванням ресурсів
///   dotnet run -- --mode highperf      -> Interlocked atomic (за замовчуванням)
///   dotnet run -- --benchmark          -> Порівняння продуктивності без графічного інтерфейсу
///   dotnet run -- --no-vis             -> Headless simulation (без графічного інтерфейсу)
///   dotnet run -- --particles 50000    -> Налаштування кількості частинок
///   dotnet run -- --seed 1234          -> Повторюваний запуск з певним seed
///   dotnet run -- --deadlock-demo      -> Показати сценарій взаємного блокування + рішення
/// 
/// Примітка:
/// - Візуалізацію в режимі HighPerformance можна ставити на паузу клавішею Space, і після цього крокувати по одному такту клавішею Right.
/// </summary>

PrintBanner();

 // Parse arguments  
var args_list = args.ToList();

SimulationMode mode = SimulationMode.HighPerformance;
bool runBenchmark = args_list.Contains("--benchmark");
bool noVis = args_list.Contains("--no-vis") || runBenchmark;
bool deadlockDemo = args_list.Contains("--deadlock-demo");

int particleCount = GetIntArg(args_list, "--particles", 10_000);
int gridW = GetIntArg(args_list, "--grid-w", 120);
int gridH = GetIntArg(args_list, "--grid-h", 80);
int workers = GetIntArg(args_list, "--workers", Environment.ProcessorCount);
int seed = GetIntArg(args_list, "--seed", 42);
int cellSize = GetIntArg(args_list, "--cell-size", 8);

if (args_list.Contains("--mode"))
{
    string modeStr = args_list[args_list.IndexOf("--mode") + 1].ToLower();
    mode = modeStr switch
    {
        "unsafe" => SimulationMode.Unsafe,
        "lock" => SimulationMode.LockBased,
        "highperf" => SimulationMode.HighPerformance,
        _ => throw new ArgumentException($"Unknown mode: {modeStr}. Use: unsafe | lock | highperf")
    };
}

var simConfig = new SimulationConfig
{
    GridWidth = gridW,
    GridHeight = gridH,
    ParticleCount = particleCount,
    Seed = seed,
    Mode = mode,
    WorkerCount = workers,
    EnableVisualization = !noVis,
    CellPixelSize = cellSize,
    ConsoleLogIntervalTicks = 100,
    BenchmarkTicks = 300,
};

Console.WriteLine($"Config: {simConfig.GridWidth}×{simConfig.GridHeight} grid | {simConfig.ParticleCount:N0} particles | Mode: {mode} | Workers: {workers}");
Console.WriteLine($"Seed: {simConfig.Seed} | Visualization: {simConfig.EnableVisualization}\n");

 // Deadlock demo 
if (deadlockDemo)
{
    var demoGrid = new Grid(10, 10);
    LockBasedSimulationEngine.DemonstrateDeadlock(demoGrid);
    if (!args_list.Contains("--continue")) return;
}

 // Benchmark mode
if (runBenchmark)
{
    PerformanceBenchmark.Run(simConfig);
    return;
}

 // Simulation mode
switch (mode)
{
    case SimulationMode.Unsafe:
        await RunUnsafe(simConfig);
        break;
    case SimulationMode.LockBased:
        await RunLockBased(simConfig);
        break;
    case SimulationMode.HighPerformance:
        await RunHighPerf(simConfig);
        break;
}

 // Runners 

static async Task RunHighPerf(SimulationConfig config)
{
    using var engine = new HighPerformanceSimulationEngine(config);
    engine.Start();

    if (config.EnableVisualization)
    {
        using var renderer = new SimulationRenderer(config, engine.Snapshot, engine.Stats, SimulationMode.HighPerformance);
        renderer.Initialize();
        while (!renderer.ShouldClose && engine.IsRunning)
        {
            if (Raylib.IsKeyPressed(KeyboardKey.Space))
            {
                engine.TogglePause();
            }
            else if (Raylib.IsKeyPressed(KeyboardKey.Right))
            {
                // Якщо симуляція вже на паузі, робимо крок
                if (engine.IsPaused)
                {
                    engine.RequestSingleStep();
                }
            }
            renderer.RenderFrame();
        }
    }
    else
    {
        Console.WriteLine("Running headless. Press Ctrl+C to stop.");
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        try { await Task.Delay(Timeout.Infinite, cts.Token); } catch { }
    }

    await engine.StopAsync();
    engine.PrintFinalValidation();
}

static async Task RunLockBased(SimulationConfig config)
{
    using var engine = new LockBasedSimulationEngine(config);
    engine.Start();

    if (config.EnableVisualization)
    {
        using var renderer = new SimulationRenderer(config, engine.Snapshot, engine.Stats, SimulationMode.LockBased);
        renderer.Initialize();
        while (!renderer.ShouldClose && engine.IsRunning)
            renderer.RenderFrame();
    }
    else
    {
        Console.WriteLine("Running headless. Press Ctrl+C to stop.");
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        try { await Task.Delay(Timeout.Infinite, cts.Token); } catch { }
    }

    await engine.StopAsync();

    var (sum, valid) = engine.Grid.ValidateParticleCount(config.ParticleCount);
    Console.WriteLine($"\nFinal validation: ΣCells={sum} expected={config.ParticleCount} [{(valid ? "CORRECT" : "INCORRECT")}]");
}

static async Task RunUnsafe(SimulationConfig config)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine("WARNING: Running in UNSAFE mode. Expect race conditions and particle drift!");
    Console.ResetColor();

    using var engine = new UnsafeSimulationEngine(config);
    engine.Start();

    if (config.EnableVisualization)
    {
        using var renderer = new SimulationRenderer(config, engine.Snapshot, engine.Stats, SimulationMode.Unsafe);
        renderer.Initialize();
        while (!renderer.ShouldClose && engine.IsRunning)
            renderer.RenderFrame();
    }
    else
    {
        Console.WriteLine("Running headless. Press Ctrl+C to stop.");
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        try { await Task.Delay(Timeout.Infinite, cts.Token); } catch { }
    }

    await engine.StopAsync();

    var (sum, valid) = engine.Grid.ValidateParticleCount(config.ParticleCount);
    Console.ForegroundColor = valid ? ConsoleColor.Green : ConsoleColor.Red;
    Console.WriteLine($"\nFinal validation: ΣCells={sum} expected={config.ParticleCount} Difference={sum - config.ParticleCount:+#;-#;0}");
    Console.ResetColor();
}

 // Helpers 

static int GetIntArg(List<string> args, string key, int defaultValue)
{
    int idx = args.IndexOf(key);
    if (idx >= 0 && idx + 1 < args.Count && int.TryParse(args[idx + 1], out int val))
        return val;
    return defaultValue;
}

static void PrintBanner()
{
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine(@"
====================================================================
       BROWNIAN MOTION PARALLEL SIMULATOR - .NET 10 / C#          
  Demonstrating: Race Conditions | Deadlocks | Atomic Ops         
                 Worker Pools | Double-Buffer Snapshots           
====================================================================");
    Console.ResetColor();

    Console.WriteLine("Modes:");
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Write("  --mode unsafe   ");
    Console.ResetColor();
    Console.WriteLine("Race condition demo (particle count drifts)");

    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.Write("  --mode lock     ");
    Console.ResetColor();
    Console.WriteLine("Lock-based sync (deadlock prevented via resource ordering)");

    Console.ForegroundColor = ConsoleColor.Green;
    Console.Write("  --mode highperf ");
    Console.ResetColor();
    Console.WriteLine("Interlocked atomic ops (default, recommended)");

    Console.WriteLine("  --benchmark      Performance comparison (no GUI)");
    Console.WriteLine("  --deadlock-demo  Deadlock scenario + solution");
    Console.WriteLine("  --no-vis         Headless mode");
    Console.WriteLine("  --particles N    Set particle count (default: 10000)");
    Console.WriteLine("  --workers N      Set worker thread count (default: CPU cores)");
    Console.WriteLine("  --seed N         RNG seed for reproducibility");
    Console.WriteLine();
}
