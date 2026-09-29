using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Tilemaps;

// The tiles maps paint: the game's own by name, ones built from pieces of
// them for shapes the level never uses, and the plain block the editor places.
internal static class MapTiles
{
    // A real tile by name - or, for one the level never places (so the game has
    // no Tile for it, e.g. a lone block), one built from tiles it does have:
    // { ref, quarters: [4 tile names] } puts together the top-left, top-right,
    // bottom-left and bottom-right quarters of those tiles; { ref, rect, ppu,
    // pivot } cuts a rect from ref's sheet.
    private static readonly Dictionary<string, Tile> Built = new Dictionary<string, Tile>();
    public static TileBase Resolve(string tilemapName, JObject obj)
    {
        var name = obj["tileName"]?.Value<string>();
        var from = obj["spriteFrom"] as JObject;
        // A tile that brings its own sprite is always built from it, even if the
        // game happens to have a tile by that name.
        if (from == null) return name != null ? RealAssetPalette.GetTileByName(tilemapName, name) : null;
        if (name == null) return null;
        if (Built.TryGetValue(name, out var built) && built != null) return built;
        var reference = RealAssetPalette.GetTileByName(tilemapName, from["ref"]?.Value<string>()) as Tile;
        if (reference == null || reference.sprite == null) return null;
        var pivotArr = from["pivot"] as JArray;
        var pivot = pivotArr != null && pivotArr.Count == 2 ? new Vector2(pivotArr[0].Value<float>(), pivotArr[1].Value<float>()) : new Vector2(0.5f, 0.5f);
        var ppu = from["ppu"]?.Value<float>() ?? reference.sprite.pixelsPerUnit;
        Sprite sprite;
        var rect = from["rect"] as JArray;
        var quarters = from["quarters"] as JArray;
        if (quarters != null && quarters.Count == 4)
        {
            // Top-left, top-right, bottom-left, bottom-right quarters, each cut
            // from that real tile's own sprite (wherever the game packed it).
            var pieces = quarters.Select(q => (RealAssetPalette.GetTileByName(tilemapName, q.Value<string>()) as Tile)?.sprite).ToArray();
            if (pieces.Any(p => p == null)) return null;
            var size = reference.sprite.rect.size;
            var w = Mathf.RoundToInt(size.x);
            var h = Mathf.RoundToInt(size.y);
            int hw = w / 2, hh = h / 2;
            var parts = new List<(Texture2D, RectInt, Vector2Int)>();
            for (int i = 0; i < 4; i++)
            {
                int col = i % 2, row = i < 2 ? 1 : 0; // texture rows count from the bottom
                var tr = pieces[i].textureRect;
                parts.Add((pieces[i].texture, new RectInt(Mathf.RoundToInt(tr.x) + col * hw, Mathf.RoundToInt(tr.y) + row * hh, hw, hh), new Vector2Int(col * hw, row * hh)));
            }
            var texture = ComposeTexture(w, h, parts);
            if (texture == null) return null;
            pivot = new Vector2(reference.sprite.pivot.x / size.x, reference.sprite.pivot.y / size.y);
            sprite = Sprite.Create(texture, new Rect(0, 0, w, h), pivot, reference.sprite.pixelsPerUnit);
        }
        else if (rect != null && rect.Count == 4)
        {
            sprite = Sprite.Create(reference.sprite.texture,
                new Rect(rect[0].Value<float>(), rect[1].Value<float>(), rect[2].Value<float>(), rect[3].Value<float>()), pivot, ppu);
        }
        else return null;
        built = ScriptableObject.CreateInstance<Tile>();
        built.name = name;
        built.sprite = sprite;
        built.colliderType = Tile.ColliderType.Grid;
        Built[name] = built;
        return built;
    }

    // A new texture made of rectangles copied from sprite sheets (texture
    // pixels, bottom-left origin). A straight GPU copy keeps the sheets' exact
    // pixels; if the formats won't allow it, read them back through a render texture.
    private static Texture2D ComposeTexture(int w, int h, List<(Texture2D src, RectInt from, Vector2Int to)> parts)
    {
        var first = parts[0].src;
        if (SystemInfo.copyTextureSupport != UnityEngine.Rendering.CopyTextureSupport.None && parts.All(p => p.src.format == first.format))
        {
            try
            {
                var copy = new Texture2D(w, h, first.format, false) { filterMode = first.filterMode, wrapMode = TextureWrapMode.Clamp };
                foreach (var p in parts) Graphics.CopyTexture(p.src, 0, 0, p.from.x, p.from.y, p.from.width, p.from.height, copy, 0, 0, p.to.x, p.to.y);
                return copy;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RechargeMaps] composed tile copy failed, reading back instead: " + e.Message);
            }
        }
        var read = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = first.filterMode, wrapMode = TextureWrapMode.Clamp };
        var previous = RenderTexture.active;
        foreach (var p in parts)
        {
            var rt = RenderTexture.GetTemporary(p.src.width, p.src.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            try
            {
                Graphics.Blit(p.src, rt);
                RenderTexture.active = rt;
                read.ReadPixels(new Rect(p.from.x, p.from.y, p.from.width, p.from.height), p.to.x, p.to.y);
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
            }
        }
        read.Apply();
        return read;
    }

    // Author-placed blocks: a plain grey tile (the map maker's block colour)
    // with a full-cell collider, so they read as "yours" against the real art.
    private static Tile _blankGround;
    public static Tile BlankGround()
    {
        if (_blankGround != null) return _blankGround;
        var size = Mathf.Max(1, Mathf.RoundToInt(RealAssetPalette.GroundCellSize.x));
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
        var pixels = new Color32[size * size];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(0x4a, 0x4d, 0x44, 0xff);
        tex.SetPixels32(pixels);
        tex.Apply();
        _blankGround = ScriptableObject.CreateInstance<Tile>();
        _blankGround.name = "RechargeBlankGround";
        _blankGround.sprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 1f);
        _blankGround.colliderType = Tile.ColliderType.Grid;
        return _blankGround;
    }
}
