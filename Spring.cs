namespace NotchWin;

internal sealed class Spring
{
    public double Value;
    public double Velocity;
    public double Target;
    public double Stiffness = 280;
    public double Damping = 26;

    public Spring(double value) { Value = Target = value; }

    public void Step(double dt)
    {
        const double maxStep = 1.0 / 240;
        while (dt > 0)
        {
            double h = Math.Min(dt, maxStep);
            double accel = Stiffness * (Target - Value) - Damping * Velocity;
            Velocity += accel * h;
            Value += Velocity * h;
            dt -= h;
        }
    }

    public bool Settled => Math.Abs(Target - Value) < 0.05 && Math.Abs(Velocity) < 0.5;

    public void Snap() { Value = Target; Velocity = 0; }
}
