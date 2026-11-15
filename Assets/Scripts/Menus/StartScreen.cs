using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// The title screen. START goes to the story (it skips itself once seen). CREDITS shows the credit list over the title; a click closes it.
// The title art already prints "start", so the START button is placed over that word. Attached by StartScreenBootstrap.
public class StartScreen : MonoBehaviour
{
    const string TitlePath = "Sprites/Untitled.png";
    const string CreditsPath = "Sprites/credits.png";

    GameObject credits;

    void Start()
    {
        // UI pointer input needs an EventSystem using the new Input System module, not StandaloneInputModule.
        var events = FindFirstObjectByType<EventSystem>();
        if (!events) events = new GameObject("EventSystem", typeof(EventSystem)).GetComponent<EventSystem>();
        var legacy = events.GetComponent<StandaloneInputModule>();
        if (legacy) Destroy(legacy);
        if (!events.GetComponent<InputSystemUIInputModule>()) events.gameObject.AddComponent<InputSystemUIInputModule>();

        var font = FontFactory.GetPixelifyFont();
        var canvasGo = new GameObject("StartCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        var canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 0;
        var scaler = canvasGo.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Shrink;

        var stageGo = new GameObject("Stage", typeof(RectTransform));
        stageGo.transform.SetParent(canvasGo.transform, false);
        var stage = (RectTransform)stageGo.transform;
        stage.anchorMin = stage.anchorMax = stage.pivot = new Vector2(0.5f, 0.5f);
        stage.sizeDelta = new Vector2(1920f, 1080f);

        var title = Fill(stage, "Title");
        title.sprite = LoadSprite(TitlePath);
        title.preserveAspect = true;

        // Button rects are in stage pixels, top-left origin. The START box sits on the printed "start" word.
        Hit(stage, new Rect(820, 746, 300, 80), null, Color.clear, () => SceneManager.LoadScene("Plot"), font);
        Hit(stage, new Rect(810, 880, 300, 64), "CREDITS", new Color(0.16f, 0.1f, 0.04f, 0.95f), ShowCredits, font);

        // The credits cover the title; any click closes them.
        credits = Fill(stage, "Credits").gameObject;
        var creditsImg = credits.GetComponent<Image>();
        creditsImg.sprite = LoadSprite(CreditsPath);
        creditsImg.preserveAspect = true;
        creditsImg.raycastTarget = true;
        var close = credits.AddComponent<Button>();
        close.targetGraphic = creditsImg;
        close.transition = Selectable.Transition.None;
        close.onClick.AddListener(() => credits.SetActive(false));
        credits.SetActive(false);
    }

    void ShowCredits() => credits.SetActive(true);

    static Image Fill(RectTransform stage, string name)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(stage, false);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        var img = go.GetComponent<Image>();
        img.raycastTarget = false;
        return img;
    }

    // A clickable box at a stage rect (top-left origin). A label is optional; the box is clear when no colour is given.
    static Button Hit(RectTransform stage, Rect r, string label, Color bg, UnityAction onClick, TMP_FontAsset font)
    {
        var go = new GameObject("Button", typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(stage, false);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(r.x, -r.y);
        rt.sizeDelta = new Vector2(r.width, r.height);
        var img = go.GetComponent<Image>();
        img.color = bg;
        var b = go.GetComponent<Button>();
        b.targetGraphic = img;
        b.transition = Selectable.Transition.None;
        b.onClick.AddListener(onClick);
        if (label != null)
        {
            var textGo = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI));
            textGo.transform.SetParent(rt, false);
            var t = textGo.GetComponent<TextMeshProUGUI>();
            t.font = font;
            t.text = label;
            t.fontSize = 28;
            t.color = new Color(1f, 0.9f, 0.62f);
            t.alignment = TextAlignmentOptions.Center;
            t.raycastTarget = false;
            var tr = (RectTransform)textGo.transform;
            tr.anchorMin = Vector2.zero;
            tr.anchorMax = Vector2.one;
            tr.offsetMin = tr.offsetMax = Vector2.zero;
        }
        return b;
    }

    static Sprite LoadSprite(string path) => ProjectAssets.LoadSprite(path);
}

// Attaches the start screen when its scene loads.
public static class StartScreenBootstrap
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Attach()
    {
        if (SceneManager.GetActiveScene().name == "Start")
            new GameObject("StartScreen").AddComponent<StartScreen>();
    }
}
