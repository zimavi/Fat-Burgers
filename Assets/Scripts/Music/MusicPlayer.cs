using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
#if UNITY_EDITOR
using UnityEditor;
#endif

// Background music, one track per scene. One persistent AudioSource is created before the first scene loads and survives
// scene changes. When a scene loads, its track starts (a scene that shares the current track keeps it playing, without a restart).
// The track loops for as long as the scene is open.
// shortcut: tracks load through AssetDatabase in the editor only. A player build needs the MP3s under Resources.
public sealed class MusicPlayer : MonoBehaviour
{
    // Scene name -> track file name (without the extension). Change the pairs here to move a track to another scene.
    static readonly Dictionary<string, string> SceneTracks = new Dictionary<string, string>
    {
        { "Plot", "CAR" },
        { "MissionSelect", "HOUSE" },
        { "BuilderScene", "ощи" },
        { "GameplayScene", "хтонь" },
    };

    const string MusicFolder = "Assets/Music";
    const float Volume = 0.25f;

    AudioSource source;
    readonly Dictionary<string, AudioClip> clips = new Dictionary<string, AudioClip>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Create()
    {
        var go = new GameObject("Music");
        DontDestroyOnLoad(go);
        go.AddComponent<MusicPlayer>();
    }

    void Awake()
    {
        source = gameObject.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.loop = true;  // the track loops while its scene is open
        source.volume = Volume;
        LoadClips();
        SceneManager.sceneLoaded += OnSceneLoaded;  // the first scene's load comes after this, so it gets its track too
    }

    void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        // A scene with no entry in the table keeps whatever is playing.
        if (!SceneTracks.TryGetValue(scene.name, out var trackName)) return;
        if (!clips.TryGetValue(trackName, out var clip))
        {
            Debug.LogWarning($"MusicPlayer: no track named '{trackName}' for scene {scene.name}.");
            return;
        }
        if (source.clip == clip && source.isPlaying) return;  // same track: carry on without a restart
        source.clip = clip;
        source.Play();
    }

    void LoadClips()
    {
#if UNITY_EDITOR
        foreach (var guid in AssetDatabase.FindAssets("t:AudioClip", new[] { MusicFolder }))
        {
            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(AssetDatabase.GUIDToAssetPath(guid));
            if (clip) clips[clip.name] = clip;
        }
#endif
        if (clips.Count == 0) Debug.LogWarning($"MusicPlayer: no tracks found in {MusicFolder}.");
    }
}
