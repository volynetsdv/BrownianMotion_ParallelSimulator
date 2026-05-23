using System.Diagnostics;
using System.Threading;

namespace BrownianMotion.Simulator.Core;

/// <summary>
/// Відстежує статистику продуктивності симуляції в режимі реального часу.
/// Усі операції запису використовують Interlocked для забезпечення безпечної роботи між потоками; операції читання мають рекомендаційний характер (дані можуть бути дещо застарілими).
/// </summary>
public sealed class SimulationStats
{
    private long _totalTicks;
    private long _particleMovesThisSecond;
    private long _particleMovesLastSecond;
    private long _validationFailures;
    private long _lastParticleSum;

    private readonly Stopwatch _uptime = Stopwatch.StartNew();
    private readonly Stopwatch _tpsTimer = Stopwatch.StartNew();
    private long _ticksAtLastTpsSample;

    public long TotalTicks => Volatile.Read(ref _totalTicks);
    public long ParticleMoves => Volatile.Read(ref _particleMovesLastSecond);
    public long ValidationFailures => Volatile.Read(ref _validationFailures);
    public long LastParticleSum => Volatile.Read(ref _lastParticleSum);
    public double UptimeSeconds => _uptime.Elapsed.TotalSeconds;

    // Кількість імпульсів на секунду, вимірюється один раз на секунду
    private double _tps;
    public double TicksPerSecond => _tps;

    public void RecordTick(int particlesMovedThisTick)
    {
        Interlocked.Increment(ref _totalTicks);
        Interlocked.Add(ref _particleMovesThisSecond, particlesMovedThisTick);

        // Update TPS approximately once per second
        if (_tpsTimer.Elapsed.TotalSeconds >= 1.0)
        {
            long currentTick = Volatile.Read(ref _totalTicks);
            double elapsed = _tpsTimer.Elapsed.TotalSeconds;
            _tps = (currentTick - _ticksAtLastTpsSample) / elapsed;
            _ticksAtLastTpsSample = currentTick;

            Interlocked.Exchange(ref _particleMovesLastSecond, Volatile.Read(ref _particleMovesThisSecond));
            Interlocked.Exchange(ref _particleMovesThisSecond, 0);
            _tpsTimer.Restart();
        }
    }

    public void RecordValidation(long particleSum, int expectedK)
    {
        Interlocked.Exchange(ref _lastParticleSum, particleSum);
        if (particleSum != expectedK)
            Interlocked.Increment(ref _validationFailures);
    }

    public string FormatSummary(int expectedK, SimulationMode mode, int workerCount) =>
        $"[{mode}] Tick={TotalTicks:N0} | TPS={TicksPerSecond:F0} | " +
        $"Particles={LastParticleSum}/{expectedK} | " +
        $"Drift={(LastParticleSum - expectedK):+#;-#;0} | " +
        $"Errors={ValidationFailures} | Workers={workerCount} | " +
        $"Uptime={UptimeSeconds:F1}s";
}
