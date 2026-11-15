using System;
using UnityEngine;

[CreateAssetMenu(menuName = "Fat Burgers/Rocket Data", fileName = "RocketData")]
public class RocketData : ScriptableObject
{
    [Min(0)] public float dryMass = 40f;           // kg
    [Min(0)] public float fuelMass = 60f;          // kg
    [Min(0)] public float thrust = 5000f;          // N, burns until propellant is gone
    [Min(1)] public float isp = 200f;              // s

    // Set by RocketAssembly from the modules. The trajectory reads these.
    public Vector4 dragCdA;                        // m^2, Cd*A for each drop state (index = Separated | Jettisoned bits)
    public float chuteCdA;                         // m^2, parachutes, added once deployed
    public float drogueCdA;                        // m^2, drogue chute, deployed at apogee and released when the main canopy opens
    public float shieldCdA;                        // m^2, heat shields, added on reentry
    public float heatScale = 1f;                   // heating index multiplier, below 1 with a heat shield
    public float fairingMass;                      // kg, dropped at 100 km
    public float decouplerMass;                    // kg, the booster: everything the decoupler carries, dropped when it stages

    // Two stages. The booster (under the decoupler) burns first. With a handover the sustainer lights when the booster is dropped.
    public float launchFuel;                       // kg, propellant of the active first stage at launch
    public float upperFuel;                        // kg, propellant of the sustainer
    public float upperThrust, upperIsp;            // N, s: the sustainer
    public bool handover;                          // the booster has engines, so staging hands thrust over to the sustainer

    // The probe (payload tree) on its own, after it separates from the rocket.
    public float probeDryMass;                     // kg, payload tree without the fairing
    public float probeDragCdA;                     // m^2, probe Cd*A (the fairing is gone before it separates)
    public float probeChuteCdA, probeDrogueCdA, probeShieldCdA;  // m^2, the probe's own recovery modules
    public float probeHeatScale = 1f;
    public int specialMask;                        // bit (1 << SpecialModuleType) for each special module fitted anywhere
    public float scienceValue;                     // payload score, summed from the modules

    public bool Has(SpecialModuleType t) => (specialMask & (1 << (int)t)) != 0;

    // Damage limits of the fitted modules, copied from ModuleData by RocketAssembly.
    [Serializable]
    public struct PartLimit
    {
        public SpecialModuleType type;
        public float maxQ, maxHeating;  // Pa, heating index; 0 = no limit
    }
    public PartLimit[] partLimits = new PartLimit[0];

    [Tooltip("Parachute opening: each altitude in this range is flown and the best one is kept.")]
    [Min(0)] public float chuteDeployMin = 3000f;  // m
    [Min(0)] public float chuteDeployMax = 10000f; // m
    [Tooltip("Largest opening shock (dynamic pressure at deployment) the chute may take. If no altitude stays under it, the lowest shock wins. Airframe limit on dynamic pressure. The advisor warns as the flight nears it.")]
    [Min(0)] public float maxQ = 80000f;           // Pa, 0 = no limit. Above it the airframe breaks up and the flight ends.
    [Min(0)] public float maxChuteQ = 60000f;      // Pa

    [Tooltip("Pitch from local horizontal: 90 = straight up. Empty = straight up all flight.")]
    public PitchKey[] pitchProgram;

    [Serializable]
    public struct PitchKey
    {
        public float time;                         // s, mission elapsed time
        [Range(-90f, 90f)] public float pitchDeg;  // degrees
    }
}
