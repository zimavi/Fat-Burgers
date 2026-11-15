using System.Collections.Generic;
using UnityEngine;

// CPU drawing into a texture for the plot screens. Redrawn when the data changes, not every frame.
// Pixel origin is bottom-left, like the texture itself.
public sealed class Raster
{
    public readonly int Width, Height;
    public readonly Texture2D Texture;
    readonly Color32[] pixels;

    public Raster(int width, int height)
    {
        Width = width;
        Height = height;
        pixels = new Color32[width * height];
        Texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
        };
    }

    public void Clear(Color background)
    {
        Color32 c = background;
        System.Array.Fill(pixels, c);
    }

    public void Apply()
    {
        Texture.SetPixels32(pixels);
        Texture.Apply(false);
    }

    public void Plot(Vector2 p, Color c)
    {
        int x = Mathf.RoundToInt(p.x), y = Mathf.RoundToInt(p.y);
        if (x < 0 || y < 0 || x >= Width || y >= Height) return;
        Color32 px = c;
        pixels[y * Width + x] = px;
    }

    // dash > 0 draws dotted: dash pixels on, dash pixels off.
    public void Line(Vector2 a, Vector2 b, Color c, int dash = 0)
    {
        int steps = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(a, b)));
        for (int i = 0; i <= steps; i++)
            if (dash == 0 || (i / dash) % 2 == 0) Plot(Vector2.Lerp(a, b, i / (float)steps), c);
    }

    // Connected points. The dash counts pixels along the whole path, so the dots stay even across corners.
    public void Polyline(IList<Vector2> pts, Color c, int dash = 0)
    {
        int along = 0;
        for (int k = 1; k < pts.Count; k++)
        {
            Vector2 a = pts[k - 1], b = pts[k];
            int steps = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(a, b)));
            for (int i = 0; i < steps; i++, along++)
                if (dash == 0 || (along / dash) % 2 == 0) Plot(Vector2.Lerp(a, b, i / (float)steps), c);
        }
    }

    public void Dot(Vector2 p, int radius, Color c)
    {
        for (int y = -radius; y <= radius; y++)
            for (int x = -radius; x <= radius; x++)
                Plot(p + new Vector2(x, y), c);
    }

    public void Dispose() => Object.Destroy(Texture);
}
