using System.Collections.Generic;

// How each module can fail. A failure picks one cause from its list, seeded by the flight, so a replay shows the same cause.
// Each cause completes "the <module> failed: <cause>". Causes are plain text; keep them short enough for a tooltip line.
// shortcut: causes live in code, not in ModuleData. Move them there if designers need to edit them without touching scripts.
public static class FailureModes
{
    static readonly Dictionary<SpecialModuleType, string[]> causes = new Dictionary<SpecialModuleType, string[]>
    {
        [SpecialModuleType.Parachute] = new[]
        {
            "the reefing line tangled and the canopy never fully opened",
            "the drogue chute did not inflate",
            "the canopy tore under the opening shock",
            "the riser stitching gave way",
        },
        [SpecialModuleType.Decoupler] = new[]
        {
            "the separation charge did not fire",
            "the clamp band did not release",
            "the separation spring was undercharged",
            "the pyro circuit lost power at burnout",
        },
        [SpecialModuleType.HeatShield] = new[]
        {
            "adhesive failed between the ablator and the nose cone",
            "ablator density was incorrect for the mix",
            "the ablator cracked during the coast",
            "the char layer spalled off early",
        },
        [SpecialModuleType.Fairing] = new[]
        {
            "the latch did not release",
            "the hinge seal jammed",
            "the jettison charge misfired",
            "the halves cracked under the aerodynamic load",
        },
        [SpecialModuleType.AdvancedCommunication] = new[]
        {
            "the antenna cable came loose under vibration",
            "the transmitter overheated in thin air",
            "the modem lost lock with the ground station",
            "the antenna feed cracked at launch",
        },
    };

    public static string Cause(SpecialModuleType t, int seed)
    {
        if (!causes.TryGetValue(t, out var list) || list.Length == 0) return "unknown fault";
        var rng = new System.Random(seed * 7919 + (int)t * 104729 + 11);
        return list[rng.Next(list.Length)];
    }

    public static bool SelfCheck()
    {
        foreach (var t in ReliabilityStore.Rolled)
        {
            if (!causes.TryGetValue(t, out var list) || list.Length < 3) return false;
            if (Cause(t, 42) != Cause(t, 42)) return false;  // repeatable for one seed
        }
        return true;
    }
}
