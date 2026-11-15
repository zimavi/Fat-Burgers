using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// Sums the assembled trees into the RocketData that TrajectoryView flies.
// Engines fire together: thrust adds, Isp is thrust-weighted (T / sum(T/Isp)).
// Drag: one frontal area (the largest flying module's area), with Cd area-weighted over the flying modules: Cd_mean * A_max.
// Summing every module's Cd*A would count internal parts as extra drag.
// Stowed parachutes and heat shields add no base drag; they add their own Cd*A when deployed (RocketData).
// Decoupler and fairing drops change the drag too, so drag is stored for each drop state.
public static class RocketAssembly
{
    public const float HeatShieldHeatScale = 0.5f;  // shortcut: one flat factor for any heat shield, not per material

    public static void Apply(IEnumerable<PartNode> roots, RocketData r)
    {
        var nodes = roots.SelectMany(n => n.Walk()).ToList();
        float dry = 0f, fuel = 0f, thrust = 0f, flow = 0f, chute = 0f, shield = 0f, fairing = 0f, heat = 1f;
        foreach (var node in nodes)
        {
            var m = node.data;
            dry += m.mass;
            fuel += m.fuelMass;
            thrust += m.thrust;
            if (m.thrust > 0f) flow += m.thrust / m.isp;
            switch (m.specialType)
            {
                case SpecialModuleType.Parachute: chute += m.dragCoefficient * m.referenceArea; break;
                case SpecialModuleType.HeatShield: shield += m.dragCoefficient * m.referenceArea; heat = HeatShieldHeatScale; break;
                case SpecialModuleType.Fairing: fairing += m.mass; break;
            }
        }

        // The decoupler drops the modules attached to it at MECO. Fairings drop at 100 km, so they are not counted here.
        // The decoupler fires when propellant is spent, so the dropped modules carry no fuel.
        var decoupled = new HashSet<PartNode>();
        foreach (var n in nodes.Where(n => n.data.specialType == SpecialModuleType.Decoupler))
            foreach (var c in n.children)
                foreach (var x in c.Walk())
                    if (x.data.specialType != SpecialModuleType.Fairing) decoupled.Add(x);

        var flying = nodes.Where(n => !IsStowed(n.data)).ToList();
        bool IsFairing(PartNode n) => n.data.specialType == SpecialModuleType.Fairing;

        // Index = Separated | Jettisoned bits: none, decoupler gone, fairing gone, both.
        r.dragCdA = new Vector4(
            BaseCdA(flying),
            BaseCdA(flying.Where(n => !decoupled.Contains(n))),
            BaseCdA(flying.Where(n => !IsFairing(n))),
            BaseCdA(flying.Where(n => !decoupled.Contains(n) && !IsFairing(n))));
        r.dryMass = dry;
        r.fuelMass = fuel;
        r.thrust = thrust;
        r.isp = thrust > 0f ? thrust / flow : 1f;  // Isp is unused when thrust is zero
        r.chuteCdA = chute;
        r.shieldCdA = shield;
        r.heatScale = heat;
        r.fairingMass = fairing;
        r.decouplerMass = decoupled.Sum(x => x.data.mass);
    }

    static bool IsStowed(ModuleData m) => m.specialType == SpecialModuleType.Parachute || m.specialType == SpecialModuleType.HeatShield;

    // Cd_mean * A_max over the given nodes: one frontal area, with Cd averaged by area.
    static float BaseCdA(IEnumerable<PartNode> nodes)
    {
        float cdArea = 0f, sumArea = 0f, maxArea = 0f;
        foreach (var n in nodes)
        {
            var m = n.data;
            cdArea += m.dragCoefficient * m.referenceArea;
            sumArea += m.referenceArea;
            maxArea = Mathf.Max(maxArea, m.referenceArea);
        }
        return sumArea > 0f ? cdArea / sumArea * maxArea : 0f;
    }

    public static bool SelfCheck()
    {
        var motorA = Make("A", 10f, 0f, 1000f, 250f, 0.5f, 0.04f);
        var motorB = Make("B", 10f, 0f, 1000f, 200f, 0.5f, 0.02f);
        var tank = Make("T", 5f, 40f, 0f, 200f, 0.5f, 0.04f);
        var chute = Make("P", 3f, 0f, 0f, 200f, 1f, 0.5f);
        chute.specialType = SpecialModuleType.Parachute;
        var decoupler = Make("D", 2f, 0f, 0f, 200f, 0.5f, 0f);
        decoupler.specialType = SpecialModuleType.Decoupler;

        // Tree: base tank with motorA above it, a stowed parachute, and a decoupler carrying motorB.
        var root = new PartNode(tank, null);
        var child = root.AddChild(motorA);
        root.AddChild(chute);
        var dec = root.AddChild(decoupler);
        dec.AddChild(motorB);
        var r = ScriptableObject.CreateInstance<RocketData>();
        Apply(new[] { root }, r);

        float isp = 2000f / (1000f / 250f + 1000f / 200f);  // 222.2 s
        // Dry: 5 + 10 + 10 + 3 + 2 = 30 kg. Flying drag: Cd_mean 0.5 over 0.10 m^2 of flying area, times the largest area 0.04 m^2.
        bool ok = Near(r.dryMass, 30f) && Near(r.fuelMass, 40f) && Near(r.thrust, 2000f) && Near(r.isp, isp)
                  && Near(r.dragCdA.x, 0.02f) && Near(r.chuteCdA, 0.5f) && Near(r.decouplerMass, 10f) && Near(r.fairingMass, 0f);

        // Tree: five nodes. Removing motorA leaves four.
        ok &= root.Walk().Count() == 5;
        child.Remove();
        ok &= root.children.Count == 2 && root.Walk().Count() == 4;
        root.Remove();  // root has no parent, so this does nothing
        ok &= root.Walk().Count() == 4;

        foreach (var o in new[] { motorA, motorB, tank, chute, decoupler }) Object.DestroyImmediate(o);
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
