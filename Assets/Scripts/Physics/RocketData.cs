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
    public float shieldCdA;                        // m^2, heat shields, added on reentry
    public float heatScale = 1f;                   // heating index multiplier, below 1 with a heat shield
    public float fairingMass;                      // kg, dropped at 100 km
    public float decouplerMass;                    // kg, dropped at MECO

    [Tooltip("Parachute opening: each altitude in this range is flown and the best one is kept.")]
    [Min(0)] public float chuteDeployMin = 5000f;  // m
    [Min(0)] public float chuteDeployMax = 10000f; // m
    [Tooltip("Largest opening shock (dynamic pressure at deployment) the chute may take. If no altitude stays under it, the lowest shock wins.")]
    [Min(0)] public float maxChuteQ = 40000f;      // Pa

    [Tooltip("Pitch from local horizontal: 90 = straight up. Empty = straight up all flight.")]
    public PitchKey[] pitchProgram;

    [Serializable]
    public struct PitchKey
    {
        public float time;                         // s, mission elapsed time
        [Range(-90f, 90f)] public float pitchDeg;  // degrees
    }
}
