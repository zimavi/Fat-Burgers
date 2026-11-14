using UnityEngine;

[CreateAssetMenu(menuName = "Fat Burgers/Planet Data", fileName = "PlanetData")]
public class PlanetData : ScriptableObject
{
    public float g0 = 9.80665f;             // m/s^2, surface gravity
    public float radius = 6371000f;         // m
    public float seaLevelDensity = 1.225f;  // kg/m^3
    public float scaleHeight = 8500f;       // m
    public float angularVelocity = 7.2921159e-5f;  // rad/s, sidereal rotation; the launch pad moves at angularVelocity * radius
}
