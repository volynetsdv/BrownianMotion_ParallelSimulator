namespace BrownianMotion.Simulator.Core;

/// <summary>
/// Представляє окрему бройівську частинку. Зберігається у вигляді структури для забезпечення локальності кешу.
/// Зберігається у плоскому масиві; для кожної частинки не відбувається виділення пам’яті з купи.
/// </summary>
public struct Particle
{
    public int Id;
    public int X;
    public int Y;

    public Particle(int id, int x, int y)
    {
        Id = id;
        X = x;
        Y = y;
    }

    public override readonly string ToString() => $"Particle#{Id} ({X},{Y})";
}

/// <summary>
/// Чотири основні напрямки, у яких може рухатися частинка.
/// </summary>
public enum Direction { Up, Down, Left, Right, Stay }
