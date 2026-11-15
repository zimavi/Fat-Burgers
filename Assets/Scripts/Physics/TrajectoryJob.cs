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
    public float DryMass;                                 // kg, everything fitted at launch, dry (booster, sustainer and payload)
    public float Fuel0;                                   // kg, propellant of the first stage at launch
    public float Thrust, Isp;                             // N, s: the first stage (the booster when Handover is set)
    public float UpperFuel0;                              // kg, propellant of the sustainer (second stage) at launch
    public float UpperThrust, UpperIsp;                   // N, s: the sustainer, lit by staging when Handover is set
    public bool Handover;                                 // the booster has engines, so staging hands thrust to the sustainer
    public bool AutoStage;                                // planned profile: stage when the booster is empty
    public bool AutoDrogue;                               // planned profile: deploy the drogue at apogee
    public float4 DragCdA;                                // m^2, base Cd*A per drop state (index = Separated | Jettisoned bits)
    public float G0, PlanetRadius, Rho0, ScaleHeight;     // Earth: 9.80665, 6371000 m, 1.225 kg/m^3, 8500 m
    public float Omega;                                   // rad/s, planet rotation (Earth: 7.2921159e-5)
    public float ChuteCdA, ShieldCdA;                     // m^2, parachute / heat shield drag area, added when deployed
    public float DrogueCdA;                               // m^2, drogue, released when the main canopy opens
    public float ChuteDeployAltitude;                     // m, the main canopy opens on descent below this
    public float HeatScale;                               // heating index multiplier (heat shield)
    public float DecouplerMass, FairingMass;              // kg dropped by staging (the booster) and at 100 km (fairing)
}

public struct TrajectoryState
{
    public float2 Pos, Vel;   // m, m/s, planet-centred
    public float Fuel;        // kg, propellant of the active stage
    public float UpperFuel;   // kg, propellant of the sustainer, held until staging hands over
    public float Time;        // s, mission elapsed time
    public byte Flags;        // FlightFlags bits
}

public struct TrajectorySample
{
    // Speed, VerticalVel, HorizontalVel are air-relative (ground-relative at the surface). Downrange is Earth-fixed.
    public float Time, Altitude, Downrange, Speed, VerticalVel, HorizontalVel, Mass, Fuel, Pitch;
    public float Q, Heating;  // Pa, and heating index sqrt(rho) * v^3 (relative only, not a temperature)
    public bool ThrustOn;
    public byte Flags;  // FlightFlags bits
}

// One-way switches set during the flight. Samples carry them, so event detection finds the transition sample.
public static class FlightFlags
{
    public const byte Separated = 1, Jettisoned = 2, ChuteOn = 4, AboveBoundary = 8, ShieldOn = 16, DrogueOn = 32, ProbeFree = 64;
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
    // After staging with a handover, the sustainer's engine is the one that burns.
    public static float ActiveThrust(in TrajectoryState s, in TrajectoryParams p) =>
        p.Handover && (s.Flags & FlightFlags.Separated) != 0 ? p.UpperThrust : p.Thrust;

    public static float ActiveIsp(in TrajectoryState s, in TrajectoryParams p) =>
        p.Handover && (s.Flags & FlightFlags.Separated) != 0 ? p.UpperIsp : p.Isp;

    public static bool ThrustOn(in TrajectoryState s, in TrajectoryParams p) => s.Fuel > 0f && ActiveThrust(s, p) > 0f;

    // Exponential atmosphere, shared by the step and the sample output.
    public static float Density(float altitude, in TrajectoryParams p) =>
        p.Rho0 * math.exp(-math.max(altitude, 0f) / p.ScaleHeight);

    // Air moves with the planet: at pos it has velocity Omega x pos (east at the pad). Drag and dynamic pressure use the air-relative velocity.
    public static float2 AirVelocity(in float2 pos, in TrajectoryParams p) => p.Omega * new float2(pos.y, -pos.x);

    // Dropped modules leave the vehicle: the booster at staging, the fairing at 100 km or on jettison.
    public static float Mass(in TrajectoryState s, in TrajectoryParams p) =>
        p.DryMass + s.Fuel + s.UpperFuel
        - ((s.Flags & FlightFlags.Separated) != 0 ? p.DecouplerMass : 0f)
        - ((s.Flags & FlightFlags.Jettisoned) != 0 ? p.FairingMass : 0f);

    // The decoupler fires: the booster is dropped. With a handover, the sustainer's propellant and engine take over.
    // Does nothing without a decoupler, or once it has fired (a failed decoupler has zero mass in the actual flight, so it cannot fire).
    public static void Stage(ref TrajectoryState s, in TrajectoryParams p)
    {
        if (p.DecouplerMass <= 0f || (s.Flags & FlightFlags.Separated) != 0) return;
        s.Flags |= FlightFlags.Separated;
        if (p.Handover)
        {
            s.Fuel = s.UpperFuel;
            s.UpperFuel = 0f;
        }
    }

    // The drogue opens while the rocket is descending. Manual unless the planned profile sets AutoDrogue.
    public static void DeployDrogue(ref TrajectoryState s, in TrajectoryParams p)
    {
        float r = math.length(s.Pos);
        if (p.DrogueCdA > 0f && math.dot(s.Vel, s.Pos / r) < 0f) s.Flags |= FlightFlags.DrogueOn;
    }

    // Parachutes and heat shield. The main canopy opens on descent below its altitude; the heat shield takes over below 100 km on the way back.
    static void Deployments(ref TrajectoryState s, in TrajectoryParams p, float altitude, float vertical)
    {
        if (p.AutoDrogue && p.DrogueCdA > 0f && vertical < 0f) s.Flags |= FlightFlags.DrogueOn;
        if (p.ChuteCdA > 0f && vertical < 0f && altitude <= p.ChuteDeployAltitude) s.Flags |= FlightFlags.ChuteOn;
        if (altitude >= FlightEvents.BoundaryAltitude)
        {
            s.Flags |= FlightFlags.AboveBoundary;
            if (p.FairingMass > 0f) s.Flags |= FlightFlags.Jettisoned;
        }
        if ((s.Flags & FlightFlags.AboveBoundary) != 0 && vertical < 0f && altitude < FlightEvents.BoundaryAltitude)
            s.Flags |= FlightFlags.ShieldOn;
    }

    // Semi-implicit Euler. Returns whether the engine produced thrust during this step.
    public static bool Step(ref TrajectoryState s, in TrajectoryParams p, float pitchRad, float dt)
    {
        float r = math.length(s.Pos);
        float2 up = s.Pos / r;                     // radial unit vector
        float2 downrange = new float2(up.y, -up.x); // local horizontal, toward downrange
        float altitude = r - p.PlanetRadius;
        Deployments(ref s, p, altitude, math.dot(s.Vel, up));
        float mass = Mass(s, p);
        float rho = Density(altitude, p);
        float2 relVel = s.Vel - AirVelocity(s.Pos, p);
        float speed = math.length(relVel);
        float cdA = p.DragCdA[s.Flags & (FlightFlags.Separated | FlightFlags.Jettisoned)]
                  + ((s.Flags & FlightFlags.ChuteOn) != 0 ? p.ChuteCdA : (s.Flags & FlightFlags.DrogueOn) != 0 ? p.DrogueCdA : 0f)
                  + ((s.Flags & FlightFlags.ShieldOn) != 0 ? p.ShieldCdA : 0f);

        float2 acc = -p.G0 * p.PlanetRadius * p.PlanetRadius / (r * r) * up;     // radial gravity, inverse square
        acc -= 0.5f * rho * speed * cdA / mass * relVel;                        // drag opposes velocity relative to air

        bool thrustOn = ThrustOn(s, p);
        float burn = 0f;
        if (thrustOn)
        {
            float thrust = ActiveThrust(s, p), isp = ActiveIsp(s, p);
            acc += thrust / mass * (math.cos(pitchRad) * downrange + math.sin(pitchRad) * up);
            burn = math.min(thrust / (isp * p.G0) * dt, s.Fuel);  // mdot = T / (Isp * g0)
        }

        s.Vel += acc * dt;
        s.Pos += s.Vel * dt;
        s.Fuel -= burn;
        s.Time += dt;
        if (p.AutoStage && p.Handover && burn > 0f && s.Fuel <= 0f) Stage(ref s, p);  // planned: stage at booster burnout
        return thrustOn;
    }

    // Telemetry for one state. Shared by baking and the live flight, so both report the same numbers.
    public static TrajectorySample Sample(in TrajectoryState s, in TrajectoryParams p, float pitch, bool thrustOn)
    {
        float r = math.length(s.Pos);
        float2 up = s.Pos / r;
        float2 downrange = new float2(up.y, -up.x);
        float altitude = r - p.PlanetRadius;
        // Air-relative velocity: the air co-rotates with the ground, so at the surface this is the speed over the ground.
        float2 relVel = s.Vel - AirVelocity(s.Pos, p);
        float speed = math.length(relVel);
        float rho = Density(altitude, p);
        return new TrajectorySample
        {
            Time = s.Time,
            Altitude = altitude,
            Downrange = p.PlanetRadius * (math.atan2(s.Pos.x, s.Pos.y) - p.Omega * s.Time),  // ground track, Earth-fixed
            Speed = speed,
            VerticalVel = math.dot(relVel, up),
            HorizontalVel = math.dot(relVel, downrange),
            Mass = Mass(s, p),
            Fuel = s.Fuel + s.UpperFuel,  // total propellant still on board
            Pitch = pitch,
            Q = 0.5f * rho * speed * speed,
            Heating = math.sqrt(rho) * speed * speed * speed * p.HeatScale,
            ThrustOn = thrustOn,
            Flags = s.Flags,
        };
    }
}

// Shared launch setup, so baking, the turn solver and any live prediction start the same way.
public static class FlightSetup
{
    // The planned profile: stages at booster burnout and opens the drogue at apogee.
    public static TrajectoryParams Params(RocketData rocket, PlanetData planet) => new TrajectoryParams
    {
        // Without a handover the whole propellant is the first stage. An unassembled RocketData (no staging fields set) falls back to fuelMass.
        DryMass = rocket.dryMass, Fuel0 = rocket.handover ? rocket.launchFuel : rocket.fuelMass, Thrust = rocket.thrust, Isp = rocket.isp,
        UpperFuel0 = rocket.handover ? rocket.upperFuel : 0f, UpperThrust = rocket.upperThrust, UpperIsp = rocket.upperIsp, Handover = rocket.handover,
        AutoStage = true, AutoDrogue = true,
        DragCdA = new float4(rocket.dragCdA.x, rocket.dragCdA.y, rocket.dragCdA.z, rocket.dragCdA.w),
        G0 = planet.g0, PlanetRadius = planet.radius, Rho0 = planet.seaLevelDensity, ScaleHeight = planet.scaleHeight,
        Omega = planet.angularVelocity,
        ChuteCdA = rocket.chuteCdA, DrogueCdA = rocket.drogueCdA, ShieldCdA = rocket.shieldCdA, ChuteDeployAltitude = rocket.chuteDeployMin,
        HeatScale = rocket.heatScale, DecouplerMass = rocket.decouplerMass, FairingMass = rocket.fairingMass,
    };

    // The probe after it separates from the rocket: payload tree only, no thrust, fairing already gone. Parachutes on it are its own.
    public static TrajectoryParams Probe(RocketData rocket, PlanetData planet, float chuteAltitude) => new TrajectoryParams
    {
        // shortcut: a rocket with no payload tree still gets a 1 kg probe, so the acceleration never divides by zero.
        DryMass = math.max(1f, rocket.probeDryMass), Thrust = 0f, Isp = 1f, UpperThrust = 0f, UpperIsp = 1f, Handover = false,
        AutoStage = false, AutoDrogue = false,
        DragCdA = new float4(rocket.probeDragCdA),
        G0 = planet.g0, PlanetRadius = planet.radius, Rho0 = planet.seaLevelDensity, ScaleHeight = planet.scaleHeight,
        Omega = planet.angularVelocity,
        ChuteCdA = rocket.probeChuteCdA, DrogueCdA = rocket.probeDrogueCdA, ShieldCdA = rocket.probeShieldCdA,
        ChuteDeployAltitude = chuteAltitude, HeatScale = rocket.probeHeatScale,
        DecouplerMass = 0f, FairingMass = 0f,
    };

    // The pad already moves with the planet's rotation, eastward (+x, the downrange direction there).
    // Without that velocity a vertical launch would be a straight line; with it the flight is a parabola that bends west.
    public static TrajectoryState Pad(in TrajectoryParams p) => new TrajectoryState
    {
        Pos = new float2(0f, p.PlanetRadius),
        Vel = new float2(p.Omega * p.PlanetRadius, 0f),
        Fuel = p.Fuel0,
        UpperFuel = p.UpperFuel0,
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

    TrajectorySample ToSample(in TrajectoryState s, float pitch, bool thrustOn) => FlightStep.Sample(s, P, pitch, thrustOn);

    // Usage (baking): var samples = new NativeList<TrajectorySample>(1024, Allocator.TempJob);
    //   new TrajectoryJob { P = p, Pitch = pitch, Start = FlightSetup.Pad(p), Samples = samples }.Schedule().Complete();
    //   ... read samples ...; samples.Dispose();
    // Live prediction: same call with Start = a copy of the current state. Never write back to the live state.

    // Drag-free, thrust-free vertical throw from the pad: apex must equal v^2 / (2 g0).
    public static bool SelfCheck()
    {
        var p = new TrajectoryParams { DryMass = 100f, Thrust = 0f, Isp = 200f, DragCdA = float4.zero, G0 = 9.80665f, PlanetRadius = 6371000f, Rho0 = 1.225f, ScaleHeight = 8500f };
        var start = new TrajectoryState { Pos = new float2(0f, p.PlanetRadius), Vel = new float2(0f, 100f) };
        var pitch = new NativeArray<float2>(0, Allocator.Temp);
        var samples = new NativeList<TrajectorySample>(1024, Allocator.TempJob);
        new TrajectoryJob { P = p, Pitch = pitch, Start = start, Samples = samples }.Run();

        float apex = 0f;
        for (int i = 0; i < samples.Length; i++) apex = math.max(apex, samples[i].Altitude);
        samples.Dispose();

        float expected = 100f * 100f / (2f * p.G0);
        // Once separated, the decoupler's mass is gone: 100 kg dry + 10 kg fuel - 4 kg dropped.
        p.DecouplerMass = 4f;
        bool massOk = math.abs(FlightStep.Mass(new TrajectoryState { Fuel = 10f, Flags = FlightFlags.Separated }, p) - 106f) < 1e-4f;
        return math.abs(apex - expected) < 0.01f * expected && massOk;
    }
}
