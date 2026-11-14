using System;
using System.Collections.Generic;
using TMPro;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

// Bakes the trajectory with TrajectoryJob and draws downrange (x) vs altitude (y) with a LineRenderer.
// Re-bakes on Start and whenever the Inspector changes. Simulation stays in SI; only this view converts units.
// Event circles: rings in Play mode, Scene-view gizmos in Edit mode. The tooltip is spawned at runtime.
[RequireComponent(typeof(LineRenderer))]
public class TrajectoryView : MonoBehaviour
{
    public RocketData rocket;
    public PlanetData planet;
    [Tooltip("Meters per world unit. 1000 = 1 unit per km.")]
    public float metersPerUnit = 1000f;
    [Tooltip("Event circle radius and hover radius, in world units.")]
    public float markerRadius = 0.3f;
    [Tooltip("Width of the event circle lines, in world units.")]
    public float markerLineWidth = 0.05f;
    [Tooltip("Defaults to Camera.main when empty.")]
    public Camera viewCamera;

    LineRenderer line;
    readonly List<FlightEvent> events = new List<FlightEvent>();
    readonly List<Vector3> eventPoints = new List<Vector3>();  // local space, same order as events
    readonly List<LineRenderer> markers = new List<LineRenderer>();
    private Camera _camera;
    GameObject tooltip;   // spawned at runtime
    TMP_Text tooltipText;

    void Awake() => line = GetComponent<LineRenderer>();
    void Start()
    {
        _camera = Camera.main;
        SpawnTooltip();
        Bake();
    }

    void OnDrawGizmos()
    {
        if (Application.isPlaying) return;
        Gizmos.color = Color.cyan;
        foreach (var p in eventPoints) Gizmos.DrawWireSphere(transform.TransformPoint(p), markerRadius);
    }
    void OnValidate() => Bake();
    void Update() => UpdateHover();

    public void Bake()
    {
        if (line == null) line = GetComponent<LineRenderer>();
        events.Clear();
        eventPoints.Clear();
        if (rocket == null || planet == null) { line.positionCount = 0; ClearMarkers(); return; }

        var keys = (RocketData.PitchKey[])(rocket.pitchProgram ?? new RocketData.PitchKey[0]).Clone();
        Array.Sort(keys, (a, b) => a.time.CompareTo(b.time));
        var pitch = new NativeArray<float2>(keys.Length, Allocator.TempJob);
        for (int i = 0; i < keys.Length; i++) pitch[i] = new float2(keys[i].time, math.radians(keys[i].pitchDeg));

        var p = new TrajectoryParams
        {
            DryMass = rocket.dryMass, Thrust = rocket.thrust, Isp = rocket.isp,
            Cd = rocket.dragCoefficient, Area = rocket.referenceArea,
            G0 = planet.g0, PlanetRadius = planet.radius, Rho0 = planet.seaLevelDensity, ScaleHeight = planet.scaleHeight,
        };
        var start = new TrajectoryState { Pos = new float2(0f, p.PlanetRadius), Vel = float2.zero, Fuel = rocket.fuelMass, Time = 0f };
        var samples = new NativeList<TrajectorySample>(1024, Allocator.TempJob);

        new TrajectoryJob { P = p, Pitch = pitch, Start = start, Samples = samples }.Schedule().Complete();

        var points = new Vector3[samples.Length];
        for (int i = 0; i < points.Length; i++)
            points[i] = new Vector3(samples[i].Downrange, samples[i].Altitude, 0f) / metersPerUnit;
        line.useWorldSpace = false;  // points are local, so the line starts at this object's transform
        line.positionCount = points.Length;
        line.SetPositions(points);

        events.AddRange(FlightEvents.Detect(samples));
        foreach (var e in events) eventPoints.Add(new Vector3(e.Downrange, e.Altitude, 0f) / metersPerUnit);
        if (Application.isPlaying) BuildMarkers();

        pitch.Dispose();
        samples.Dispose();
    }

    void BuildMarkers()
    {
        ClearMarkers();
        for (int i = 0; i < eventPoints.Count; i++)
        {
            var go = new GameObject(FlightEvents.Name(events[i].Type));
            go.transform.SetParent(transform, false);
            go.transform.localPosition = eventPoints[i];

            var ring = go.AddComponent<LineRenderer>();
            ring.useWorldSpace = false;
            ring.loop = true;
            ring.sharedMaterial = line.sharedMaterial;
            ring.startWidth = ring.endWidth = markerLineWidth;
            const int segments = 32;
            ring.positionCount = segments;
            for (int k = 0; k < segments; k++)
            {
                float a = k * 2f * math.PI / segments;
                ring.SetPosition(k, new Vector3(math.cos(a), math.sin(a), 0f) * markerRadius);
            }
            markers.Add(ring);
        }
    }

    void ClearMarkers()
    {
        foreach (var m in markers) if (m != null) Destroy(m.gameObject);
        markers.Clear();
    }

    // Overlay canvas with a dark panel and a text block. The panel grows with its text.
    void SpawnTooltip()
    {
        var canvasGo = new GameObject("TrajectoryTooltipCanvas", typeof(Canvas));
        var canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;

        tooltip = new GameObject("Tooltip", typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
        tooltip.transform.SetParent(canvasGo.transform, false);
        var rect = (RectTransform)tooltip.transform;
        rect.pivot = new Vector2(0f, 1f);  // top-left corner sits at the cursor
        rect.sizeDelta = new Vector2(380f, 0f);
        tooltip.GetComponent<Image>().color = new Color(0.02f, 0.05f, 0.08f, 0.92f);
        var layout = tooltip.GetComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(12, 12, 10, 10);
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandHeight = false;
        tooltip.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        var textGo = new GameObject("Text", typeof(RectTransform));
        textGo.transform.SetParent(tooltip.transform, false);
        tooltipText = textGo.AddComponent<TextMeshProUGUI>();
        tooltipText.fontSize = 16;
        tooltipText.textWrappingMode = TextWrappingModes.Normal;

        tooltip.SetActive(false);
    }

    void UpdateHover()
    {
        if (!tooltip) return;
        var cam = viewCamera ? viewCamera : _camera;
        if (!cam || Mouse.current == null || events.Count == 0)
        {
            tooltip.SetActive(false);
            return;
        }

        Vector2 mouse = Mouse.current.position.ReadValue();
        Vector2 world = cam.ScreenToWorldPoint(new Vector3(mouse.x, mouse.y, -cam.transform.position.z));

        int best = -1;
        float bestDist = markerRadius;
        for (int i = 0; i < eventPoints.Count; i++)
        {
            float d = Vector2.Distance(world, transform.TransformPoint(eventPoints[i]));
            if (d <= bestDist) { bestDist = d; best = i; }
        }

        tooltip.SetActive(best >= 0);
        if (best < 0) return;

        var e = events[best];
        tooltipText.text = $"<b>{FlightEvents.Name(e.Type)}</b>\n{FlightEvents.Description(e.Type)}\n\n{FlightEvents.Details(e)}";
        tooltip.transform.position = mouse + new Vector2(15f, -15f);  // offset from cursor
    }
}
