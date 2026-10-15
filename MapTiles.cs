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
        if (reference == null || reference.sprite == null)
        {
            Debug.LogWarning("[Navigator] tile " + name + " fell back: no reference tile '" + from["ref"] + "' on " + tilemapName + " (nothing is painted)");
            return null;
        }
        var pivotArr = from["pivot"] as JArray;
        var pivot = pivotArr != null && pivotArr.Count == 2 ? new Vector2(pivotArr[0].Value<float>(), pivotArr[1].Value<float>()) : new Vector2(0.5f, 0.5f);
        var ppu = from["ppu"]?.Value<float>() ?? reference.sprite.pixelsPerUnit;
        Sprite sprite;
        var rect = from["rect"] as JArray;
        var quarters = from["quarters"] as JArray;
        var pixels = from["pixels"] as JArray;
        if (pixels != null)
        {
            // A tile's own sprite with single pixels replaced by pixels of other tiles' sprites: [x, y, tile, sx, sy] in the
            // sprite's own units, y down. An inside corner joins the two edges beside it this way.
            Texture2D drawn = null;
            try { drawn = WithPixels(tilemapName, reference.sprite, pixels); }
            catch (Exception e) { Debug.LogWarning("[RechargeMaps] tile " + name + ": replacing its pixels failed: " + e.Message); }
            if (drawn == null)
            {
                Debug.LogWarning("[Navigator] tile " + name + " fell back to its base tile (pixel replacement failed)");
                return RealAssetPalette.GetTileByName(tilemapName, from["base"]?.Value<string>() ?? from["ref"]?.Value<string>());
            }
            var sz = reference.sprite.rect.size;
            pivot = new Vector2(reference.sprite.pivot.x / sz.x, reference.sprite.pivot.y / sz.y);
            sprite = Sprite.Create(drawn, new Rect(0, 0, drawn.width, drawn.height), pivot, reference.sprite.pixelsPerUnit);
        }
        else if (quarters != null && quarters.Count == 4)
        {
            // Top-left, top-right, bottom-left, bottom-right quarters, each cut
            // from that real tile's own sprite (wherever the game packed it).
            var pieces = quarters.Select(q => (RealAssetPalette.GetTileByName(tilemapName, q.Value<string>()) as Tile)?.sprite).ToArray();
            // Optional per-quarter turns ("x" mirrors left-right, "y" top-bottom, "xy" both): an inside corner is cut from an outer corner piece.
            var flipArr = from["flips"] as JArray;
            var flips = Enumerable.Range(0, 4).Select(i => flipArr != null && i < flipArr.Count ? flipArr[i].Value<string>() ?? "" : "").ToArray();
            if (pieces.Any(p => p == null))
            {
                Debug.LogWarning("[Navigator] tile " + name + " fell back: a quarter's source tile is missing (" + string.Join(",", quarters.Select(q => q.Value<string>())) + ") - nothing is painted");
                return null;
            }
            var size = reference.sprite.rect.size;
            var w = Mathf.RoundToInt(size.x);
            var h = Mathf.RoundToInt(size.y);
            Texture2D texture = null;
            // Always read back through a render texture: a straight GPU copy of a sub-rectangle is wrong on block-compressed
            // sheets, which gave 1-wide columns (left + right edge quarters) a see-through yellow smear.
            try { texture = QuartersByPixels(pieces, flips, w, h); }
            catch (Exception e) { Debug.LogWarning("[Navigator] tile " + name + ": reading its quarters failed (" + e.Message + "), drawing them instead"); }
            if (texture == null)
            {
                try { texture = QuartersByDrawing(pieces, flips, w, h); }
                catch (Exception e) { Debug.LogWarning("[RechargeMaps] tile " + name + ": drawing its quarters failed: " + e.Message); }
            }
            // Never a gap: the nearest whole tile of the set.
            if (texture == null)
            {
                Debug.LogWarning("[Navigator] tile " + name + " fell back to the whole tile '" + quarters[0].Value<string>() + "' (its quarters could not be built)");
                return RealAssetPalette.GetTileByName(tilemapName, quarters[0].Value<string>());
            }
            pivot = new Vector2(reference.sprite.pivot.x / size.x, reference.sprite.pivot.y / size.y);
            sprite = Sprite.Create(texture, new Rect(0, 0, w, h), pivot, reference.sprite.pixelsPerUnit);
        }
        else if (rect != null && rect.Count == 4)
        {
            sprite = Sprite.Create(reference.sprite.texture,
                new Rect(rect[0].Value<float>(), rect[1].Value<float>(), rect[2].Value<float>(), rect[3].Value<float>()), pivot, ppu);
        }
        else
        {
            Debug.LogWarning("[Navigator] tile " + name + " fell back: spriteFrom has no quarters, pixels or rect - nothing is painted");
            return null;
        }
        built = ScriptableObject.CreateInstance<Tile>();
        built.name = name;
        built.sprite = sprite;
        built.colliderType = Tile.ColliderType.Grid;
        Built[name] = built;
        return built;
    }

    // The four quarters copied straight from the sheets. Only for sprites the
    // atlas keeps as plain rectangles (tight or turned ones have no rectangle to
    // copy); a trimmed sprite's missing edge stays transparent.
    private static Texture2D QuartersByCopy(Sprite[] pieces, int w, int h)
    {
        int hw = w / 2, hh = h / 2;
        var parts = new List<(Texture2D, RectInt, Vector2Int)>();
        for (int i = 0; i < 4; i++)
        {
            var sp = pieces[i];
            if (sp.packed && (sp.packingMode != SpritePackingMode.Rectangle || sp.packingRotation != SpritePackingRotation.None)) return null;
            int col = i % 2, row = i < 2 ? 1 : 0; // texture rows count from the bottom
            var tr = sp.textureRect;
            var off = sp.textureRectOffset;
            // The quarter in the sprite's own rect, then where that lies in its trimmed texture rect.
            var q = new RectInt(col * hw, row * hh, hw, hh);
            int x0 = Mathf.Max(q.xMin, Mathf.RoundToInt(off.x)), y0 = Mathf.Max(q.yMin, Mathf.RoundToInt(off.y));
            int x1 = Mathf.Min(q.xMax, Mathf.RoundToInt(off.x + tr.width)), y1 = Mathf.Min(q.yMax, Mathf.RoundToInt(off.y + tr.height));
            if (x1 <= x0 || y1 <= y0) continue;
            parts.Add((sp.texture, new RectInt(Mathf.RoundToInt(tr.x) + x0 - Mathf.RoundToInt(off.x), Mathf.RoundToInt(tr.y) + y0 - Mathf.RoundToInt(off.y), x1 - x0, y1 - y0), new Vector2Int(x0, y0)));
        }
        return parts.Count > 0 ? ComposeTexture(w, h, parts) : null;
    }

    // A sprite's pixels as a w x h buffer, bottom row first; null for a packed sprite that is not a plain rectangle.
    private static Color32[] SpritePixels(Sprite sp, int w, int h)
    {
        if (sp.packed && (sp.packingMode != SpritePackingMode.Rectangle || sp.packingRotation != SpritePackingRotation.None)) return null;
        var buf = new Color32[w * h];
        var tr = sp.textureRect;
        var off = sp.textureRectOffset;
        var px = ReadRect(sp.texture, new RectInt(Mathf.RoundToInt(tr.x), Mathf.RoundToInt(tr.y), Mathf.RoundToInt(tr.width), Mathf.RoundToInt(tr.height)));
        int rw = Mathf.RoundToInt(tr.width), rh = Mathf.RoundToInt(tr.height), ox = Mathf.RoundToInt(off.x), oy = Mathf.RoundToInt(off.y);
        for (int y = 0; y < rh; y++) for (int x = 0; x < rw; x++)
        {
            int bx = x + ox, by = y + oy;
            if (bx >= 0 && bx < w && by >= 0 && by < h) buf[by * w + bx] = px[y * rw + x];
        }
        return buf;
    }

    private static Texture2D WithPixels(string tilemapName, Sprite basis, JArray pixels)
    {
        int w = Mathf.RoundToInt(basis.rect.width), h = Mathf.RoundToInt(basis.rect.height);
        var outPx = SpritePixels(basis, w, h);
        if (outPx == null) return null;
        var cache = new Dictionary<string, Color32[]>();
        foreach (var p in pixels)
        {
            var name = p[2].Value<string>();
            if (!cache.TryGetValue(name, out var src))
            {
                var sp = (RealAssetPalette.GetTileByName(tilemapName, name) as Tile)?.sprite;
                cache[name] = src = sp != null ? SpritePixels(sp, w, h) : null;
            }
            if (src == null) continue;
            int x = p[0].Value<int>(), y = p[1].Value<int>(), sx = p[3].Value<int>(), sy = p[4].Value<int>();
            if (x < 0 || x >= w || y < 0 || y >= h || sx < 0 || sx >= w || sy < 0 || sy >= h) continue;
            outPx[(h - 1 - y) * w + x] = src[(h - 1 - sy) * w + sx];
        }
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
        tex.SetPixels32(outPx);
        tex.Apply();
        return tex;
    }

    // Like QuartersByCopy, for quarters that are turned: each quarter's source quadrant is read back (through a render
    // texture, so unreadable sheets work) and its pixels mirrored into place.
    private static Texture2D QuartersByPixels(Sprite[] pieces, string[] flips, int w, int h)
    {
        int hw = w / 2, hh = h / 2;
        var outPx = new Color32[w * h];
        for (int i = 0; i < 4; i++)
        {
            var sp = pieces[i];
            if (sp.packed && (sp.packingMode != SpritePackingMode.Rectangle || sp.packingRotation != SpritePackingRotation.None)) return null;
            int col = i % 2, row = i < 2 ? 1 : 0;
            bool fx = flips[i].Contains("x"), fy = flips[i].Contains("y");
            int sc = fx ? 1 - col : col, sr = fy ? 1 - row : row;
            var tr = sp.textureRect;
            var off = sp.textureRectOffset;
            var q = new RectInt(sc * hw, sr * hh, hw, hh);
            int x0 = Mathf.Max(q.xMin, Mathf.RoundToInt(off.x)), y0 = Mathf.Max(q.yMin, Mathf.RoundToInt(off.y));
            int x1 = Mathf.Min(q.xMax, Mathf.RoundToInt(off.x + tr.width)), y1 = Mathf.Min(q.yMax, Mathf.RoundToInt(off.y + tr.height));
            if (x1 <= x0 || y1 <= y0) continue;
            var from = new RectInt(Mathf.RoundToInt(tr.x) + x0 - Mathf.RoundToInt(off.x), Mathf.RoundToInt(tr.y) + y0 - Mathf.RoundToInt(off.y), x1 - x0, y1 - y0);
            var px = ReadRect(sp.texture, from);
            for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++)
            {
                int lx = x - q.xMin, ly = y - q.yMin; // in the source quadrant
                int tx = fx ? hw - 1 - lx : lx, ty = fy ? hh - 1 - ly : ly; // in the target quadrant
                outPx[(row * hh + ty) * w + col * hw + tx] = px[(y - y0) * from.width + (x - x0)];
            }
        }
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
        tex.SetPixels32(outPx);
        tex.Apply();
        return tex;
    }

    private static Color32[] ReadRect(Texture2D src, RectInt from)
    {
        var rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        var previous = RenderTexture.active;
        try
        {
            Graphics.Blit(src, rt);
            RenderTexture.active = rt;
            var read = new Texture2D(from.width, from.height, TextureFormat.RGBA32, false);
            read.ReadPixels(new Rect(from.x, from.y, from.width, from.height), 0, 0);
            var px = read.GetPixels32();
            UnityEngine.Object.Destroy(read);
            return px;
        }
        finally
        {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);
        }
    }

    // The four quarters drawn from each sprite's own mesh into a render
    // texture - whatever way the atlas packed them - then read back.
    private static Material _drawMaterial;
    private static Texture2D QuartersByDrawing(Sprite[] pieces, string[] flips, int w, int h)
    {
        if (_drawMaterial == null)
        {
            var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default") ?? Shader.Find("Unlit/Transparent");
            if (shader == null) return null;
            _drawMaterial = new Material(shader);
        }
        var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        var previous = RenderTexture.active;
        try
        {
            RenderTexture.active = rt;
            GL.Clear(true, true, Color.clear);
            GL.PushMatrix();
            for (int i = 0; i < 4; i++)
            {
                var sp = pieces[i];
                int col = i % 2, row = i < 2 ? 1 : 0;
                GL.Viewport(new Rect(col * w / 2f, row * h / 2f, w / 2f, h / 2f));
                // The sprite's own quarter (col, row), in its local units.
                var ppu = sp.pixelsPerUnit;
                float minX = -sp.pivot.x / ppu, minY = -sp.pivot.y / ppu, sw = sp.rect.width / ppu, sh = sp.rect.height / ppu;
                // A turned quarter shows the sprite's opposite quarter, mirrored (a reversed projection).
                bool fx = flips[i].Contains("x"), fy = flips[i].Contains("y");
                int sc = fx ? 1 - col : col, sr = fy ? 1 - row : row;
                float x0 = minX + sc * sw / 2f, x1 = minX + (sc + 1) * sw / 2f, y0 = minY + sr * sh / 2f, y1 = minY + (sr + 1) * sh / 2f;
                GL.LoadProjectionMatrix(Matrix4x4.Ortho(fx ? x1 : x0, fx ? x0 : x1, fy ? y1 : y0, fy ? y0 : y1, -1f, 1f));
                GL.modelview = Matrix4x4.identity;
                _drawMaterial.mainTexture = sp.texture;
                _drawMaterial.SetPass(0);
                GL.Begin(GL.TRIANGLES);
                GL.Color(Color.white);
                var v = sp.vertices;
                var uv = sp.uv;
                foreach (var t in sp.triangles)
                {
                    GL.TexCoord2(uv[t].x, uv[t].y);
                    GL.Vertex3(v[t].x, v[t].y, 0f);
                }
                GL.End();
            }
            GL.PopMatrix();
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            return tex;
        }
        finally
        {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);
        }
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
