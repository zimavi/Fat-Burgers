using System;
using UnityEngine;

[CreateAssetMenu(menuName = "Fat Burgers/Rocket Data", fileName = "RocketData")]
public class RocketData : ScriptableObject
{
    [Min(0)] public float dryMass = 40f;           // kg
    [Min(0)] public float fuelMass = 60f;          // kg
    [Min(0)] public float thrust = 5000f;          // N, burns until propellant is gone
    [Min(1)] public float isp = 200f;              // s
    [Min(0)] public float dragCoefficient = 0.5f;  // Cd
    [Min(0)] public float referenceArea = 0.02f;  // m^2
    [Tooltip("Pitch from local horizontal: 90 = straight up. Empty = straight up all flight.")]
    public PitchKey[] pitchProgram;

    [Serializable]
    public struct PitchKey
    {
        public float time;                         // s, mission elapsed time
        [Range(-90f, 90f)] public float pitchDeg;  // degrees
    }
}
