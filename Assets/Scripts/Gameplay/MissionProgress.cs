using UnityEngine;

// Mission progress during one flight. Plain C#, stepped once per fixed step after LiveFlight.Step.
// Both mission actions are the player's:
//   TEST MODULE (test flight): once the working module is at the test altitude.
//   RUN EXPERIMENT (science): starts inside the target window and pauses if the rocket leaves it.
// The data goes home by a recovered probe (soft landing after separation) or by transmitting it (needs Advanced Communication, costs battery, takes time).
public sealed class MissionProgress
{
    public const float TransmitCost = 15f;      // % battery
    public const float TransmitSeconds = 10f;   // s of flight
    public const float ExperimentDrain = 0.5f;  // % battery per s while the experiment runs

    public readonly MissionData Mission;
    public readonly float TargetAltitude;       // m, science only
    public float ExperimentTime;                // s of experiment run so far
    public bool Running;                        // the experiment is running now
    public bool TestDone;                       // test flights: the module was tested
    public bool DataCollected, Transmitting, Transmitted;
    float transmitLeft;
    bool moduleWorks;  // test flights: the module under test is fitted and did not fail this flight

    public MissionProgress(MissionData mission, int seed)
    {
        Mission = mission;
        // Its own generator, so the altitude is not the same number as the first disturbance.
        var rng = new System.Random(seed + 1);
        float raw = mission.altitudeMin + (float)rng.NextDouble() * (mission.altitudeMax - mission.altitudeMin);
        TargetAltitude = Mathf.Round(raw / 1000f) * 1000f;  // whole kilometres, so the briefing and the plot agree
    }

    // Called at launch, once the failures for this flight are known.
    public void StartFlight(RocketData rocket, int failed)
    {
        moduleWorks = rocket.Has(Mission.moduleUnderTest) && (failed & (1 << (int)Mission.moduleUnderTest)) == 0;
    }

    public bool InWindow(LiveFlight live) => Mathf.Abs(live.Latest.Altitude - TargetAltitude) <= Mission.experimentWindow;

    public bool CanTest(LiveFlight live) =>
        Mission.kind == MissionKind.TestFlight && moduleWorks && !TestDone && live.Latest.Altitude >= Mission.testAltitude;

    public void Test(LiveFlight live)
    {
        if (!CanTest(live)) return;
        TestDone = true;
        DataCollected = true;
        live.Log.Add($"T+{live.Time:0.0} s  {Mission.moduleUnderTest} tested at {live.Latest.Altitude / 1000f:0} km");
    }

    public bool CanRun(LiveFlight live) =>
        Mission.kind == MissionKind.Science && !DataCollected && live.Battery > 0f && InWindow(live);

    public void ToggleRun(LiveFlight live)
    {
        if (Running) { Running = false; return; }
        if (CanRun(live)) Running = true;
    }

    // Once per fixed step, after LiveFlight.Step.
    public void Step(LiveFlight live)
    {
        if (Transmitting)
        {
            transmitLeft -= TrajectoryJob.Dt;
            if (transmitLeft <= 0f) { Transmitting = false; Transmitted = true; }
        }
        if (!Running) return;
        if (!InWindow(live) || live.Battery <= 0f)
        {
            Running = false;
            live.Log.Add($"T+{live.Time:0.0} s  experiment paused: {(live.Battery <= 0f ? "battery empty" : "outside the target window")}");
            return;
        }
        ExperimentTime += TrajectoryJob.Dt;
        live.Battery = Mathf.Max(0f, live.Battery - ExperimentDrain * TrajectoryJob.Dt);
        if (ExperimentTime >= Mission.experimentSeconds)
        {
            Running = false;
            DataCollected = true;
            live.Log.Add($"T+{live.Time:0.0} s  experiment complete: data collected");
        }
    }

    public bool CanTransmit(LiveFlight live, bool hasComms) =>
        hasComms && DataCollected && !Transmitted && !Transmitting && live.Battery >= TransmitCost;

    public void StartTransmit(LiveFlight live)
    {
        live.Battery -= TransmitCost;
        Transmitting = true;
        transmitLeft = TransmitSeconds;
        live.Log.Add($"T+{live.Time:0.0} s  transmitting the data");
    }

    // The target altitude stays in range for many seeds and is repeatable for one.
    public static bool SelfCheck()
    {
        var m = ScriptableObject.CreateInstance<MissionData>();
        m.kind = MissionKind.Science;
        m.altitudeMin = 105000f;
        m.altitudeMax = 140000f;
        bool ok = true;
        for (int seed = 1; seed <= 50; seed++)
        {
            float t = new MissionProgress(m, seed).TargetAltitude;
            ok &= t >= m.altitudeMin - 500f && t <= m.altitudeMax + 500f;
        }
        ok &= new MissionProgress(m, 7).TargetAltitude == new MissionProgress(m, 7).TargetAltitude;
        Object.DestroyImmediate(m);
        return ok;
    }
}
