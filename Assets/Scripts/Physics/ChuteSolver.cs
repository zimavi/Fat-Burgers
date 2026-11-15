using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

// Picks the parachute opening altitude for the baked flight. Each candidate is flown with TrajectoryJob, the same step as the flight.
// Best = lowest impact speed among candidates whose opening shock (peak dynamic pressure after deployment) stays under rocket.maxChuteQ.
// If none does, the candidate with the lowest opening shock wins.
// shortcut: fixed grid of candidates over the range, not a refined search. Upgrade if the grid misses the best altitude.
public static class ChuteSolver
{
    const int Candidates = 20;

    public struct Result
    {
        public float Altitude;     // m, deployment altitude
        public float ImpactSpeed;  // m/s, speed at the surface
        public float PeakQ;        // Pa, highest dynamic pressure after deployment
    }

    public static Result Solve(RocketData rocket, PlanetData planet, NativeArray<float2> pitch)
    {
        var p = FlightSetup.Params(rocket, planet);
        var start = FlightSetup.Pad(p, rocket.fuelMass);
        var best = new Result { Altitude = rocket.chuteDeployMin, ImpactSpeed = float.MaxValue, PeakQ = float.MaxValue };
        bool bestOk = false;

        for (int i = 0; i <= Candidates; i++)
        {
            p.ChuteDeployAltitude = math.lerp(rocket.chuteDeployMin, rocket.chuteDeployMax, i / (float)Candidates);
            var r = Fly(p, start, pitch);
            bool ok = r.PeakQ <= rocket.maxChuteQ;
            // Within the Q limit: lower impact speed wins. Over it: lower opening shock wins, and any candidate within the limit beats it.
            bool better = ok ? (!bestOk || r.ImpactSpeed < best.ImpactSpeed) : (!bestOk && r.PeakQ < best.PeakQ);
            if (better) { best = r; bestOk = ok; }
        }
        return best;
    }

    static Result Fly(TrajectoryParams p, TrajectoryState start, NativeArray<float2> pitch)
    {
        var samples = new NativeList<TrajectorySample>(4096, Allocator.TempJob);
        new TrajectoryJob { P = p, Pitch = pitch, Start = start, Samples = samples }.Schedule().Complete();

        var r = new Result { Altitude = p.ChuteDeployAltitude, ImpactSpeed = samples[samples.Length - 1].Speed, PeakQ = 0f };
        for (int i = 0; i < samples.Length; i++)
            if ((samples[i].Flags & FlightFlags.ChuteOn) != 0) r.PeakQ = math.max(r.PeakQ, samples[i].Q);
        samples.Dispose();
        return r;
    }
}
