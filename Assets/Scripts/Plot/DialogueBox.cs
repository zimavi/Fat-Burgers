using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

// Plays story beats over the screen: Dr. Ashford's portrait (her mouth moves while she talks) and the text box.
// A click or Enter shows the rest of the line; the next click moves on. Only one dialogue plays at a time (Active).
// The overlay catches clicks, so the screen underneath does not react while the story plays.
public sealed class DialogueBox : MonoBehaviour
{
    public static bool Active;

    const float TypeSpeed = 40f;       // characters per second
    const float TalkInterval = 0.12f;  // s between mouth movements while she talks

    int[] beats;
    int next, total, startFrame;
    Action<int> onBeat;
    Action onDone;
    TMP_Text header, body;
    Image portrait;
    Sprite closed, open;
    float typeAcc, talkTimer;
    bool typing, talkOpen;

    static private Dictionary<TMP_FontAsset, Material> OutlinedMaterials = new();

    // Plays the beats in order. onBeat runs before each beat (the plot scene uses it to change the background); onDone after the last.
    public static DialogueBox Play(int[] beats, Action onDone, Action<int> onBeat = null)
    {
        var box = new GameObject("Dialogue").AddComponent<DialogueBox>();
        box.beats = beats;
        box.onDone = onDone;
        box.onBeat = onBeat;
        box.startFrame = Time.frameCount;  // ignore the click that started this dialogue
        box.Build();
        Active = true;
        box.ShowNext();
        return box;
    }

    void Build()
    {
        var font = FontFactory.GetPixelifyFont();
        var canvasGo = new GameObject("DialogueCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvasGo.transform.SetParent(transform, false);
        var canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 300;
        var scaler = canvasGo.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Shrink;

        var stageGo = new GameObject("Stage", typeof(RectTransform));
        stageGo.transform.SetParent(canvasGo.transform, false);
        var stage = (RectTransform)stageGo.transform;
        stage.anchorMin = stage.anchorMax = stage.pivot = new Vector2(0.5f, 0.5f);
        stage.sizeDelta = new Vector2(1920f, 1080f);

        var catcher = At(stage, 0, 0, 1920, 1080, "Catcher").gameObject.AddComponent<Image>();
        catcher.color = Color.clear;

        // Low enough that her belt is just under the bottom edge: the legs are off-screen, the head and chest are in view.
        portrait = At(stage, 20, 0, 820, 820, "Portrait").gameObject.AddComponent<Image>();
        closed = LoadSprite("Assets/Sprites/1FINAL.png");
        open = LoadSprite("Assets/Sprites/2FINAL.png");
        portrait.sprite = closed;
        portrait.preserveAspect = true;
        portrait.raycastTarget = false;

        var boxImg = At(stage, 560, 40, 1320, 300, "TextBox").gameObject.AddComponent<Image>();
        boxImg.sprite = LoadSprite("Assets/Sprites/Dialogue.png");
        boxImg.preserveAspect = true;
        boxImg.raycastTarget = false;
        if (!boxImg.sprite) boxImg.color = new Color(0.6f, 0.6f, 0.6f);  // no art in this build: a plain box

        header = Label(stage, 650, 264, 900, 50, 32, font, Color.white, "");
        body = Label(stage, 650, 76, 1130, 186, 36, font, Color.white, "");
        var hint = Label(stage, 1400, 46, 420, 30, 22, font, new Color(0.25f, 0.15f, 0.05f), "click to continue");
        hint.alignment = TextAlignmentOptions.Right;
    }

    void Update()
    {
        bool fresh = Time.frameCount > startFrame;
        bool press = fresh && ((Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
                               || (Keyboard.current != null && Keyboard.current.enterKey.wasPressedThisFrame));

        if (typing)
        {
            typeAcc += Time.unscaledDeltaTime * TypeSpeed;
            int n = Mathf.Min(total, (int)typeAcc);
            body.maxVisibleCharacters = n;
            if (n >= total) typing = false;
        }
        if (press)
        {
            if (typing)
            {
                typing = false;
                body.maxVisibleCharacters = total;
            }
            else ShowNext();
        }

        // The mouth moves while the text types out, and rests when it stops.
        talkTimer -= Time.unscaledDeltaTime;
        if (typing && talkTimer <= 0f)
        {
            talkTimer = TalkInterval;
            talkOpen = !talkOpen;
        }
        if (!typing) talkOpen = false;
        portrait.sprite = talkOpen && open ? open : closed;
    }

    void ShowNext()
    {
        if (next >= beats.Length)
        {
            Finish();
            return;
        }
        int beat = beats[next++];
        onBeat?.Invoke(beat);
        var line = Story.Beats[beat];
        if (line.RevealsName) Story.RevealName();
        header.text = Story.NameKnown ? Story.Name : Story.Unknown;
        body.text = line.Text;
        total = line.Text.Length;
        body.maxVisibleCharacters = 0;
        typeAcc = 0f;
        typing = true;
    }

    void Finish()
    {
        Active = false;
        onDone?.Invoke();
        Destroy(gameObject);
    }

    // A rect in stage coordinates (1920x1080, bottom-left origin).
    static RectTransform At(RectTransform parent, float x, float y, float w, float h, string name)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = rt.anchorMax = rt.pivot = Vector2.zero;
        rt.anchoredPosition = new Vector2(x, y);
        rt.sizeDelta = new Vector2(w, h);
        return rt;
    }

    static TMP_Text Label(RectTransform stage, float x, float y, float w, float h, float size, TMP_FontAsset font, Color color, string text)
    {
        var t = At(stage, x, y, w, h, "Text").gameObject.AddComponent<TextMeshProUGUI>();
        t.font = font;
        t.fontSize = size;
        t.color = color;
        t.text = text;
        t.textWrappingMode = TextWrappingModes.Normal;
        t.raycastTarget = false;
        // A dark outline keeps the white text readable over the grey box.
        UseOutlineShader(t, font, new Color32(0, 0, 0, 255), 0.3f);
        return t;
    }

    // Runtime-created fonts get TextMeshPro's mobile shader, which has no outline. This gives the text a copy of the font's
    // material on the full Distance Field shader, which does.
    static void UseOutlineShader(TMP_Text t, TMP_FontAsset font, Color32 outlineColor, float thickness)
    {
        if (!OutlinedMaterials.TryGetValue(font, out var material) || !material)
        {
            var shader = Shader.Find("TextMeshPro/Distance Field");
            if (!shader)
            {
                Debug.LogWarning("DialogueBox: TextMeshPro/Distance Field shader not found, so the outline will not show.");
                return;
            }
            material = new Material(font.material) { shader = shader };
            material.EnableKeyword(ShaderUtilities.Keyword_Outline);
            material.SetColor(ShaderUtilities.ID_OutlineColor, outlineColor);
            material.SetFloat(ShaderUtilities.ID_OutlineWidth, thickness);
            material.SetFloat(ShaderUtilities.ID_FaceDilate, thickness);
            OutlinedMaterials[font] = material;
        }

        t.fontSharedMaterial = material;
        t.UpdateMeshPadding();
    }

    static Sprite LoadSprite(string path)
    {
#if UNITY_EDITOR
        foreach (var asset in UnityEditor.AssetDatabase.LoadAllAssetsAtPath(path))
            if (asset is Sprite s) return s;
#endif
        return null;
    }
}
