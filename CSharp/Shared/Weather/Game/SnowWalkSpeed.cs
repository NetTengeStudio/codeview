using System;

namespace ScpItdMod.Weather;

// The walk slowdown from snow resistance, kept free of game types so the regression harness can check it without a running game.
internal static class SnowWalkSpeed
{
    // Share of body weight a leg can thrust with; resistance above that thrust slows the walk in proportion.
    internal const float Thrust = .35f, Gravity = 9.81f, Floor = .15f;

    internal static float Fraction(float resistance, float mass)
    {
        float available = Thrust * mass * Gravity;
        return Math.Clamp(available / Math.Max(available, resistance), Floor, 1);
    }
}
