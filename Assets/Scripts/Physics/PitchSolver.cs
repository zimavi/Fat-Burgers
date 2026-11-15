using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

// Finds the turn that puts apogee as close as possible to a target altitude.
// Pitch program: vertical until turnStart, then a linear turn to turnDeg over TurnDuration seconds, last value held.
// Every candidate is flown with TrajectoryJob, the same step as the real flight and the baked trajectory.
// shortcut: two-parameter search (turn start, turn angle) on a coarse grid then one refinement, not a global optimiser.
public static class PitchSolver
{
    public const float TurnDuration = 0.5f;  // s, from vertical to the turn angle

    public struct Result
    {
        public float TurnStart;  // s
        public float TurnDeg;    // degrees from local horizontal
        public float Apogee;     // m, the apogee this turn produces
    }

    // Turn starts are searched while the engine still burns, since a turn after burnout cannot change apogee.
    public static Result Solve(RocketData rocket, PlanetData planet, float targetAltitude)
    {
        float burn = rocket.thrust > 0f ? rocket.fuelMass * rocket.isp * planet.g0 / rocket.thrust : 0f;
        float maxT = Mathf.Max(burn, 1f);

        var best = new Result { TurnStart = 0f, TurnDeg = 90f, Apogee = Apogee(rocket, planet, 0f, 90f) };
        float bestErr = Mathf.Abs(best.Apogee - targetAltitude);

        void Consider(float t0, float th)
        {
            float apogee = Apogee(rocket, planet, t0, th);
            float err = Mathf.Abs(apogee - targetAltitude);
            if (err < bestErr)
            {
                bestErr = err;
                best = new Result { TurnStart = t0, TurnDeg = th, Apogee = apogee };
            }
        }

        // Coarse grid: 11 turn starts across the burn, 18 turn angles from 0 to 85 degrees.
        for (int i = 0; i <= 10; i++)
            for (float th = 0f; th <= 85f; th += 5f)
                Consider(maxT * i / 10f, th);

        // Refinement: 11 x 11 points around the coarse winner.
        float centreT = best.TurnStart, centreTh = best.TurnDeg;
        float stepT = maxT / 10f, stepTh = 5f;
        for (int i = -5; i <= 5; i++)
            for (int j = -5; j <= 5; j++)
                Consider(Mathf.Clamp(centreT + i * stepT / 5f, 0f, maxT), Mathf.Clamp(centreTh + j * stepTh / 5f, 0f, 89f));

        return best;
    }

    // Pitch program keys for a solved turn: vertical at turnStart, then turning to turnDeg.
    public static RocketData.PitchKey[] Program(float turnStart, float turnDeg) => new[]
    {
        new RocketData.PitchKey { time = turnStart, pitchDeg = 90f },
        new RocketData.PitchKey { time = turnStart + TurnDuration, pitchDeg = turnDeg },
    };

    // Highest altitude of one flight with this turn.
    public static float Apogee(RocketData rocket, PlanetData planet, float turnStart, float turnDeg)
    {
        var p = FlightSetup.Params(rocket, planet);
        var start = FlightSetup.Pad(p);

        var pitch = new NativeArray<float2>(2, Allocator.TempJob);
        pitch[0] = new float2(turnStart, math.radians(90f));
        pitch[1] = new float2(turnStart + TurnDuration, math.radians(turnDeg));
        var samples = new NativeList<TrajectorySample>(4096, Allocator.TempJob);

        new TrajectoryJob { P = p, Pitch = pitch, Start = start, Samples = samples }.Schedule().Complete();

        float apogee = 0f;
        for (int i = 0; i < samples.Length; i++) apogee = math.max(apogee, samples[i].Altitude);
        pitch.Dispose();
        samples.Dispose();
        return apogee;
    }
}
