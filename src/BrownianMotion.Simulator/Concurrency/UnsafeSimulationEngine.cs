using System.Threading;
using System.Threading.Tasks;
using BrownianMotion.Simulator.Core;

namespace BrownianMotion.Simulator.Concurrency;

/// <summary>
/// ЕТАП 2: Небезпечний механізм симуляції - навмисна гонитва.
/// 
/// Кожне робоче завдання обробляє призначений йому фрагмент частинок у кожному такті.
/// Переміщення частинок (зменшення значення джерела, збільшення значення пункту 
/// призначення) НЕ Є атомарними операціями.
/// 
/// Як організована гонитва:
///   Потік A зчитує cell(5,5) = 1 → планує перемістити частинку назовні
///   Потік B зчитує cell(5,5) = 1 → також планує перемістити частинку назовні
///   Потік A записує cell(5,5) = 0
///   Потік B записує cell(5,5) = 0  ← втрачено одну частинку!
///   Обидва записують +1 у свої відповідні пункти призначення → фантомна частинка створена деінде
/// 
/// Спостережуваний ефект: ΣCells відхиляється від початкового K протягом декількох секунд.
/// </summary>
public sealed class UnsafeSimulationEngine : IDisposable
{
    private readonly SimulationConfig _config;
    private readonly Grid _grid;
    private readonly Particle[] _particles;
    private readonly DoubleBufferSnapshot _snapshot;
    private readonly SimulationStats _stats;

    private CancellationTokenSource? _cts;
    private Task[]? _workerTasks;
    private volatile int _currentTick;
    private volatile bool _running;

    // Барєр для синхронізації всіх робочих процесів наприкінці кожного такту
    private Barrier? _tickBarrier;

    public Grid Grid => _grid;
    public DoubleBufferSnapshot Snapshot => _snapshot;
    public SimulationStats Stats => _stats;
    public bool IsRunning => _running;
    public int CurrentTick => _currentTick;

    public UnsafeSimulationEngine(SimulationConfig config)
    {
        _config = config;
        _grid = new Grid(config.GridWidth, config.GridHeight);
        _particles = new Particle[config.ParticleCount];
        _snapshot = new DoubleBufferSnapshot(config.GridWidth, config.GridHeight);
        _stats = new SimulationStats();

        RngFactory.Initialize(config.Seed);
        InitializeParticles();
    }

    private void InitializeParticles()
    {
        var rng = new Random(_config.Seed ?? Environment.TickCount);
        for (int i = 0; i < _config.ParticleCount; i++)
        {
            int x = rng.Next(_config.GridWidth);
            int y = rng.Next(_config.GridHeight);
            _particles[i] = new Particle(i, x, y);
            _grid.Set(x, y, _grid.Get(x, y) + 1);
        }
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _cts = new CancellationTokenSource();

        int workerCount = _config.WorkerCount;
        _tickBarrier = new Barrier(workerCount, _ =>
        {
            // Цей зворотний виклик виконується один раз на такт після завершення роботи всіх робочих процесів
            int tick = Interlocked.Increment(ref _currentTick);
            if (tick % _config.SnapshotIntervalTicks == 0)
                _snapshot.Capture(_grid, tick);

            var (sum, valid) = _grid.ValidateParticleCount(_config.ParticleCount);
            _stats.RecordValidation(sum, _config.ParticleCount);
            _stats.RecordTick(_config.ParticleCount);

            if (!valid && tick % _config.ConsoleLogIntervalTicks == 0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[UNSAFE][Tick {tick}] RACE CONDITION DETECTED: ΣCells={sum} expected={_config.ParticleCount} drift={sum - _config.ParticleCount:+#;-#;0}");
                Console.ResetColor();
            }
            else if (tick % _config.ConsoleLogIntervalTicks == 0)
            {
                Console.WriteLine(_stats.FormatSummary(_config.ParticleCount, SimulationMode.Unsafe, workerCount));
            }

            if (_config.MaxTicks > 0 && tick >= _config.MaxTicks)
                _cts!.Cancel();
        });

        // Ділмо частинки на блоки для завдань робочих процесів
        int chunkSize = (int)Math.Ceiling((double)_config.ParticleCount / workerCount);
        _workerTasks = new Task[workerCount];

        for (int w = 0; w < workerCount; w++)
        {
            int start = w * chunkSize;
            int end = Math.Min(start + chunkSize, _config.ParticleCount);
            int workerIdx = w;

            _workerTasks[w] = Task.Run(() => WorkerLoop(start, end, _cts.Token), _cts.Token);
        }
    }

    private void WorkerLoop(int startIdx, int endIdx, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            for (int i = startIdx; i < endIdx; i++)
            {
                ref Particle p = ref _particles[i];
                Direction dir = RngFactory.NextDirection(
                    _config.ProbUp, _config.ProbDown, _config.ProbLeft, _config.ProbRight);

                int nx = p.X, ny = p.Y;
                switch (dir)
                {
                    case Direction.Up: ny--; break;
                    case Direction.Down: ny++; break;
                    case Direction.Left: nx--; break;
                    case Direction.Right: nx++; break;
                }

                // Reflection boundary: кріпимо до сітки
                nx = Math.Clamp(nx, 0, _config.GridWidth - 1);
                ny = Math.Clamp(ny, 0, _config.GridHeight - 1);

                // UNSAFE: Оновлення неатомарної сітки - тут виникає ситуація гонитви!
                _grid.MoveParticleUnsafe(p.X, p.Y, nx, ny);

                p.X = nx;
                p.Y = ny;
            }

            try { _tickBarrier!.SignalAndWait(ct); }
            catch (OperationCanceledException) { break; }
            catch (BarrierPostPhaseException) { break; }
        }
    }

    public async Task StopAsync()
    {
        _running = false;
        _cts?.Cancel();
        if (_workerTasks != null)
        {
            try { await Task.WhenAll(_workerTasks); }
            catch { /* expected on cancellation */ }
        }
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
        _cts?.Dispose();
        _tickBarrier?.Dispose();
    }
}
