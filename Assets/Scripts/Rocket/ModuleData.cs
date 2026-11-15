using UnityEngine;

// One part type. Base modules are tree roots; every other module attaches under a node whose allowedChildren lists it.
// Units: kg, N, s, m^2, UI pixels for size. Unused stats stay at zero.
[CreateAssetMenu(menuName = "Fat Burgers/Module Data", fileName = "ModuleData")]
public class ModuleData : ScriptableObject
{
    public string displayName = "Module";
    public bool isBase;                              // tree root
    [Tooltip("Position in the ghost stack: lower sits lower. Equal values keep tree order (base first), so a fairing gets a high value to sit on top.")]
    public int stackOrder;
    public SpecialModuleType specialType;            // special module type, for gameplay effects
    public Sprite sprite;                            // null = placeholder rectangle in placeholderColor
    public Sprite icon;                              // shown inside the card's circle; the circle stays plain when empty
    [TextArea] public string description;            // tooltip text under the module name
    public Color placeholderColor = Color.gray;
    public Vector2 size = new Vector2(120f, 40f);    // UI pixels in the rocket view

    [Min(0)] public float mass = 10f;                // kg, dry
    [Min(0)] public float fuelMass;                  // kg
    [Min(0)] public float thrust;                    // N
    [Min(1)] public float isp = 200f;                // s
    [Min(0)] public float dragCoefficient = 0.5f;    // Cd
    [Min(0)] public float referenceArea;             // m^2, frontal area
    [Min(0)] public float scienceValue;              // payload score, no physics effect
    [Tooltip("Drogue drag area, m^2. Deployed at apogee, released when the main canopy opens. Parachute module only. 0 = none.")]
    [Min(0)] public float drogueCdA;
    [Tooltip("Dynamic pressure this module survives, Pa. Above it the module is torn off or destroyed for the rest of the flight. 0 = no limit.")]
    [Min(0)] public float maxQ;
    [Tooltip("Heating index this module survives. Above it the module burns through or is destroyed for the rest of the flight. 0 = no limit.")]
    [Min(0)] public float maxHeating;

    public ModuleData[] allowedChildren;
    [Tooltip("Most children this node can hold. 0 = one slot per entry in allowedChildren.")]
    [Min(0)] public int maxChildren;
    [Tooltip("Most copies of this module in one build, across both trees. 0 = unlimited. Set it on any module that can stack on itself.")]
    [Min(0)] public int maxInstances;
}
