using System;
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// The opening scene: Dr. Ashford's welcome, outside the building and then inside the Vertical Assembly Building.
// It plays once, then goes to the mission select. Attached by PlotSceneBootstrap when the Plot scene loads.
public class PlotScene : MonoBehaviour
{
    const string OutsidePath = "Sprites/IMG_2484.PNG";
    const string InsidePath = "Sprites/IMG_2483.PNG";

    Image background, fade;

    void Start()
    {
        if (Story.Seen("intro"))
        {
            SceneManager.LoadScene("MissionSelect");
            return;
        }

        var canvasGo = new GameObject("PlotCanvas", typeof(Canvas), typeof(CanvasScaler));
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

        // Black behind, then the scene art, fitted to the stage.
        var backdrop = Fill(stage, "Backdrop");
        backdrop.color = Color.black;
        background = Fill(stage, "Background");
        background.preserveAspect = true;
        background.sprite = LoadSprite(OutsidePath);

        // Outside: the first four lines. The change to inside is a fade that starts after "Come on in", then the last line.
        fade = Fill(stage, "Fade");
        fade.color = new Color(0f, 0f, 0f, 0f);
        DialogueBox.Play(new[] { 0, 1, 2, 3 }, () => StartCoroutine(Transition(
            () => background.sprite = LoadSprite(InsidePath),
            () => DialogueBox.Play(new[] { 4 }, Finish))));
    }

    void Finish()
    {
        Story.MarkSeen("intro");
        StartCoroutine(Fade(1f, 0.8f, () => SceneManager.LoadScene("MissionSelect")));
    }

    // Fades to black, runs the midpoint (the scene change), then fades back in.
    IEnumerator Transition(Action midpoint, Action done)
    {
        yield return Fade(1f, 0.6f, null);
        midpoint?.Invoke();
        yield return Fade(0f, 0.6f, null);
        done?.Invoke();
    }

    IEnumerator Fade(float target, float seconds, Action done)
    {
        float start = fade.color.a;
        for (float t = 0f; t < seconds; t += Time.unscaledDeltaTime)
        {
            fade.color = new Color(0f, 0f, 0f, Mathf.Lerp(start, target, t / seconds));
            yield return null;
        }
        fade.color = new Color(0f, 0f, 0f, target);
        done?.Invoke();
    }

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

    static Sprite LoadSprite(string path) => ProjectAssets.LoadSprite(path);
}

// Attaches the plot scene when its scene loads.
public static class PlotSceneBootstrap
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Attach()
    {
        if (SceneManager.GetActiveScene().name == "Plot")
            new GameObject("PlotScene").AddComponent<PlotScene>();
    }
}
