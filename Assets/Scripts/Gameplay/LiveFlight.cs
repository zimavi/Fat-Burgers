using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

// The rocket in flight. Steps the same FlightStep as baking. It differs by seeded disturbances (none when ideal),
// by failures rolled at launch, by damage taken in flight, and by the player's actions: staging, the drogue, and probe separation.
// The planned profile (Nominal) stages at booster burnout and opens the drogue at apogee. The actual flight (Actual) does those only when the player does.
// Sample i is at time i * Dt, the same index as the baked list, so deviation is a direct comparison. Plain C#, no Unity objects.
public sealed class LiveFlight : IDisposable
{
    public const float BatteryPerSecond = 0.1f;  // % per s, about 17 minutes from full

    public TrajectoryParams Nominal;             // planned model: used for prediction
    public TrajectoryParams Actual;              // nominal plus disturbances and damage, with the player's actions: what the rocket really does
    public TrajectoryState State;
    public NativeArray<float2> Pitch;            // (time s, pitch rad) x2, owned by the caller and edited in flight
    public NativeList<TrajectorySample> Samples; // one per step, from launch
    public float Battery = 100f;                 // %
    public bool Landed;                          // the flight is over: impact, breakup, or the sample cap
    public bool Broken;                          // the airframe broke up
    public readonly int Failed;                  // (1 << SpecialModuleType) bits of modules that failed their roll this flight
    public int Damaged;                          // (1 << SpecialModuleType) bits of modules torn off or destroyed in flight
    public readonly List<string> Log = new List<string>();  // what happened in flight, for the status page and the result
    readonly RocketData rocket;

    public float Time => State.Time;
    public TrajectorySample Latest => Samples[Samples.Length - 1];

    public bool Staged => (State.Flags & FlightFlags.Separated) != 0;
    public bool ProbeFree => (State.Flags & FlightFlags.ProbeFree) != 0;
    public bool DrogueOut => (State.Flags & FlightFlags.DrogueOn) != 0;
    public bool CanStage => Actual.DecouplerMass > 0f && !Staged;
    public bool CanDeployDrogue => Actual.DrogueCdA > 0f && !DrogueOut && Descending;
    bool Descending => math.dot(State.Vel, State.Pos / math.length(State.Pos)) < 0f;

    public LiveFlight(RocketData rocket, TrajectoryParams nominal, TrajectoryState start, NativeArray<float2> pitch, int seed, bool ideal, int failed = 0)
    {
        this.rocket = rocket;
        Nominal = nominal;
        Actual = nominal;
        Actual.AutoStage = false;  // the player stages, deploys the drogue and separates the probe
        Actual.AutoDrogue = false;
        State = start;
        Pitch = pitch;
        Failed = failed;
        if (!ideal)
        {
            // Seeded, so the same seed flies the same way every time.
            var rng = new System.Random(seed);
            Actual.Thrust *= 1f + Spread(rng, 0.04f);
            Actual.DragCdA *= 1f + Spread(rng, 0.10f);
        }
        ApplyFailures(failed);
        Samples = new NativeList<TrajectorySample>(4096, Allocator.Persistent);
        Samples.Add(FlightStep.Sample(State, Actual, PitchProgram.At(Pitch, State.Time), FlightStep.ThrustOn(State, Actual)));
    }

    static float Spread(System.Random rng, float amount) => (float)(rng.NextDouble() * 2.0 - 1.0) * amount;

    // A failed or destroyed module does nothing in the actual flight. The nominal model still assumes it works, so deviation shows it.
    // Drag index = Separated | Jettisoned: a module that never drops leaves its state's drag the same as the one before it.
    void ApplyFailures(int failed)
    {
        if ((failed & (1 << (int)SpecialModuleType.Parachute)) != 0) { Actual.ChuteCdA = 0f; Actual.DrogueCdA = 0f; }
        if ((failed & (1 << (int)SpecialModuleType.HeatShield)) != 0) { Actual.ShieldCdA = 0f; Actual.HeatScale = 1f; }
        if ((failed & (1 << (int)SpecialModuleType.Decoupler)) != 0)
        {
            Actual.DecouplerMass = 0f;
            Actual.DragCdA[1] = Actual.DragCdA[0];
            Actual.DragCdA[3] = Actual.DragCdA[2];
        }
        if ((failed & (1 << (int)SpecialModuleType.Fairing)) != 0)
        {
            Actual.FairingMass = 0f;
            Actual.DragCdA[2] = Actual.DragCdA[0];
            Actual.DragCdA[3] = Actual.DragCdA[1];
        }
    }

    // One fixed step, recorded. Mirrors TrajectoryJob.Execute: step, sample, then check for impact.
    // Damage is checked after the sample, so the sample that shows it is the one that is logged.
    public void Step()
    {
        if (Landed) return;
        float pitch = PitchProgram.At(Pitch, State.Time);
        byte before = State.Flags;
        bool thrustOn = FlightStep.Step(ref State, Actual, pitch, TrajectoryJob.Dt);
        Samples.Add(FlightStep.Sample(State, Actual, pitch, thrustOn));
        Battery = math.max(0f, Battery - BatteryPerSecond * TrajectoryJob.Dt);

        var c = Latest;
        if (rocket.maxQ > 0f && c.Q > rocket.maxQ)
        {
            Broken = true;
            Landed = true;
            Log.Add($"T+{c.Time:0.0} s  airframe broke up at {c.Altitude / 1000f:0.0} km: {c.Q / 1000f:0} kPa over the {rocket.maxQ / 1000f:0} kPa limit");
            return;
        }

        // A canopy that opens into more shock than it can take tears.
        bool opened = (State.Flags & FlightFlags.ChuteOn) != 0 && (before & FlightFlags.ChuteOn) == 0;
        if (opened && rocket.maxChuteQ > 0f && c.Q > rocket.maxChuteQ)
            Disable(SpecialModuleType.Parachute, $"tore on opening: {c.Q / 1000f:0} kPa shock over the {rocket.maxChuteQ / 1000f:0} kPa limit");

        foreach (var lim in rocket.partLimits)
        {
            int bit = 1 << (int)lim.type;
            if ((Damaged & bit) != 0 || (Failed & bit) != 0) continue;
            if (lim.type == SpecialModuleType.Fairing && (State.Flags & FlightFlags.Jettisoned) != 0) continue;  // already gone
            if (lim.maxQ > 0f && c.Q > lim.maxQ)
                Disable(lim.type, $"torn off: {c.Q / 1000f:0} kPa over its {lim.maxQ / 1000f:0} kPa limit");
            else if (lim.maxHeating > 0f && c.Heating > lim.maxHeating)
                Disable(lim.type, lim.type == SpecialModuleType.HeatShield
                    ? $"burned through: heating {c.Heating:0.0e0} over its {lim.maxHeating:0.0e0} limit"
                    : $"destroyed by heating: {c.Heating:0.0e0} over its {lim.maxHeating:0.0e0} limit");
        }

        float r = math.length(State.Pos);
        bool impact = r - Actual.PlanetRadius <= 0f && math.dot(State.Vel, State.Pos / r) < 0f;
        if (impact || Samples.Length > TrajectoryJob.MaxSteps) Landed = true;
    }

    // Torn off or destroyed: the module stops working for the rest of the flight, the same way a failure does.
    void Disable(SpecialModuleType t, string what)
    {
        int bit = 1 << (int)t;
        Damaged |= bit;
        ApplyFailures(bit);
        Log.Add($"T+{State.Time:0.0} s  {ReliabilityStore.DisplayName(t)} {what}");
    }

    // The player's STAGE. The decoupler fires: the booster is dropped, and a sustainer engine takes over if there is one.
    public void Stage()
    {
        if (!CanStage) return;
        FlightStep.Stage(ref State, Actual);
        string after = !Actual.Handover ? ": the booster had no engine, so only its mass goes"
            : Actual.UpperThrust > 0f ? ": sustainer lit" : ": no sustainer engine, the rocket coasts";
        Log.Add($"T+{State.Time:0.0} s  stage separated{after}");
    }

    // The player's DEPLOY DROGUE, once the rocket is falling. Released when the main canopy opens.
    public void DeployDrogue()
    {
        if (!CanDeployDrogue) return;
        FlightStep.DeployDrogue(ref State, Actual);
        Log.Add($"T+{State.Time:0.0} s  drogue deployed");
    }

    // DEPLOY PAYLOAD: the fairing drops, the rocket body falls away, and the probe flies on alone with its own parachutes.
    // probe is FlightSetup.Probe for this rocket. The probe keeps the chute altitude the player chose.
    public void SeparateProbe(TrajectoryParams probe)
    {
        if (ProbeFree) return;
        State.Flags |= FlightFlags.Jettisoned;
        State.Flags |= FlightFlags.ProbeFree;
        State.Fuel = 0f;
        State.UpperFuel = 0f;
        probe.ChuteDeployAltitude = Actual.ChuteDeployAltitude;
        Nominal = probe;
        Actual = probe;
        ApplyFailures(Failed | Damaged);  // modules that failed or were lost on the rocket are lost on the probe too
        Log.Add($"T+{State.Time:0.0} s  fairing dropped, probe separated from the rocket; the probe flies on alone");
    }

    // Where the rocket goes from here if nothing changes: the planned profile from the current state. Caller disposes the list.
    public NativeList<TrajectorySample> Predict()
    {
        var future = new NativeList<TrajectorySample>(4096, Allocator.Persistent);
        new TrajectoryJob { P = Nominal, Pitch = Pitch, Start = State, Samples = future }.Schedule().Complete();
        return future;
    }

    // Chosen by the player. Locked once the canopy is open.
    public void SetChuteAltitude(float altitude)
    {
        if ((State.Flags & FlightFlags.ChuteOn) != 0) return;
        Nominal.ChuteDeployAltitude = altitude;
        Actual.ChuteDeployAltitude = altitude;
    }

    // Distance in metres between the flown and the baked path at the same mission time.
    public static float Deviation(NativeList<TrajectorySample> baked, NativeList<TrajectorySample> flown)
    {
        int i = math.min(flown.Length, baked.Length) - 1;
        if (i < 0) return 0f;
        float dr = flown[i].Downrange - baked[i].Downrange;
        float da = flown[i].Altitude - baked[i].Altitude;
        return math.length(new float2(dr, da));
    }

    public void Dispose()
    {
        if (Samples.IsCreated) Samples.Dispose();
    }

    // An ideal flight must match the baked job step for step, and the same seed must fly the same way twice.
    public static bool SelfCheck()
    {
        var p = new TrajectoryParams
        {
            DryMass = 40f, Fuel0 = 60f, Thrust = 5000f, Isp = 200f, DragCdA = new float4(0.5f, 0.4f, 0.5f, 0.4f),
            G0 = 9.80665f, PlanetRadius = 6371000f, Rho0 = 1.225f, ScaleHeight = 8500f, Omega = 7.2921159e-5f,
        };
        var rocket = ScriptableObject.CreateInstance<RocketData>();
        rocket.maxQ = 0f;  // no breakup in the check, so the two flights are compared over the same steps
        var start = FlightSetup.Pad(p);
        var pitch = new NativeArray<float2>(2, Allocator.TempJob);
        pitch[0] = new float2(0f, math.PI * 0.5f);
        pitch[1] = new float2(20f, math.radians(60f));

        var baked = new NativeList<TrajectorySample>(4096, Allocator.TempJob);
        new TrajectoryJob { P = p, Pitch = pitch, Start = start, Samples = baked }.Schedule().Complete();

        var ideal = new LiveFlight(rocket, p, start, pitch, 1, true);
        for (int i = 1; i < baked.Length; i++) ideal.Step();
        var last = baked[baked.Length - 1];
        bool matches = ideal.Samples.Length == baked.Length
            && math.abs(ideal.Latest.Altitude - last.Altitude) < 1f
            && math.abs(ideal.Latest.Downrange - last.Downrange) < 1f;

        var a = new LiveFlight(rocket, p, start, pitch, 7, false);
        var b = new LiveFlight(rocket, p, start, pitch, 7, false);
        for (int i = 1; i < baked.Length; i++) { a.Step(); b.Step(); }
        bool repeats = a.Latest.Altitude == b.Latest.Altitude && a.Latest.Downrange == b.Latest.Downrange;

        ideal.Dispose(); a.Dispose(); b.Dispose();
        baked.Dispose();
        pitch.Dispose();
        UnityEngine.Object.DestroyImmediate(rocket);
        return matches && repeats;
    }
}
