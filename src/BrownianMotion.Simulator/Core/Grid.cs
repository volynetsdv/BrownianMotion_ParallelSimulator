using System.Runtime.CompilerServices;
using System.Threading;

namespace BrownianMotion.Simulator.Core;

/// <summary>
/// 2D-сітка, що зберігається у вигляді плоского масиву цілих чисел для забезпечення локальності кешу.
/// Кожна комірка містить кількість частинок, які її наразі займають.
/// 
/// Сітка є спільним змінним станом, за доступ до якого конкурують усі робочі потоки.
/// Різні режими моделювання по-різному вирішують цю проблему конкуренції.
/// </summary>
public sealed class Grid
{
    private readonly int[] _cells;
    public readonly int Width;
    public readonly int Height;

    // Блокування об’єктів за комірками (використовується лише в режимі LockBased)
    // Індексування за лінеаризованим індексом комірки для забезпечення послідовності блокування.
    private readonly object[] _cellLocks;

    public Grid(int width, int height)
    {
        Width = width;
        Height = height;
        _cells = new int[width * height];
        _cellLocks = new object[width * height];
        for (int i = 0; i < _cellLocks.Length; i++)
            _cellLocks[i] = new object();
    }

    //Indexing

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Index(int x, int y) => y * Width + x;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool InBounds(int x, int y) => x >= 0 && x < Width && y >= 0 && y < Height;

    // Unsafe (no sync)

    /// <summary>
    /// НЕБЕЗПЕЧНО: Звичайне читання - без бар'єру пам'яті. 
    /// В умовах гонитви операцій спостерігаються випадки зчитування з пошкоджених даних.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadUnsafe(int x, int y) => _cells[Index(x, y)];

    /// <summary>
    /// UNSAFE: Наївний підхід до операцій «читання-модифікація-запис» без атомарності. Створює умови для виникнення гонитви.
    /// Два потоки можуть одночасно зчитувати одну й ту саму вихідну комірку, обидва бачать, що count > 0,
    /// обидва зменшують значення, що призводить до від’ємного значення count і втрати частинок.
    /// </summary>
    public void MoveParticleUnsafe(int fromX, int fromY, int toX, int toY)
    {
        int fromIdx = Index(fromX, fromY);
        int toIdx = Index(toX, toY);
        _cells[fromIdx]--;   // не атомарна операція: читання, зменшення, запис - три етапи
        _cells[toIdx]++;     // та сама проблема з інкрементом
    }

    // Lock-Based 

    /// <summary>
    /// Переміщення з використанням блокувань та RESOURCE ORDERING для запобігання взаємним блокуванням.
    /// 
    /// Сценарій взаємного блокування без впорядкування:
    ///   Потік A блокує комірку (2,3), а потім очікує на комірку (2,4)
    ///   Потік B блокує комірку (2,4), а потім чекає на комірку (2,3) -> циклічне очікування -> DEADLOCK
    /// 
    /// Рішення: Завжди отримувати блокування у порядку зростання індексів (я робив щось схоже в одній з попередніх лабораторних).
    ///   min(fromIdx, toIdx) блокується першим - гарантовано відсутнє циклічне очікування.
    /// </summary>
    public void MoveParticleLocked(int fromX, int fromY, int toX, int toY)
    {
        int fromIdx = Index(fromX, fromY);
        int toIdx = Index(toX, toY);

        if (fromIdx == toIdx) return;

        // Resource Ordering: завжди спершу блокуємо індекс з меншим номером
        int firstIdx = Math.Min(fromIdx, toIdx);
        int secondIdx = Math.Max(fromIdx, toIdx);

        lock (_cellLocks[firstIdx])
            lock (_cellLocks[secondIdx])
            {
                _cells[fromIdx]--;
                _cells[toIdx]++;
            }
    }

    /// <summary>
    /// Демонстрація навмисного дедлоку: Блокування в довільному порядку (не використовується в звичайній симуляції).
    /// Одночасний виклик з двох потоків для надійного створення дедлоку.
    /// </summary>
    public void MoveParticleDeadlockDemo(int fromX, int fromY, int toX, int toY)
    {
        int fromIdx = Index(fromX, fromY);
        int toIdx = Index(toX, toY);
        if (fromIdx == toIdx) return;

        // Intentionally lock in address order - not index order - causes deadlock
        lock (_cellLocks[fromIdx])   // ← Thread A locks from, Thread B locks to
            lock (_cellLocks[toIdx])     // ← Both now wait for the other → deadlock
            {
                _cells[fromIdx]--;
                _cells[toIdx]++;
            }
    }

    // High-Performance (Interlocked)

    /// <summary>
    /// Високопродуктивне атомарне переміщення з використанням Interlocked.Add.
    /// 
    /// Без блокувань - кожна операція з коміркою є окремою атомарною інструкцією CAS/XADD.
    /// Існує коротке вікно несумісності (після зменшення, перед збільшенням),
    /// але загальне значення K зберігається з часом і дані не пошкоджуються.
    /// 
    /// Це рекомендований підхід для високої продуктивності та відсутності блокувань, хоча він може 
    /// призвести до тимчасових коливань у візуалізації (наприклад, комірка може короткочасно 
    /// показувати -1 або 0, навіть якщо частинка фізично там знаходиться).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void MoveParticleAtomic(int fromX, int fromY, int toX, int toY)
    {
        if (fromX == toX && fromY == toY) return;
        Interlocked.Decrement(ref _cells[Index(fromX, fromY)]);
        Interlocked.Increment(ref _cells[Index(toX, toY)]);
    }

    // Snapshot

    /// <summary>
    /// Копіює стан сітки у вказаний буфер без призупинення симуляції.
    /// Використовує Volatile.Read для кожної комірки, щоб отримати відносно узгоджений знімок.
    /// Для отримання справді атомарних знімків краще використовувати подвійну буферизацію (див. DoubleBufferSnapshot).
    /// </summary>
    public void CopyTo(int[] buffer)
    {
        if (buffer.Length < _cells.Length)
            throw new ArgumentException("Buffer too small", nameof(buffer));

        for (int i = 0; i < _cells.Length; i++)
            buffer[i] = Volatile.Read(ref _cells[i]);
    }

    /// <summary>
    /// Перевіряє дотримання закону збереження маси: сума всіх комірок повинна дорівнювати K.
    /// </summary>
    public (long sum, bool valid) ValidateParticleCount(int expectedK)
    {
        long sum = 0;
        for (int i = 0; i < _cells.Length; i++)
            sum += Volatile.Read(ref _cells[i]);

        return (sum, sum == expectedK);
    }

    /// <summary>Пряме встановлення значення комірки (використовується під час ініціалізації).</summary>
    public void Set(int x, int y, int value) => _cells[Index(x, y)] = value;

    /// <summary>Отримати значення комірки безпосередньо (для візуалізації зчитування під знімком екрану).</summary>
    public int Get(int x, int y) => Volatile.Read(ref _cells[Index(x, y)]);

    public void Reset()
    {
        Array.Clear(_cells, 0, _cells.Length);
    }

    public int TotalCells => _cells.Length;
}
