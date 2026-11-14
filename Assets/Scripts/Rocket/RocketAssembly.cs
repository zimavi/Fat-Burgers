using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// Sums assembled modules into the RocketData that TrajectoryView flies.
// Engines fire together: thrust adds, Isp is thrust-weighted (T / sum(T/Isp)).
// Drag: the rocket has one frontal area (the largest module area), and Cd is the area-weighted mean of the modules.
// So drag = Cd_mean * A_max. Summing every module's Cd*A would count internal parts as extra drag.
public static class RocketAssembly
{
    public static void Apply(IEnumerable<ModuleData> parts, RocketData r)
    {
        float dry = 0f, fuel = 0f, thrust = 0f, flow = 0f, cdArea = 0f, sumArea = 0f, maxArea = 0f;
        foreach (var m in parts)
        {
            dry += m.mass;
            fuel += m.fuelMass;
            thrust += m.thrust;
            if (m.thrust > 0f) flow += m.thrust / m.isp;
            cdArea += m.dragCoefficient * m.referenceArea;
            sumArea += m.referenceArea;
            maxArea = Mathf.Max(maxArea, m.referenceArea);
        }
        r.dryMass = dry;
        r.fuelMass = fuel;
        r.thrust = thrust;
        r.isp = thrust > 0f ? thrust / flow : 1f;  // Isp is unused when thrust is zero
        r.referenceArea = maxArea;
        r.dragCoefficient = sumArea > 0f ? cdArea / sumArea : 0f;
    }

    public static bool SelfCheck()
    {
        var motorA = Make("A", 10f, 0f, 1000f, 250f, 0.5f, 0.04f);
        var motorB = Make("B", 10f, 0f, 1000f, 200f, 0.5f, 0.02f);
        var tank = Make("T", 5f, 40f, 0f, 200f, 0.5f, 0.04f);
        var r = ScriptableObject.CreateInstance<RocketData>();
        Apply(new[] { motorA, motorB, tank }, r);

        float isp = 2000f / (1000f / 250f + 1000f / 200f);  // 222.2 s
        float cd = 0.5f;  // area-weighted mean: (0.02 + 0.01 + 0.02) / 0.10
        bool ok = Near(r.dryMass, 25f) && Near(r.fuelMass, 40f) && Near(r.thrust, 2000f)
                  && Near(r.isp, isp) && Near(r.referenceArea, 0.04f) && Near(r.dragCoefficient, cd);

        // Tree: attach, then remove the subtree; root cannot be removed.
        var root = new PartNode(tank, null);
        var child = root.AddChild(motorA);
        child.AddChild(motorB);
        ok &= root.Walk().Count() == 3;
        child.Remove();
        root.Remove();
        ok &= root.children.Count == 0 && root.Walk().Count() == 1;

        foreach (var o in new[] { motorA, motorB, tank }) Object.DestroyImmediate(o);
        Object.DestroyImmediate(r);
        return ok;
    }

    static ModuleData Make(string name, float mass, float fuel, float thrust, float isp, float cd, float area)
    {
        var m = ScriptableObject.CreateInstance<ModuleData>();
        m.displayName = name; m.mass = mass; m.fuelMass = fuel; m.thrust = thrust;
        m.isp = isp; m.dragCoefficient = cd; m.referenceArea = area;
        return m;
    }

    static bool Near(float a, float b) => Mathf.Abs(a - b) < 1e-3f;
}
