namespace BrownianMotion.Simulator.Core;

/// <summary>
/// Потокобезпечне генерування випадкових чисел із можливістю задавання початкового значення.
/// 
/// Проблема: System.Random не є потокобезпечним. Використання єдиного спільного екземпляра
/// призводить до пошкодження даних та вузьких місць у продуктивності.
/// 
/// Рішення: ThreadLocal<Random> надає кожному потоку власний екземпляр Random.
/// Завдяки початковому значенню кожен потік отримує детерміновано отримане початкове значення на основі
/// головного початкового значення + індексу потоку, що забезпечує відтворювані результати для кожного запуску.
/// 
/// Примітка: Точна послідовність частинок, що обробляються кожним потоком, може відрізнятися між
/// запусками через планувальник ОС, але генератор випадкових чисел кожного окремого потоку є детермінованим.
/// Для повністю відтворюваних симуляцій використовуйте однопотоковий режим.
/// </summary>
public static class RngFactory
{
    private static int? _masterSeed;
    private static int _threadCounter;

    // Кожен потік отримує власний об’єкт Random, який ініціалізується при першому зверненні
    private static readonly ThreadLocal<Random> _threadLocal = new(
        () =>
        {
            int seed = _masterSeed.HasValue
                ? _masterSeed.Value ^ (Interlocked.Increment(ref _threadCounter) * 1_000_003)
                : Environment.TickCount ^ Thread.CurrentThread.ManagedThreadId;
            return new Random(seed);
        },
        trackAllValues: false
    );

    public static void Initialize(int? masterSeed)
    {
        _masterSeed = masterSeed;
        _threadCounter = 0;
    }

    /// <summary>Отримує екземпляр Random поточного потоку. Після ініціалізації пам'ять не виділяється.</summary>
    public static Random Current => _threadLocal.Value!;

    /// <summary>
    /// Вибирає напрямок на основі заданих ймовірностей.
    /// Використовує кумулятивний розподіл - O(1) за виклик.
    /// </summary>
    public static Direction NextDirection(
        double probUp, double probDown, double probLeft, double probRight)
    {
        double r = Current.NextDouble();
        if (r < probUp) return Direction.Up;
        if (r < probUp + probDown) return Direction.Down;
        if (r < probUp + probDown + probLeft) return Direction.Left;
        if (r < probUp + probDown + probLeft + probRight) return Direction.Right;
        return Direction.Stay;
    }
}
