using System.Threading;

namespace BrownianMotion.Simulator.Core;

/// <summary>
/// Система знімків із подвійним буфером для рівня візуалізації.
/// 
/// Симулятор безперервно записує дані в активну сітку.
/// Під час кожного такту знімка робочі потоки копіюють стан сітки в «задній» буфер.
/// Рендерер завжди зчитує дані з «переднього» буфера (останнього зафіксованого знімка).
/// Один обмін з блокуванням міняє місця «front» і «back» - без блокування з обох боків.
/// 
/// Це забезпечує:
///   - Потоки симуляції: ніколи не блокуються в очікуванні рендерера
///   - Потік рендерера: ніколи не блокується в очікуванні симуляції
///   - Сумісний кадр: рендерер бачить повний знімок, а не розірваний стан посередині кадру
/// </summary>
public sealed class DoubleBufferSnapshot
{
    private int[] _frontBuffer;
    private int[] _backBuffer;
    private int _swapGeneration; // збільшується при кожному обміні - рендерер може виявити "застарілість"

    public int Width { get; }
    public int Height { get; }

    // Статистичні дані, доступні для візуалізатора
    public long LastSnapshotTick { get; private set; }
    public long ParticleSum { get; private set; }
    public int Generation => Volatile.Read(ref _swapGeneration);

    public DoubleBufferSnapshot(int width, int height)
    {
        Width = width;
        Height = height;
        _frontBuffer = new int[width * height];
        _backBuffer = new int[width * height];
    }

    /// <summary>
    /// Викликається під час симуляції: копіювання активної сітки у буфер відтворення, а потім обмін.
    /// Це єдиний "writer". Якщо цю функцію викликають кілька потоків, вони вступають в гонитву за обміном,
    /// але виграє лише один - це все одно безпечно, оскільки запис, що програв, просто перезаписується
    /// під час наступного циклу створення знімка.
    /// </summary>
    public void Capture(Grid grid, long tick)
    {
        // Записати знімок у буфер відтворення
        grid.CopyTo(_backBuffer);

        long sum = 0;
        for (int i = 0; i < _backBuffer.Length; i++)
            sum += _backBuffer[i];
        ParticleSum = sum;
        LastSnapshotTick = tick;

        // Атомарний обмін показниками - обмін місцями «спереду» і «ззаду»
        var tmp = _frontBuffer;
        _frontBuffer = Volatile.Read(ref _backBuffer);
        Volatile.Write(ref _backBuffer, tmp);

        Interlocked.Increment(ref _swapGeneration);
    }

    /// <summary>
    /// Викликається рендерером: зчитування комірки з буфера FRONT (останнього збереженого).
    /// Блокування не потрібне - рендерер лише зчитує, а симулятор лише обмінюється показниками.
    /// </summary>
    public int ReadFront(int x, int y)
    {
        var front = Volatile.Read(ref _frontBuffer);
        int idx = y * Width + x;
        return idx < front.Length ? front[idx] : 0;
    }

    /// <summary>Копіюємо весь передній буфер для візуалізації.</summary>
    public void ReadFrontAll(int[] destination)
    {
        var front = Volatile.Read(ref _frontBuffer);
        Array.Copy(front, destination, Math.Min(front.Length, destination.Length));
    }
}
