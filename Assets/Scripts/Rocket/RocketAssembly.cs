using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// Sums the assembled trees into the RocketData that TrajectoryView flies.
// Two stages: the booster is what the decoupler carries (its children). It burns first. Staging drops it, and with a handover
// the sustainer (the rest of the rocket tree) lights. Without engines in the booster there is no handover: staging only drops mass.
// The payload tree is the probe. After it separates, the probe flies on its own (probe* fields).
// Drag: one frontal area (the largest flying module's area), with Cd area-weighted over the flying modules: Cd_mean * A_max.
// Summing every module's Cd*A would count internal parts as extra drag.
// Stowed parachutes and heat shields add no base drag; they add their own Cd*A when deployed (RocketData).
// Decoupler and fairing drops change the drag too, so drag is stored for each drop state.
public static class RocketAssembly
{
    public const float HeatShieldHeatScale = 0.5f;  // shortcut: one flat factor for any heat shield, not per material

    public static void Apply(PartNode rocketRoot, PartNode payloadRoot, RocketData r)
    {
        var rocketNodes = rocketRoot == null ? new List<PartNode>() : rocketRoot.Walk().ToList();
        var payloadNodes = payloadRoot == null ? new List<PartNode>() : payloadRoot.Walk().ToList();
        var nodes = rocketNodes.Concat(payloadNodes).ToList();

        float dry = 0f, fuel = 0f, thrust = 0f, flow = 0f, chute = 0f, drogue = 0f, shield = 0f, fairing = 0f, heat = 1f, science = 0f;
        int mask = 0;
        foreach (var node in nodes)
        {
            var m = node.data;
            mask |= 1 << (int)m.specialType;
            science += m.scienceValue;
            drogue += m.drogueCdA;
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

        // The booster: what the decoupler carries in the rocket tree. Fairings are dropped separately, so they are not part of it.
        var decoupled = new HashSet<PartNode>();
        foreach (var n in rocketNodes.Where(n => n.data.specialType == SpecialModuleType.Decoupler))
            foreach (var c in n.children)
                foreach (var x in c.Walk())
                    if (x.data.specialType != SpecialModuleType.Fairing) decoupled.Add(x);

        float boosterThrust = 0f, boosterFlow = 0f, boosterFuel = 0f;
        float sustainerThrust = 0f, sustainerFlow = 0f, sustainerFuel = 0f;
        foreach (var node in rocketNodes)
        {
            var m = node.data;
            if (decoupled.Contains(node))
            {
                boosterThrust += m.thrust;
                if (m.thrust > 0f) boosterFlow += m.thrust / m.isp;
                boosterFuel += m.fuelMass;
            }
            else
            {
                sustainerThrust += m.thrust;
                if (m.thrust > 0f) sustainerFlow += m.thrust / m.isp;
                sustainerFuel += m.fuelMass;
            }
        }
        bool handover = decoupled.Count > 0 && boosterThrust > 0f;
        if (handover)
        {
            r.thrust = boosterThrust;
            r.isp = boosterThrust / boosterFlow;
            r.launchFuel = boosterFuel;
            r.upperThrust = sustainerThrust;
            r.upperIsp = sustainerThrust > 0f ? sustainerThrust / sustainerFlow : 1f;
            r.upperFuel = sustainerFuel;
        }
        else
        {
            // shortcut: without a handover every engine and all propellant share the one stage, and staging only drops the booster's mass.
            r.thrust = thrust;
            r.isp = thrust > 0f ? thrust / flow : 1f;  // Isp is unused when thrust is zero
            r.launchFuel = fuel;
            r.upperThrust = 0f;
            r.upperIsp = 1f;
            r.upperFuel = 0f;
        }
        r.handover = handover;

        // The probe: the payload tree without the fairing, with its own parachutes, heat shield and drag.
        var probe = payloadNodes.Where(n => !IsFairing(n.data)).ToList();
        r.probeDryMass = probe.Sum(n => n.data.mass);
        r.probeDragCdA = BaseCdA(probe.Where(n => !IsStowed(n.data)));
        r.probeChuteCdA = probe.Where(n => n.data.specialType == SpecialModuleType.Parachute).Sum(n => n.data.dragCoefficient * n.data.referenceArea);
        r.probeDrogueCdA = probe.Sum(n => n.data.drogueCdA);
        r.probeShieldCdA = probe.Where(n => n.data.specialType == SpecialModuleType.HeatShield).Sum(n => n.data.dragCoefficient * n.data.referenceArea);
        r.probeHeatScale = probe.Any(n => n.data.specialType == SpecialModuleType.HeatShield) ? HeatShieldHeatScale : 1f;

        var flying = nodes.Where(n => !IsStowed(n.data)).ToList();

        // Index = Separated | Jettisoned bits: none, booster gone, fairing gone, both.
        r.dragCdA = new Vector4(
            BaseCdA(flying),
            BaseCdA(flying.Where(n => !decoupled.Contains(n))),
            BaseCdA(flying.Where(n => !IsFairing(n.data))),
            BaseCdA(flying.Where(n => !decoupled.Contains(n) && !IsFairing(n.data))));
        r.dryMass = dry;
        r.fuelMass = fuel;
        r.chuteCdA = chute;
        r.drogueCdA = drogue;
        r.shieldCdA = shield;
        r.heatScale = heat;
        r.fairingMass = fairing;
        r.decouplerMass = decoupled.Sum(x => x.data.mass);
        r.specialMask = mask;
        r.scienceValue = science;
        r.partLimits = nodes.Where(n => n.data.maxQ > 0f || n.data.maxHeating > 0f)
            .Select(n => new RocketData.PartLimit { type = n.data.specialType, maxQ = n.data.maxQ, maxHeating = n.data.maxHeating })
            .ToArray();
    }

    static bool IsStowed(ModuleData m) => m.specialType == SpecialModuleType.Parachute || m.specialType == SpecialModuleType.HeatShield;
    static bool IsFairing(ModuleData m) => m.specialType == SpecialModuleType.Fairing;

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

        // Tree: base tank with motorA above it, a stowed parachute, and a decoupler carrying motorB (the booster).
        var root = new PartNode(tank, null);
        var child = root.AddChild(motorA);
        root.AddChild(chute);
        var dec = root.AddChild(decoupler);
        dec.AddChild(motorB);
        var r = ScriptableObject.CreateInstance<RocketData>();
        Apply(root, null, r);

        // Dry: 5 + 10 + 10 + 3 + 2 = 30 kg. Flying drag: Cd_mean 0.5 over 0.10 m^2 of flying area, times the largest area 0.04 m^2.
        // Booster = motorB (1000 N, 200 s, no propellant). Sustainer = tank (40 kg) and motorA (1000 N, 250 s), lit by the handover.
        bool ok = Near(r.dryMass, 30f) && Near(r.fuelMass, 40f) && Near(r.thrust, 1000f) && Near(r.isp, 200f)
                  && r.handover && Near(r.upperThrust, 1000f) && Near(r.upperIsp, 250f) && Near(r.upperFuel, 40f) && Near(r.launchFuel, 0f)
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
