using System.Threading;
using System.Threading.Tasks;
using BrownianMotion.Simulator.Core;

namespace BrownianMotion.Simulator.Concurrency;

/// <summary>
/// ЕТАП 3А: Механізм моделювання на основі блокувань, що демонструє:
/// 
/// А) ПРОБЛЕМУ ВЗАЄМНОГО БЛОКУВАННЯ (теорія, проілюстрована в коментарях):
///    Якщо потік A блокує cell[i], а потім чекає на cell[j],
///    а потік B блокує cell[j], а потім чекає на cell[i],
///    обидва потоки блокуються назавжди - виникає взаємне блокування, тобто Deadlock.
/// 
/// Б) РІШЕННЯ - впорядкування ресурсів (Дейкстра):
///    Усі потоки завжди отримують блокування комірок у порядку зростання індексів.
///    Це порушує циклічну умову очікування → тупикова ситуація неможлива.
///    Дивіться Grid.MoveParticleLocked() для реалізації.
/// 
/// Примітка щодо продуктивності: детальне блокування на рівні окремих комірок
/// все ще спричиняє значну конкуренцію за ресурси, коли багато частинок
/// потрапляють до одних і тих самих комірок. HighPerformanceEngine вирішує цю проблему.
/// </summary>
public sealed class LockBasedSimulationEngine : IDisposable
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
    private Barrier? _tickBarrier;

    public Grid Grid => _grid;
    public DoubleBufferSnapshot Snapshot => _snapshot;
    public SimulationStats Stats => _stats;
    public bool IsRunning => _running;
    public int CurrentTick => _currentTick;

    public LockBasedSimulationEngine(SimulationConfig config)
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
            int tick = Interlocked.Increment(ref _currentTick);
            if (tick % _config.SnapshotIntervalTicks == 0)
                _snapshot.Capture(_grid, tick);

            var (sum, valid) = _grid.ValidateParticleCount(_config.ParticleCount);
            _stats.RecordValidation(sum, _config.ParticleCount);
            _stats.RecordTick(_config.ParticleCount);

            if (tick % _config.ConsoleLogIntervalTicks == 0)
            {
                if (valid)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"[LOCK-BASED][Tick {tick}] ✓ ΣCells={sum} (correct) | {_stats.FormatSummary(_config.ParticleCount, SimulationMode.LockBased, workerCount)}");
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"[LOCK-BASED][Tick {tick}] Transient drift ΣCells={sum} expected={_config.ParticleCount}");
                }
                Console.ResetColor();
            }

            if (_config.MaxTicks > 0 && tick >= _config.MaxTicks)
                _cts!.Cancel();
        });

        int chunkSize = (int)Math.Ceiling((double)_config.ParticleCount / workerCount);
        _workerTasks = new Task[workerCount];

        for (int w = 0; w < workerCount; w++)
        {
            int start = w * chunkSize;
            int end = Math.Min(start + chunkSize, _config.ParticleCount);

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

                nx = Math.Clamp(nx, 0, _config.GridWidth - 1);
                ny = Math.Clamp(ny, 0, _config.GridHeight - 1);

                // SAFE: resource-ordered locking prevents deadlock
                _grid.MoveParticleLocked(p.X, p.Y, nx, ny);

                p.X = nx;
                p.Y = ny;
            }

            try { _tickBarrier!.SignalAndWait(ct); }
            catch (OperationCanceledException) { break; }
            catch (BarrierPostPhaseException) { break; }
        }
    }

    /// <summary>
    /// Демонструє сценарій взаємного блокування в контрольованому режимі з обмеженим часом.
    /// Два потоки намагаються перемістити частинки між тими самими двома комірками,
    /// але блокують їх у протилежній послідовності. Повертається після закінчення часу очікування (виявлено взаємне блокування).
    /// 
    /// ПРИМІТКА: Це навчальний приклад. У основній симуляції завжди використовується безпечне блокування.
    /// </summary>
    public static void DemonstrateDeadlock(Grid grid, int timeoutMs = 3000)
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine("\n============================================");
        Console.WriteLine("       DEADLOCK DEMONSTRATION  ");
        Console.WriteLine("  Two threads racing for same two cells ");
        Console.WriteLine("============================================");
        Console.ResetColor();

        // Два індекси комірок, за які буде вестися боротьба
        int cellA = 0, cellB = 1;

        grid.Set(0, 0, 10);
        grid.Set(1, 0, 10);

        bool deadlockDetected = false;
        var cts = new CancellationTokenSource(timeoutMs);

        var lockA = new object();
        var lockB = new object();

        var t1 = new Thread(() =>
        {
            Console.WriteLine("[Thread 1] Attempting: lock(A) → lock(B)");
            lock (lockA)
            {
                Console.WriteLine("[Thread 1] Acquired lockA, sleeping briefly...");
                Thread.Sleep(50); // даємо потоку 2 час, щоб отримати блокування lockB
                Console.WriteLine("[Thread 1] Now waiting for lockB... (potential deadlock)");
                lock (lockB)
                {
                    Console.WriteLine("[Thread 1] Got both locks (deadlock avoided this time)");
                }
            }
        });

        var t2 = new Thread(() =>
        {
            Console.WriteLine("[Thread 2] Attempting: lock(B) → lock(A)");
            lock (lockB)
            {
                Console.WriteLine("[Thread 2] Acquired lockB, sleeping briefly...");
                Thread.Sleep(50);
                Console.WriteLine("[Thread 2] Now waiting for lockA... (potential deadlock)");
                lock (lockA)
                {
                    Console.WriteLine("[Thread 2] Got both locks (deadlock avoided this time)");
                }
            }
        });

        t1.IsBackground = true;
        t2.IsBackground = true;
        t1.Start();
        Thread.Sleep(10);
        t2.Start();

        bool t1Done = t1.Join(timeoutMs);
        bool t2Done = t2.Join(timeoutMs);

        if (!t1Done || !t2Done)
        {
            deadlockDetected = true;
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n[DEADLOCK] Threads did not complete within {timeoutMs}ms!");
            Console.WriteLine("[DEADLOCK] Thread 1 alive: " + t1.IsAlive);
            Console.WriteLine("[DEADLOCK] Thread 2 alive: " + t2.IsAlive);
            Console.ResetColor();
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("\n[SOLUTION] Resource Ordering: always lock lower index first.");
        Console.WriteLine("[SOLUTION] Grid.MoveParticleLocked() implements this correctly.");
        Console.WriteLine("[SOLUTION] Circular wait is impossible → deadlock impossible.\n");
        Console.ResetColor();
    }

    public async Task StopAsync()
    {
        _running = false;
        _cts?.Cancel();
        if (_workerTasks != null)
        {
            try { await Task.WhenAll(_workerTasks); }
            catch (Exception ex)
            {
                Console.WriteLine($"Error occurred while stopping worker tasks: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
        _cts?.Dispose();
        _tickBarrier?.Dispose();
    }
}
