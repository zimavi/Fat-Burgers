// Sticky-note advice. One line from the flight state. The first rule that matches wins, so the order is priority.
public struct AdvisorState
{
    public bool Launched, Landed, HasChute, ChuteOn, Ascending;
    public float PredictedApogee, TargetApogee;  // m
    public float ChuteQ, ChuteQLimit;            // Pa, opening shock at the chosen altitude
    public float Deviation;                      // m
    public float Battery;                        // %
    public float ImpactSpeed;                    // m/s, valid once Landed
    public float Q, QLimit;                      // Pa, dynamic pressure now and the airframe limit (0 = no limit)
    public float Altitude, Speed, ChuteAlt;      // m, m/s, and the chute opening altitude in use
    public bool Descending, Broken;
}

public static class AdvisorRules
{
    public const float SoftLanding = 30f;  // m/s

    public static string Pick(in AdvisorState s)
    {
        if (s.Broken) return "The airframe broke up under dynamic pressure. Ease the pitch earlier next time.";
        if (s.Landed)
            return s.ImpactSpeed <= SoftLanding
                ? $"Soft landing at {s.ImpactSpeed:0.0} m/s. The vehicle can be recovered."
                : $"Hard landing at {s.ImpactSpeed:0} m/s. Open the chute lower or earlier.";
        if (!s.Launched) return "Set the turn on the knobs, then LAUNCH. The dotted line is the planned flight.";
        if (s.Ascending && s.PredictedApogee < s.TargetApogee)
            return $"Predicted apogee {s.PredictedApogee / 1000f:0.0} km, short of 100 km. A steeper or earlier turn gains altitude.";
        if (s.QLimit > 0f && s.Q > 0.85f * s.QLimit)
            return $"Dynamic pressure {s.Q / 1000f:0} kPa, near the {s.QLimit / 1000f:0} kPa airframe limit. Ease the pitch.";
        if (s.Descending && s.HasChute && !s.ChuteOn && s.Altitude < s.ChuteAlt + 3000f)
            return $"The canopy opens at {s.ChuteAlt / 1000f:0.0} km and the rocket is at {s.Altitude / 1000f:0.0} km, falling at {s.Speed:0} m/s.";
        if (!s.HasChute) return "No parachute on board. The descent will be fast and the landing hard.";
        if (!s.ChuteOn && s.ChuteQ > s.ChuteQLimit)
            return $"The chute would open with {s.ChuteQ / 1000f:0} kPa at this altitude, over the limit. Open it lower.";
        if (s.Deviation > 5000f)
            return $"Off the planned path by {s.Deviation / 1000f:0.0} km. Check the predicted line before the next change.";
        if (s.Battery < 20f) return $"Battery at {s.Battery:0}%. Power is running out.";
        return "Nominal flight. Set the chute opening altitude on the graph before the descent.";
    }

    public static bool SelfCheck()
    {
        var s = new AdvisorState { Launched = true, Ascending = true, PredictedApogee = 50000f, TargetApogee = 100000f, HasChute = true, ChuteQLimit = 40000f, Battery = 100f };
        bool apogee = Pick(s).StartsWith("Predicted apogee");
        s.PredictedApogee = 120000f;
        s.HasChute = false;
        bool chute = Pick(s).StartsWith("No parachute");
        s.Landed = true;
        s.ImpactSpeed = 10f;
        bool landed = Pick(s).StartsWith("Soft");
        return apogee && chute && landed;
    }
}
