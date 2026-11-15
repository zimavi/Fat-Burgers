using UnityEngine;

public enum MissionKind { TestFlight, Science }
public enum ScienceTarget { Atmosphere, Space }

// One mission: its briefing and the requirements the flight must meet. Read by the builder (can it launch?),
// the flight (live checklist and experiment) and the result (outcome).
[CreateAssetMenu(menuName = "Fat Burgers/Mission Data", fileName = "MissionData")]
public class MissionData : ScriptableObject
{
    public string title = "Mission";
    public MissionKind kind;
    [TextArea] public string briefing;
    public int seed = 1;                           // seeds the experiment altitude and the disturbances

    [Header("Test flight")]
    public SpecialModuleType moduleUnderTest = SpecialModuleType.Parachute;
    [Min(0)] public float testAltitude = 110000f;  // m, the module is tested by reaching this altitude with it aboard

    [Header("Science")]
    public ScienceTarget target;
    [Tooltip("The experiment altitude is picked from this range, using the seed.")]
    [Min(0)] public float altitudeMin = 20000f, altitudeMax = 70000f;  // m
    [Tooltip("The experiment runs while the rocket is within this distance of its target altitude.")]
    [Min(0)] public float experimentWindow = 5000f;  // m
    [Min(0)] public float experimentSeconds = 10f;   // s inside the window to collect the data
}
