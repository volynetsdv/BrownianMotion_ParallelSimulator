using BrownianMotion.Simulator.Core;

namespace BrownianMotion.Simulator.Concurrency;

/// <summary>
/// ЕТАП 3B + ЕТАП 4: Високопродуктивний механізм моделювання.
/// 
/// Ключові проектні рішення:
/// 
/// 1. Атомарні операції (з блокуванням):
///    Операції Interlocked.Decrement/Increment реалізуються за допомогою єдиної інструкції LOCK XADD процесора.
///    Відсутність накладних витрат на блокування, відсутність перемикань у режим ядра, відсутність перемикань контексту.
///    Пропускна здатність: мільйони переміщень на секунду проти тисяч при блокуваннях на рівні кожної комірки.
/// 
/// 2. ПУЛ РОБОЧИХ ПОТОКІВ ІЗ РОЗБИТТЯМ НА БЛОКИ (а не 1 потік на частинку):
///    Створення 100 000 потоків для 100 000 частинок призвело б до:
///    - Вичерпання системної пам’яті (кожному потоку потрібно ~1 МБ стеку)
///    - Величезних накладних витрат на перемикання контексту
///    - Перевантаження планувальника ОС
/// 
///    Розбиття на блоки: N робочих * (K/N частинок кожному) - це робота O(K) з O(N) потоками.
///    N = Environment.ProcessorCount - це оптимальне значення для завдань, що залежать від продуктивності процесора.
/// 
/// 3. СИНХРОНІЗАЦІЯ З БАР'ЄРОМ:
///    Робочі процеси синхронізуються на межі такту, щоб забезпечити узгодженість знімків стану.
///    Колбек після фази бар'єру виконується один раз на такт на останньому потоці, який досягає барєра.
/// 
/// 4. THREADLOCAL<RANDOM>:
///    Кожен робочий процес має власний екземпляр Random - нульова конкуренція за генератор випадкових чисел.
/// </summary>
public sealed class HighPerformanceSimulationEngine : IDisposable
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

    // Performance counters
    private long _totalAtomicOps;

    private volatile bool _stepRequested = false;

    public Grid Grid => _grid;
    public DoubleBufferSnapshot Snapshot => _snapshot;
    public SimulationStats Stats => _stats;
    public bool IsRunning => _running;
    public int CurrentTick => _currentTick;
    public long TotalAtomicOps => Volatile.Read(ref _totalAtomicOps);
    // Механізм паузи/відновлення для демонстрації 
    private readonly ManualResetEventSlim _pauseEvent = new(true);
    public bool IsPaused => !_pauseEvent.IsSet;
    public void TogglePause()
    {
        if (_pauseEvent.IsSet)
            _pauseEvent.Reset(); // пауза - закриваємо ворота
        else
            _pauseEvent.Set(); // пуск - відкриваємо ворота
    }

    public void RequestSingleStep()
    {
        // Дозволяємо потокам пройти рівно один такт
        _stepRequested = true;
        _pauseEvent.Set(); // Тимчасово відкриваємо ворота
    }

    public HighPerformanceSimulationEngine(SimulationConfig config)
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

    /// <summary>
    /// Запускає пул робочих процесів. Робочі процеси - це завдання типу Task.Run (на базі ThreadPool).
    /// Кожне завдання прив’язується до блоку частинок на весь час роботи симуляції.
    /// </summary>
    public void Start()
    {
        if (_running) return;
        _running = true;
        _cts = new CancellationTokenSource();

        int workerCount = _config.WorkerCount;

        _tickBarrier = new Barrier(workerCount, _ =>
        {
            int tick = Interlocked.Increment(ref _currentTick);

            // Створює знімок кожні N тактів (обмін даними з подвійним буфером без блокування)
            if (tick % _config.SnapshotIntervalTicks == 0)
                _snapshot.Capture(_grid, tick);

            // Перевіряємо дотримання закону збереження маси
            var (sum, valid) = _grid.ValidateParticleCount(_config.ParticleCount);
            _stats.RecordValidation(sum, _config.ParticleCount);
            _stats.RecordTick(_config.ParticleCount);

            if (tick % _config.ConsoleLogIntervalTicks == 0)
            {
                Console.ForegroundColor = valid ? ConsoleColor.Green : ConsoleColor.Yellow;
                Console.WriteLine(
                    $"[HP][Tick {tick:D6}] ΣCells={sum} Δ={(sum - _config.ParticleCount):+#;-#;0} | " +
                    $"TPS={_stats.TicksPerSecond:F0} | AtomicOps={TotalAtomicOps:N0}");
                Console.ResetColor();
            }

            if (_config.MaxTicks > 0 && tick >= _config.MaxTicks)
                _cts!.Cancel();
        });

        // Розподіл часток між воркерами (Chunking)
        int chunkSize = (int)Math.Ceiling((double)_config.ParticleCount / workerCount);
        _workerTasks = new Task[workerCount];

        for (int w = 0; w < workerCount; w++)
        {
            int start = w * chunkSize;
            int end = Math.Min(start + chunkSize, _config.ParticleCount);

            _workerTasks[w] = Task.Run(() => WorkerLoop(start, end, _cts.Token), _cts.Token);
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"[HighPerf] Started {workerCount} workers, {_config.ParticleCount:N0} particles, chunk≈{chunkSize}");
        Console.ResetColor();
    }

    /// <summary>
    /// Основний цикл моделювання. Кожен робочий процес відповідає за частинки з індексами startIdx..endIdx.
    /// Немає спільного змінного стану, за винятком комірок сітки (доступ до яких здійснюється атомарно).
    /// </summary>
    private void WorkerLoop(int startIdx, int endIdx, CancellationToken ct)
    {
        double probUp = _config.ProbUp;
        double probDown = _config.ProbDown;
        double probLeft = _config.ProbLeft;
        double probRight = _config.ProbRight;
        int gridW = _config.GridWidth - 1;
        int gridH = _config.GridHeight - 1;

        long localAtomicOps = 0;

        while (!ct.IsCancellationRequested)
        {
            if (!_stepRequested)
            {
                try { _pauseEvent.Wait(ct); }
                catch (OperationCanceledException) { break; }
            }
            // Цикл обробки: виконуємо обробку фрагменту частинок цього робочого процесу 
            for (int i = startIdx; i < endIdx; i++)
            {
                ref Particle p = ref _particles[i]; // ref дозволяє уникнути копіювання структури

                Direction dir = RngFactory.NextDirection(probUp, probDown, probLeft, probRight);

                if (dir == Direction.Stay) continue;

                int nx = p.X, ny = p.Y;
                switch (dir)
                {
                    case Direction.Up: ny--; break;
                    case Direction.Down: ny++; break;
                    case Direction.Left: nx--; break;
                    case Direction.Right: nx++; break;
                }

                // Reflection: фіксація до межі сітки (відбивання від стінок)
                if (nx < 0) nx = 0; else if (nx > gridW) nx = gridW;
                if (ny < 0) ny = 0; else if (ny > gridH) ny = gridH;

                if (nx == p.X && ny == p.Y) continue; // не рухаємось

                // атомарність: дві взаємозалежні операції - безпечні для 
                // багатопотокового виконання, без блокувань
                _grid.MoveParticleAtomic(p.X, p.Y, nx, ny);

                p.X = nx;
                p.Y = ny;
                localAtomicOps += 2; // one decrement + one increment
            }

            // Межа такту: дочекатися, поки всі робочі процеси завершать цей такт (синхронізація бар'єром)
            try
            {
                _tickBarrier!.SignalAndWait(ct);
                // намагаюся виправити лічильник
                Interlocked.Add(ref _totalAtomicOps, localAtomicOps);
                localAtomicOps = 0;
            }
            catch (OperationCanceledException) { break; }
            catch (BarrierPostPhaseException) { break; }
            if (_stepRequested)
            {
                _stepRequested = false;
                _pauseEvent.Reset(); // Знову стаємо на паузу
            }
        }

        // Скинути значення локального лічильника до глобального
        //Interlocked.Add(ref _totalAtomicOps, localAtomicOps);
    }

    public async Task StopAsync()
    {
        _running = false;
        _cts?.Cancel();
        if (_workerTasks != null)
        {
            try { await Task.WhenAll(_workerTasks); }
            catch { }
        }
    }

    /// <summary>
    /// Остаточна перевірка: підтверджує, що значення K зберігається після завершення моделювання.
    /// </summary>
    public void PrintFinalValidation()
    {
        var (sum, valid) = _grid.ValidateParticleCount(_config.ParticleCount);
        Console.WriteLine("\n=============================================");
        Console.WriteLine("        FINAL MASS CONSERVATION CHECK ");
        Console.WriteLine($"  Expected K = {_config.ParticleCount,-10} ");
        Console.WriteLine($"  Actual  Σ  = {sum,-10} ");
        Console.WriteLine($"  Drift      = {(sum - _config.ParticleCount),-10:+#;-#;0} ");
        Console.WriteLine($"  Status     = {(valid ? "OK: COMPLAINT" : "ERROR: NON-COMPLAINT"),-12} ");
        Console.WriteLine("=============================================");
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
        _cts?.Dispose();
        _tickBarrier?.Dispose();
    }
}
