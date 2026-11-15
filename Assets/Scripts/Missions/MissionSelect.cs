using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// The first screen: pick a mission (from Resources/Missions) or a free flight with no objectives, then go to the builder.
// Attached by MissionSelectBootstrap when the MissionSelect scene loads, so the scene needs no wiring.
public class MissionSelect : MonoBehaviour
{
    static readonly Color Ink = new Color(1f, 0.9f, 0.62f);
    static readonly Color Dim = new Color(1f, 0.9f, 0.62f, 0.7f);
    static readonly Color CardBg = new Color(0.16f, 0.1f, 0.04f, 0.95f);
    static readonly Color Backdrop = new Color(0.03f, 0.04f, 0.06f, 1f);

    const int FreeFlightSeed = 12345;

    void Start()
    {
        // UI pointer input needs an EventSystem using the new Input System module, not StandaloneInputModule.
        var events = FindFirstObjectByType<EventSystem>();
        if (!events) events = new GameObject("EventSystem", typeof(EventSystem)).GetComponent<EventSystem>();
        var legacy = events.GetComponent<StandaloneInputModule>();
        if (legacy) Destroy(legacy);
        if (!events.GetComponent<InputSystemUIInputModule>()) events.gameObject.AddComponent<InputSystemUIInputModule>();

        var font = FontFactory.GetPixelifyFont();
        var canvasGo = new GameObject("MissionSelectCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        var canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        var scaler = canvasGo.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;
        var root = (RectTransform)canvasGo.transform;

        var back = new GameObject("Backdrop", typeof(RectTransform), typeof(Image));
        back.transform.SetParent(root, false);
        Stretch((RectTransform)back.transform);
        back.GetComponent<Image>().color = Backdrop;

        // Centred column: heading, then one card per mission, then the free flight.
        var columnGo = new GameObject("Column", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
        columnGo.transform.SetParent(root, false);
        var column = (RectTransform)columnGo.transform;
        column.anchorMin = column.anchorMax = column.pivot = new Vector2(0.5f, 0.5f);
        column.sizeDelta = new Vector2(1100f, 0f);
        var layout = columnGo.GetComponent<VerticalLayoutGroup>();
        layout.spacing = 14f;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        columnGo.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        Label(column, "MISSION SELECT", 40, font, Ink);
        var missions = Resources.LoadAll<MissionData>("Missions").OrderBy(m => m.name).ToArray();
        foreach (var m in missions)
        {
            var mission = m;
            Card(column, mission.title, Summary(mission), () => Choose(mission, mission.seed), font);
        }
        Card(column, "Free flight", "No mission and no objectives. Build and fly anything.", () => Choose(null, FreeFlightSeed), font);
        if (missions.Length == 0) Debug.LogWarning("MissionSelect: no missions found in Resources/Missions.");
    }

    // One line under the header, the same shape as the free flight card. The full briefing and the module's status are on the builder.
    static string Summary(MissionData m) => m.kind == MissionKind.TestFlight
        ? $"Test flight: the {ReliabilityStore.DisplayName(m.moduleUnderTest)} must work at {m.testAltitude / 1000f:0} km, and the probe must come home."
        : $"Science ({(m.target == ScienceTarget.Space ? "space" : "atmosphere")}): run the experiment at about {m.altitudeMin / 1000f:0}-{m.altitudeMax / 1000f:0} km, then bring the data home.";

    static void Choose(MissionData mission, int seed)
    {
        MissionSession.Mission = mission;
        MissionSession.Seed = seed;
        SceneManager.LoadScene("BuilderScene");
    }

    static void Card(Transform parent, string title, string body, UnityAction onClick, TMP_FontAsset font)
    {
        var go = new GameObject("Mission", typeof(RectTransform), typeof(Image), typeof(Button), typeof(VerticalLayoutGroup), typeof(LayoutElement));
        go.transform.SetParent(parent, false);
        go.GetComponent<LayoutElement>().preferredHeight = 120f;
        var img = go.GetComponent<Image>();
        img.color = CardBg;
        var button = go.GetComponent<Button>();
        button.targetGraphic = img;
        button.onClick.AddListener(onClick);
        var inner = go.GetComponent<VerticalLayoutGroup>();
        inner.padding = new RectOffset(22, 22, 14, 14);
        inner.spacing = 4f;
        inner.childControlWidth = true;
        inner.childControlHeight = true;
        inner.childForceExpandHeight = false;
        Label(go.transform, title, 28, font, Ink);
        Label(go.transform, body, 17, font, Dim);
    }

    static void Label(Transform parent, string text, float size, TMP_FontAsset font, Color color)
    {
        var go = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);
        var t = go.GetComponent<TextMeshProUGUI>();
        t.font = font;
        t.text = text;
        t.fontSize = size;
        t.color = color;
        t.textWrappingMode = TextWrappingModes.Normal;
        t.raycastTarget = false;
    }

    static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
    }
}

// Attaches the mission select when its scene loads.
public static class MissionSelectBootstrap
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Attach()
    {
        if (SceneManager.GetActiveScene().name == "MissionSelect")
            new GameObject("MissionSelect").AddComponent<MissionSelect>();
    }
}
