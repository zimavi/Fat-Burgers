using System;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

// Bakes the trajectory with TrajectoryJob and draws downrange (x) vs altitude (y) with a LineRenderer.
// Re-bakes on Start and whenever the Inspector changes. Simulation stays in SI; only this view converts units.
[RequireComponent(typeof(LineRenderer))]
public class TrajectoryView : MonoBehaviour
{
    public RocketData rocket;
    public PlanetData planet;
    [Tooltip("Meters per world unit. 1000 = 1 unit per km.")]
    public float metersPerUnit = 1000f;

    LineRenderer line;

    void Awake() => line = GetComponent<LineRenderer>();
    void Start() => Bake();
    void OnValidate() => Bake();

    public void Bake()
    {
        if (line == null) line = GetComponent<LineRenderer>();
        if (rocket == null || planet == null) { line.positionCount = 0; return; }

        var keys = (RocketData.PitchKey[])(rocket.pitchProgram ?? new RocketData.PitchKey[0]).Clone();
        Array.Sort(keys, (a, b) => a.time.CompareTo(b.time));
        var pitch = new NativeArray<float2>(keys.Length, Allocator.TempJob);
        for (int i = 0; i < keys.Length; i++) pitch[i] = new float2(keys[i].time, math.radians(keys[i].pitchDeg));

        var p = new TrajectoryParams
        {
            DryMass = rocket.dryMass, Thrust = rocket.thrust, Isp = rocket.isp,
            Cd = rocket.dragCoefficient, Area = rocket.referenceArea,
            G0 = planet.g0, PlanetRadius = planet.radius, Rho0 = planet.seaLevelDensity, ScaleHeight = planet.scaleHeight,
        };
        var start = new TrajectoryState { Pos = new float2(0f, p.PlanetRadius), Vel = float2.zero, Fuel = rocket.fuelMass, Time = 0f };
        var samples = new NativeList<TrajectorySample>(1024, Allocator.TempJob);

        new TrajectoryJob { P = p, Pitch = pitch, Start = start, Samples = samples }.Schedule().Complete();

        var points = new Vector3[samples.Length];
        for (int i = 0; i < points.Length; i++)
            points[i] = new Vector3(samples[i].Downrange, samples[i].Altitude, 0f) / metersPerUnit;
        line.useWorldSpace = false;  // points are local, so the line starts at this object's transform
        line.positionCount = points.Length;
        line.SetPositions(points);

        pitch.Dispose();
        samples.Dispose();
    }
}
