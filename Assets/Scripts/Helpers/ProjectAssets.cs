using UnityEngine;

// Art, the font and the music are loaded from Resources, so they work in a player build as well as in the editor.
// Paths are written relative to Resources, with or without the extension.
public static class ProjectAssets
{
    // The first sprite at a Resources path. Works for single and multiple sprite imports.
    public static Sprite LoadSprite(string path)
    {
        var sprites = Resources.LoadAll<Sprite>(System.IO.Path.ChangeExtension(path, null));
        return sprites.Length > 0 ? sprites[0] : null;
    }

    // Editor only: a project path, for AssetDatabase calls such as the sample rocket.
    public static string Full(string path) => "Assets/" + path;
}
