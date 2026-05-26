# Brownian Motion Parallel Simulator
Це високонавантажений симулятор броунівського руху, розроблений на C#/.NET. Проєкт пройшов шлях від простої демонстрації до інженерного рішення, що ефективно використовує багатоядерні процесори для обробки мільйонів операцій зі станом на секунду.

## Особливість рішень проєкту для максимальної ефективності:
 - Zero-Lock Architecture: Замість повільних блокувань (lock) ми використовуємо атомарні інструкції процесора (Interlocked), що дозволяє уникнути перемикання контексту.
 - Збереження маси: Система гарантує, що жодна частинка не загубиться навіть при паралельному доступі сотень потоків до однієї комірки пам'яті.
 - Висока продуктивність: Двигун здатний обробляти понад 10 000+ тактів на секунду з 10 000+ частинок, зберігаючи при цьому стабільний FPS візуалізації.
 - Інженерний підхід: Реалізовано подвійну буферизацію для розділення обчислень та рендерингу, щоб інтерфейс не підвисав під час роботи логічного апарату.

## Технічний стек
 - Мова: C# (використання ref для роботи зі структурами, Lock-free примітиви).
 - Багатопотоковість: Task pool, Barrier для синхронізації тактів, ManualResetEventSlim для ефективної паузи.
 - Візуалізація: Raylib-cs.
 - ОС: Розроблено та протестовано на Nobara Linux (версія ядра: 6.19.11-201.nobara.fc43.x86_64 (64-бітова)

## Як це працює
 - Розбиття на блоки (Chunking): Ми не створюємо окремий потік для кожної частинки (це вбиває CPU). Натомість ми ділимо частинки на рівні частини між доступними ядрами процесора.
 - Синхронізація через Barrier: Всі потоки працюють незалежно, але синхронізуються на межі кожного такту. Це дає змогу робити знімки стану системи для візуалізації без зупинки обчислень.
 - Атомарність: Використання інструкцій рівня процесора для оновлення сітки гарантує цілісність даних без «гонок потоків».

---

## Quick Start

```bash
# Prerequisites: .NET 10 SDK
# Fedora/Nobara:
sudo dnf install dotnet-sdk-10.0

# Білд
cd BrownianMotion
dotnet build -c Release

# Запуск з візуалізацією (HighPerformance mode за замовчуванням)
dotnet run --project src/BrownianMotion.Simulator -c Release

# Запуск бенчмарку (без UI)
dotnet run --project src/BrownianMotion.Simulator -c Release -- --benchmark

# Порівняння всіх режимів
dotnet run --project src/BrownianMotion.Simulator -c Release -- --mode unsafe --no-vis
dotnet run --project src/BrownianMotion.Simulator -c Release -- --mode lock   --no-vis
dotnet run --project src/BrownianMotion.Simulator -c Release -- --mode highperf
```

---

## Command-Line Reference

| Flag | Default | Description |
|------|---------|-------------|
| `--mode unsafe\|lock\|highperf` | `highperf` | Simulation synchronization mode |
| `--particles N` | `10000` | Number of particles (K) |
| `--grid-w N` | `120` | Grid width |
| `--grid-h N` | `80` | Grid height |
| `--workers N` | `CPU cores` | Worker thread/task count |
| `--seed N` | `42` | RNG seed for reproducibility |
| `--cell-size N` | `8` | Pixels per grid cell |
| `--benchmark` | off | Run perf comparison, exit |
| `--no-vis` | off | Headless/console-only mode |
| `--deadlock-demo` | off | Show deadlock scenario + fix |

### Приклади

```bash
# 100k часточок, всі ядра CPU, без UI
dotnet run --project src/BrownianMotion.Simulator -c Release -- \
  --mode highperf --particles 100000 --no-vis

# Демонстрація гонитви
dotnet run --project src/BrownianMotion.Simulator -c Release -- \
  --mode unsafe --particles 20000 --workers 8

# Повторюваний запуск з фіксованим початковим значенням
dotnet run --project src/BrownianMotion.Simulator -c Release -- \
  --seed 9999 --particles 5000

# Deadlock demo з подальшим продовженням симуляції
dotnet run --project src/BrownianMotion.Simulator -c Release -- \
  --deadlock-demo --continue
```

---

## Architecture

```
BrownianMotion.Simulator/
├── Core/
│   ├── SimulationConfig.cs       # All tunable parameters
│   ├── Particle.cs               # Lightweight struct (ID, X, Y)
│   ├── Grid.cs                   # Flat int[] grid, unsafe/locked/atomic moves
│   ├── DoubleBufferSnapshot.cs   # Lock-free front/back buffer for renderer
│   ├── RngFactory.cs             # ThreadLocal<Random>, seeded per-thread
│   └── SimulationStats.cs        # TPS, drift, validation counters
│
├── Concurrency/
│   ├── UnsafeSimulationEngine.cs       # Phase 2: Race conditions
│   ├── LockBasedSimulationEngine.cs    # Phase 3A: Deadlock demo + fix
│   └── HighPerformanceSimulationEngine.cs  # Phase 3B+4: Interlocked + worker pool
│
├── Visualization/
│   ├── SimulationRenderer.cs     # Raylib-cs render loop (decoupled FPS)
│   └── ColorMap.cs               # Density → heat-map color
│
├── Benchmarks/
│   └── PerformanceBenchmark.cs   # 1T/particle vs worker pool vs Parallel.For
│
└── Program.cs                    # CLI entry point
```

---

## Досягнуті навчальні цілі

### Перша фаза - Grid & Particles
- `Grid`: плаский `int[]` масив для локальності кешу (`y * Width + x` індексація)
- `Particle`: `struct` - для частинок використовуються структури замість класів, за рахунок чого ми захистились від виділення пам'яті з "купи" в циклі симуляції
- Reflection boundary: `Math.Clamp` утримує частинки в межах сітки

### Друга фаза - Race Condition (Unsafe Mode)
```csharp
// В UnsafeSimulationEngine - навмисно допущена помилка:
_cells[fromIdx]--;  // Потік A і B читають _cells[fromIdx] = 1
_cells[toIdx]++;    // Обидва змншують значення до 0 і одна частинка "зникає"
```
Для запуску використовуйте прапорець `--mode unsafe` і спостерігайте за ΣCells

### Фаза 3A - Deadlock & Resource Ordering (Lock Mode)
**Deadlock:**
```
Потік A: lock(cell[i]) -> очікує lock(cell[j])
Потік B: lock(cell[j]) -> очікує lock(cell[i])
# Циклічнеочікування - обидва потоки заблоковані назавжди
```
**Fix (Впорядкування за Дейкстрою):**
```csharp
// Завжди спочатку отримуємо блокування з меншим індексом - циклічне очікування неможливе
int first  = Math.Min(fromIdx, toIdx);
int second = Math.Max(fromIdx, toIdx);
lock(_cellLocks[first])
lock(_cellLocks[second])
{ ... }
```

### Фаза 3B - High-Performance атомарний режим
```csharp
// По одній інструкції процесора - LOCK XADD
Interlocked.Decrement(ref _cells[fromIdx]);
Interlocked.Increment(ref _cells[toIdx]);
```
Без блокування ресурсів, без переходів ядра. Масштабується лінійно з кількістю ядер.

### Фаза 4 - Worker Pool (Chunking)
Наївний підхід (1 потік на частинку): 
  -> 10 000 потоків -> ~10 ГБ RAM, що призводить до зависання планувальника ОС через надмірне перемикання контексту.

Оптимізований Worker Pool (10 000 частинок / 16 ядер = 625 частинок/воркер): 
  -> 16 потоків -> ефективне використання кешу CPU, мінімальні накладні витрати на синхронізацію.

Синхронізація воркерів на межі кожного такту (tick boundary) забезпечується через `System.Threading.Barrier`, що гарантує цілісність даних та атомарну узгодженість стану системи.

### Фаза 5 - візуалізація з подвійною буферизацією
```
Потік симуляції:  пише в активний Grid -> копіює в задній буфер -> перемикає вказівники
Потік рендеру:    читає з попереднього буфера отримуючи останній повний снепшот
```
Жоден із потоків не блокує інший. Єдиним механізмом синхронізації є атомарна заміна вказівника (`Interlocked pointer swap`).

### Відтворюваність - ThreadLocal RNG
```csharp
// Кожен потік отримує власний екземпляр Random з детермінованим сід-значенням:
seed = masterSeed ^ (threadIndex * 1_000_003)
```
Відсутність спільного стану в критичних ділянках коду (hot path) забезпечує нульову конкуренцію за ресурси генератора випадкових чисел (RNG).

---

## Орієнтовна продуктивність

Тестування продуктивності на системі з 12 ядрами та 10 000 частинок:

| Стратегія | Потоки | TPS (approx) | Moves/s | Примітки |
|----------|---------|--------------|-------|-------|
| WorkerPool | 1 | ~2,350 | ~23.7M | Базовий показник |
| WorkerPool | 2 | ~4,050 | ~40.5M | Масштабування майже лінійне |
| WorkerPool | 6 | ~8,850 | ~88.3M | Оптимальна ефективність |
| WorkerPool | 12 | ~10,550 | ~105.7M | Падіння через конкуренцію за кеш/шину |
| Parallel.For | auto (12) | ~2,050 | ~20.4M | Високі накладні витрати на партиціонування |
| 1T/Particle | 1,000p | ~21 | ~21343 | Екстремальні витрати на створення потоків |

Для оцінки продуктивності на вашій системі запустить проєкт з прапорцем `--benchmark` 



## Верифікація збереження маси

Кожен тік (snapshot) виконується перевірка:
```csharp
long sum = 0;
for (int i = 0; i < _cells.Length; i++)
    sum += Volatile.Read(ref _cells[i]);

bool conserved = (sum == K_initial);
```
- **HighPerf mode**: сума завжди дорівнює K (гарантується за допомогою Interlocked).
- **Lock mode**: сума дорівнює K (блокування гарантують атомарність пари інкремент/декремент).
- **Unsafe mode**: сума змінюється непередбачувано (навмисна демонстрація проблеми).

---
