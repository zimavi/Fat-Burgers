using System.Collections.Generic;
using UnityEngine;

public struct Requirement
{
    public string Text;
    public bool Met;
}

// Mission rules. The builder asks whether the rocket may launch, and the flight and result read the checklist.
public static class MissionEvaluator
{
    // Why the rocket cannot launch on this mission, or null when it can.
    public static string LaunchBlocker(MissionData m, RocketData rocket)
    {
        if (!m) return null;
        if (m.kind == MissionKind.TestFlight && !rocket.Has(m.moduleUnderTest)) return $"Fit the {m.moduleUnderTest} module to test it";
        if (m.kind == MissionKind.Science && rocket.scienceValue <= 0f) return "Fit a science payload first";
        return null;
    }

    // A test counts when the player ran it: the module under test is fitted, worked this flight, and was tested at the test altitude.
    public static bool TestPassed(MissionData m, RocketData rocket, int failed, bool testRan) =>
        m.kind == MissionKind.TestFlight && rocket.Has(m.moduleUnderTest)
        && (failed & (1 << (int)m.moduleUnderTest)) == 0 && testRan;

    // The briefing's line on the module under test: what its last flight did, and how far it can be trusted.
    public static string ModuleStatus(MissionData m)
    {
        if (m.kind != MissionKind.TestFlight) return "";
        var t = m.moduleUnderTest;
        string name = ReliabilityStore.DisplayName(t);
        string story = ReliabilityStore.LastResult(t) switch
        {
            FlightResult.Failed => $"On its last flight the {name} failed: {ReliabilityStore.LastCause(t)}.",
            FlightResult.Tested => $"On its last flight the {name} passed its test at {m.testAltitude / 1000f:0} km.",
            FlightResult.Worked => $"On its last flight the {name} worked, but was not tested to {m.testAltitude / 1000f:0} km.",
            _ => $"The {name} has never flown.",
        };
        return $"\n{story} Reliability {ReliabilityStore.Get(t):0}%.";
    }

    // Each requirement and whether the flight meets it so far. Apogee and impact are final only once landed.
    // Recovery needs the probe to be on its own (probeFree) and to land softly, or the data to be transmitted.
    public static List<Requirement> Checklist(MissionData m, MissionProgress p, RocketData rocket, int failed, float apogee, bool landed, float impactSpeed, bool probeFree)
    {
        var list = new List<Requirement>();
        bool recovered = landed && probeFree && impactSpeed <= AdvisorRules.SoftLanding;
        bool transmitted = p != null && p.Transmitted;
        if (m.kind == MissionKind.TestFlight)
        {
            bool worked = rocket.Has(m.moduleUnderTest) && (failed & (1 << (int)m.moduleUnderTest)) == 0;
            list.Add(new Requirement { Text = $"Carry the {m.moduleUnderTest} module and keep it working", Met = worked });
            list.Add(new Requirement { Text = $"Run the module test at {m.testAltitude / 1000f:0} km", Met = p != null && p.TestDone });
            list.Add(new Requirement { Text = "Bring the probe home: separate it, land softly or transmit", Met = recovered || transmitted });
            return list;
        }

        bool space = m.target == ScienceTarget.Space;
        string run = p == null ? "" : $" ({p.ExperimentTime:0}/{m.experimentSeconds:0} s)";
        list.Add(new Requirement { Text = "Carry a science payload", Met = rocket.scienceValue > 0f });
        list.Add(new Requirement
        {
            Text = $"Run the experiment at {p.TargetAltitude / 1000f:0} km{run}",
            Met = p.DataCollected,
        });
        list.Add(new Requirement
        {
            Text = space ? "Climb above 100 km" : "Stay under 100 km",
            Met = space ? apogee >= FlightEvents.BoundaryAltitude : apogee < FlightEvents.BoundaryAltitude,
        });
        list.Add(new Requirement { Text = "Bring the data home: separate the probe, land softly or transmit", Met = recovered || transmitted });
        return list;
    }

    public static bool AllMet(List<Requirement> list) => list.TrueForAll(r => r.Met);

    public static bool SelfCheck()
    {
        var m = ScriptableObject.CreateInstance<MissionData>();
        m.kind = MissionKind.TestFlight;
        m.moduleUnderTest = SpecialModuleType.Parachute;
        m.testAltitude = 110000f;
        var r = ScriptableObject.CreateInstance<RocketData>();
        int parachuteFailed = 1 << (int)SpecialModuleType.Parachute;

        bool blocked = LaunchBlocker(m, r) != null;  // no parachute fitted
        r.specialMask = parachuteFailed;
        bool clear = LaunchBlocker(m, r) == null;
        // Nothing run and nothing recovered: the test is not met.
        bool notYet = !AllMet(Checklist(m, null, r, 0, 120000f, false, 0f, false));
        // A test counts only when it was run on a working module.
        bool credit = TestPassed(m, r, 0, true) && !TestPassed(m, r, 0, false) && !TestPassed(m, r, parachuteFailed, true);

        Object.DestroyImmediate(m);
        Object.DestroyImmediate(r);
        return blocked && clear && notYet && credit;
    }
}
