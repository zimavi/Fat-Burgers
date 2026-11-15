using UnityEngine;

// One part type. Base modules are tree roots; every other module attaches under a node whose allowedChildren lists it.
// Units: kg, N, s, m^2, UI pixels for size. Unused stats stay at zero.
[CreateAssetMenu(menuName = "Fat Burgers/Module Data", fileName = "ModuleData")]
public class ModuleData : ScriptableObject
{
    public string displayName = "Module";
    public bool isBase;                              // tree root
    public bool isFairing;                           // payload fairing, drawn on the sides of the payload stack
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

    public ModuleData[] allowedChildren;
    [Tooltip("Most children this node can hold. 0 = one slot per entry in allowedChildren.")]
    [Min(0)] public int maxChildren;
    [Tooltip("Most copies of this module in one build, across both trees. 0 = unlimited. Set it on any module that can stack on itself.")]
    [Min(0)] public int maxInstances;
}
