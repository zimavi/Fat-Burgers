using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

// Spherical planet, 2D plane through its centre. Units: m, s, kg, N.
// Pos/Vel are planet-centred (inertial). Altitude = |Pos| - R. Downrange = ground track: R * (angle - Omega * t), so
// eastward is positive and a flight from the rotating pad bends west. Launch at the equator; the pad's rotation is in Pad().
// Pitch is measured from the local horizontal: 90 deg = radially up, 0 deg = downrange.
// FlightStep is the only physics step: baking and live prediction both use it.

public struct TrajectoryParams
{
    public float DryMass;                                 // kg
    public float Thrust;                                  // N, burns until propellant is gone
    public float Isp;                                     // s
    public float Cd, Area;                                // drag coefficient, reference area (m^2)
    public float G0, PlanetRadius, Rho0, ScaleHeight;     // Earth: 9.80665, 6371000 m, 1.225 kg/m^3, 8500 m
    public float Omega;                                   // rad/s, planet rotation (Earth: 7.2921159e-5)
}

public struct TrajectoryState
{
    public float2 Pos, Vel;  // m, m/s, planet-centred
    public float Fuel;       // kg
    public float Time;       // s, mission elapsed time
}

public struct TrajectorySample
{
    public float Time, Altitude, Downrange, Speed, VerticalVel, HorizontalVel, Mass, Fuel, Pitch;
    public float Q, Heating;  // Pa, and heating index sqrt(rho) * v^3 (relative only, not a temperature)
    public bool ThrustOn;
}

public static class PitchProgram
{
    public const float Vertical = math.PI * 0.5f;

    // pitch = (time s, pitch rad) pairs sorted by time. Vertical before the first entry, linear between, last value held.
    public static float At(in NativeArray<float2> list, float t)
    {
        if (list.Length == 0 || t < list[0].x) return Vertical;
        int last = list.Length - 1;
        if (t >= list[last].x) return list[last].y;

        int i = 0;
        while (list[i + 1].x <= t) i++;
        float f = (t - list[i].x) / (list[i + 1].x - list[i].x);
        return math.lerp(list[i].y, list[i + 1].y, f);
    }
}

public static class FlightStep
{
    public static bool ThrustOn(in TrajectoryState s, in TrajectoryParams p) => s.Fuel > 0f && p.Thrust > 0f;

    // Exponential atmosphere, shared by the step and the sample output.
    public static float Density(float altitude, in TrajectoryParams p) =>
        p.Rho0 * math.exp(-math.max(altitude, 0f) / p.ScaleHeight);

    // Air moves with the planet: at pos it has velocity Omega x pos (east at the pad). Drag and dynamic pressure use the air-relative velocity.
    public static float2 AirVelocity(in float2 pos, in TrajectoryParams p) => p.Omega * new float2(pos.y, -pos.x);

    // Semi-implicit Euler. Returns whether the engine produced thrust during this step.
    public static bool Step(ref TrajectoryState s, in TrajectoryParams p, float pitchRad, float dt)
    {
        float r = math.length(s.Pos);
        float2 up = s.Pos / r;                     // radial unit vector
        float2 downrange = new float2(up.y, -up.x); // local horizontal, toward downrange
        float mass = p.DryMass + s.Fuel;
        float rho = Density(r - p.PlanetRadius, p);
        float2 relVel = s.Vel - AirVelocity(s.Pos, p);
        float speed = math.length(relVel);

        float2 acc = -p.G0 * p.PlanetRadius * p.PlanetRadius / (r * r) * up;     // radial gravity, inverse square
        acc -= 0.5f * rho * speed * p.Cd * p.Area / mass * relVel;              // drag opposes velocity relative to air

        bool thrustOn = ThrustOn(s, p);
        float burn = 0f;
        if (thrustOn)
        {
            acc += p.Thrust / mass * (math.cos(pitchRad) * downrange + math.sin(pitchRad) * up);
            burn = math.min(p.Thrust / (p.Isp * p.G0) * dt, s.Fuel);  // mdot = T / (Isp * g0)
        }

        s.Vel += acc * dt;
        s.Pos += s.Vel * dt;
        s.Fuel -= burn;
        s.Time += dt;
        return thrustOn;
    }
}

// Shared launch setup, so baking, the turn solver and any live prediction start the same way.
public static class FlightSetup
{
    public static TrajectoryParams Params(RocketData rocket, PlanetData planet) => new TrajectoryParams
    {
        DryMass = rocket.dryMass, Thrust = rocket.thrust, Isp = rocket.isp,
        Cd = rocket.dragCoefficient, Area = rocket.referenceArea,
        G0 = planet.g0, PlanetRadius = planet.radius, Rho0 = planet.seaLevelDensity, ScaleHeight = planet.scaleHeight,
        Omega = planet.angularVelocity,
    };

    // The pad already moves with the planet's rotation, eastward (+x, the downrange direction there).
    // Without that velocity a vertical launch would be a straight line; with it the flight is a parabola that bends west.
    public static TrajectoryState Pad(in TrajectoryParams p, float fuel) => new TrajectoryState
    {
        Pos = new float2(0f, p.PlanetRadius),
        Vel = new float2(p.Omega * p.PlanetRadius, 0f),
        Fuel = fuel,
        Time = 0f,
    };
}

[BurstCompile]
public struct TrajectoryJob : IJob
{
    public const float Dt = 0.01f;
    public const int MaxSteps = 360000;  // 3600 s cap

    [ReadOnly] public TrajectoryParams P;
    [ReadOnly] public NativeArray<float2> Pitch;  // (time s, pitch rad), sorted by time
    public TrajectoryState Start;
    public NativeList<TrajectorySample> Samples;  // caller creates and disposes

    public void Execute()
    {
        var s = Start;
        Samples.Add(ToSample(s, PitchProgram.At(Pitch, s.Time), FlightStep.ThrustOn(s, P)));
        for (int i = 0; i < MaxSteps; i++)
        {
            float pitch = PitchProgram.At(Pitch, s.Time);
            bool thrustOn = FlightStep.Step(ref s, P, pitch, Dt);
            Samples.Add(ToSample(s, pitch, thrustOn));

            float r = math.length(s.Pos);
            float2 up = s.Pos / r;
            if (r - P.PlanetRadius <= 0f && math.dot(s.Vel, up) < 0f) break;  // impact; pad counts as ground
        }
    }

    TrajectorySample ToSample(in TrajectoryState s, float pitch, bool thrustOn)
    {
        float r = math.length(s.Pos);
        float2 up = s.Pos / r;
        float2 downrange = new float2(up.y, -up.x);
        float altitude = r - P.PlanetRadius;
        float speed = math.length(s.Vel);
        float rho = FlightStep.Density(altitude, P);
        float airSpeed = math.length(s.Vel - FlightStep.AirVelocity(s.Pos, P));
        return new TrajectorySample
        {
            Time = s.Time,
            Altitude = altitude,
            Downrange = P.PlanetRadius * (math.atan2(s.Pos.x, s.Pos.y) - P.Omega * s.Time),  // ground track, Earth-fixed
            Speed = speed,
            VerticalVel = math.dot(s.Vel, up),
            HorizontalVel = math.dot(s.Vel, downrange),
            Mass = P.DryMass + s.Fuel,
            Fuel = s.Fuel,
            Pitch = pitch,
            Q = 0.5f * rho * airSpeed * airSpeed,
            Heating = math.sqrt(rho) * airSpeed * airSpeed * airSpeed,
            ThrustOn = thrustOn,
        };
    }

    // Usage (baking): var samples = new NativeList<TrajectorySample>(1024, Allocator.TempJob);
    //   new TrajectoryJob { P = p, Pitch = pitch, Start = st, Samples = samples }.Schedule().Complete();
    //   ... read samples ...; samples.Dispose();
    // Live prediction: same call with Start = a copy of the current state. Never write back to the live state.

    // Drag-free, thrust-free vertical throw from the pad: apex must equal v^2 / (2 g0).
    public static bool SelfCheck()
    {
        var p = new TrajectoryParams { DryMass = 100f, Thrust = 0f, Isp = 200f, Cd = 0f, Area = 1f, G0 = 9.80665f, PlanetRadius = 6371000f, Rho0 = 1.225f, ScaleHeight = 8500f };
        var start = new TrajectoryState { Pos = new float2(0f, p.PlanetRadius), Vel = new float2(0f, 100f) };
        var pitch = new NativeArray<float2>(0, Allocator.Temp);
        var samples = new NativeList<TrajectorySample>(1024, Allocator.TempJob);
        new TrajectoryJob { P = p, Pitch = pitch, Start = start, Samples = samples }.Run();

        float apex = 0f;
        for (int i = 0; i < samples.Length; i++) apex = math.max(apex, samples[i].Altitude);
        samples.Dispose();

        float expected = 100f * 100f / (2f * p.G0);
        return math.abs(apex - expected) < 0.01f * expected;
    }
}
