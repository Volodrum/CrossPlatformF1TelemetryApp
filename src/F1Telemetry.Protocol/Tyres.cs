namespace F1Telemetry.Protocol;

/// <summary>Per-wheel values. The wire order is always RL, RR, FL, FR.</summary>
public readonly record struct Tyres<T>(T RearLeft, T RearRight, T FrontLeft, T FrontRight)
{
    public IEnumerable<(Wheel Wheel, T Value)> Enumerate()
    {
        yield return (Wheel.FrontLeft, FrontLeft);
        yield return (Wheel.FrontRight, FrontRight);
        yield return (Wheel.RearLeft, RearLeft);
        yield return (Wheel.RearRight, RearRight);
    }

    public T this[Wheel wheel] => wheel switch
    {
        Wheel.RearLeft => RearLeft,
        Wheel.RearRight => RearRight,
        Wheel.FrontLeft => FrontLeft,
        Wheel.FrontRight => FrontRight,
        _ => throw new ArgumentOutOfRangeException(nameof(wheel)),
    };
}

public enum Wheel
{
    RearLeft,
    RearRight,
    FrontLeft,
    FrontRight,
}
