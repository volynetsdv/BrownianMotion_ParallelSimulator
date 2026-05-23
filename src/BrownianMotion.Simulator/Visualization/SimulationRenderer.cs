using Raylib_cs;
using BrownianMotion.Simulator.Core;

namespace BrownianMotion.Simulator.Visualization;

/// <summary>
/// Етап 5: Візуалізація в реальному часі за допомогою Raylib-cs.
/// 
/// Принципи проектування:
/// - Відокремлення від симуляції: рендерер працює з цільовою частотою кадрів, а симуляція - на повній швидкості.
/// - Зчитує тільки з знімка з подвійним буфером: ніколи не торкається активних комірок Grid.
/// - HUD накладається за допомогою вбудованої функції DrawText Raylib - без залежності від зовнішніх шрифтів.
/// 
/// Нюанси на Linux:
/// - Raylib-cs постачається з власною бібліотекою libraylib.so - системна інсталяція не потрібна.
/// - Контекст OpenGL створюється Raylib; працює на X11 та Wayland (через XWayland).
/// - Вікно можна закрити; симуляція зупиняється при закритті.
/// </summary>
public sealed class SimulationRenderer : IDisposable
{
    private readonly SimulationConfig _config;
    private readonly DoubleBufferSnapshot _snapshot;
    private readonly SimulationStats _stats;
    private readonly SimulationMode _mode;

    private readonly int _windowWidth;
    private readonly int _windowHeight;
    private readonly int _cellSize;
    private readonly int _hudHeight = 90;

    // Багаторазовий буфер рендерингу - дозволяє уникнути виділення пам'яті для кожного кадру
    private readonly int[] _renderBuffer;
    private int _peakDensity = 1;
    private int _lastSnapshotGeneration = -1;

    public bool ShouldClose { get; private set; }

    public SimulationRenderer(
        SimulationConfig config,
        DoubleBufferSnapshot snapshot,
        SimulationStats stats,
        SimulationMode mode)
    {
        _config = config;
        _snapshot = snapshot;
        _stats = stats;
        _mode = mode;
        _cellSize = config.CellPixelSize;

        _windowWidth = config.GridWidth * _cellSize;
        _windowHeight = config.GridHeight * _cellSize + _hudHeight;
        _renderBuffer = new int[config.GridWidth * config.GridHeight];
    }

    /// <summary>
    /// Ініціалізує вікно Raylib. Повинно викликатися з лише з головного потоку!!!
    /// Raylib потребує контексту того потоку, який його створив.
    /// </summary>
    public void Initialize()
    {
        Raylib.SetConfigFlags(ConfigFlags.Msaa4xHint);
        Raylib.InitWindow(_windowWidth, _windowHeight, "Brownian Motion Parallel Simulator - .NET 10");
        Raylib.SetTargetFPS(_config.TargetFps);
    }

    /// <summary>
    /// Відтворює один кадр. Повертає значення false, коли вікно має закритися.
    /// Цю функцію можна викликати в головному циклі, але НЕ можна викликати з потоків симуляції.
    /// </summary>
    public bool RenderFrame()
    {
        if (Raylib.WindowShouldClose())
        {
            ShouldClose = true;
            return false;
        }

        // Оновлювати буфер рендерингу лише тоді, коли доступний новий знімок
        int gen = _snapshot.Generation;
        if (gen != _lastSnapshotGeneration)
        {
            _snapshot.ReadFrontAll(_renderBuffer);
            _lastSnapshotGeneration = gen;

            // Оновити пікову щільність для масштабування кольору
            foreach (int v in _renderBuffer)
                if (v > _peakDensity) _peakDensity = v;
        }

        Raylib.BeginDrawing();
        Raylib.ClearBackground(new Color(10, 10, 20, 255));

        DrawGrid();
        DrawHud();

        Raylib.EndDrawing();
        return true;
    }

    private void DrawGrid()
    {
        int w = _config.GridWidth;
        int h = _config.GridHeight;

        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int count = _renderBuffer[y * w + x];
                var color = ColorMap.DensityToColor(count, _peakDensity);
                Raylib.DrawRectangle(x * _cellSize, y * _cellSize, _cellSize, _cellSize, color);
            }

        // Draw subtle grid lines for small cell sizes
        if (_cellSize >= 6)
        {
            var lineColor = new Color(30, 30, 40, 120);
            for (int x = 0; x <= w; x++)
                Raylib.DrawLine(x * _cellSize, 0, x * _cellSize, h * _cellSize, lineColor);
            for (int y = 0; y <= h; y++)
                Raylib.DrawLine(0, y * _cellSize, w * _cellSize, y * _cellSize, lineColor);
        }
    }

    private void DrawHud()
    {
        int hudY = _config.GridHeight * _cellSize;
        int gridW = _config.GridWidth * _cellSize;

        // HUD background
        Raylib.DrawRectangle(0, hudY, gridW, _hudHeight, new Color(8, 8, 18, 245));
        Raylib.DrawLine(0, hudY, gridW, hudY, new Color(60, 60, 100, 255));

        var modeColor = ColorMap.ModeColor(_mode);
        string modeLabel = ColorMap.ModeLabel(_mode);
        long particleSum = _stats.LastParticleSum;
        long drift = particleSum - _config.ParticleCount;

        // Row 1: Mode
        Raylib.DrawText($"Mode: {modeLabel}", 10, hudY + 8, 14, modeColor);
        Raylib.DrawText($"FPS: {Raylib.GetFPS()}", gridW - 90, hudY + 8, 14, Color.Gray);

        // Row 2: Particle counts
        var driftColor = drift == 0 ? Color.Green : (Math.Abs(drift) < 100 ? Color.Yellow : Color.Red);
        Raylib.DrawText($"Particles: {_config.ParticleCount:N0}  Current Σ: {particleSum:N0}", 10, hudY + 26, 14, Color.White);
        Raylib.DrawText($"Drift: {drift:+#;-#;0}", 10 + 320, hudY + 26, 14, driftColor);

        // Row 3: Performance
        Raylib.DrawText($"Tick: {_stats.TotalTicks:N0}  TPS: {_stats.TicksPerSecond:F0}  Workers: {_config.WorkerCount}", 10, hudY + 44, 14, Color.LightGray);

        // Row 4: Validation failures
        long errs = _stats.ValidationFailures;
        var errColor = errs == 0 ? Color.Green : Color.Red;
        Raylib.DrawText($"Validation Failures: {errs}  Peak Density/Cell: {_peakDensity}", 10, hudY + 62, 14, errColor);

        // Row 5: Controls hint
        Raylib.DrawText("Press ESC to exit", 10, hudY + 74, 11, new Color(80, 80, 100, 255));

        // Mode indicator bar on left edge
        Raylib.DrawRectangle(0, hudY, 4, _hudHeight, modeColor);
    }

    public void Dispose()
    {
        if (Raylib.IsWindowReady())
            Raylib.CloseWindow();
    }
}
