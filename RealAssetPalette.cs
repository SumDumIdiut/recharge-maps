using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.Tilemaps;

// Opportunistically finds and caches one real instance of each gameplay component
// the map editor needs to clone from, across whatever scenes the player visits.
// The cache is cumulative for the whole play session (some types, like the
// atom/currency system, only live in specific scenes - e.g. the main menu).
internal static class RealAssetPalette
{
    private static readonly Dictionary<Type, Component> Templates = new Dictionary<Type, Component>();
    private static readonly Dictionary<string, Tilemap> TilemapTemplates = new Dictionary<string, Tilemap>();
    private static readonly Dictionary<string, List<TileBase>> TilePalettes = new Dictionary<string, List<TileBase>>();
    private static readonly string[] TilemapNames = { "ground", "blueBlocks", "orangeBlocks" };
    private static GameObject _holder;

    public static Vector3 GroundCellSize { get; private set; } = new Vector3(32f, 32f, 1f);

    private static readonly Type[] ScanTypes =
    {
        typeof(spikeScript),
        typeof(checkpointScript),
        typeof(startGate),
        typeof(endGate),
        typeof(PlatformMover),
        typeof(SpringScript),
        typeof(courseScript),
    };

    private static GameObject Holder
    {
        get
        {
            if (_holder == null)
            {
                _holder = new GameObject("RechargeMaps_AssetTemplates");
                _holder.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(_holder);
            }
            return _holder;
        }
    }

    public static void ScanCurrentScene()
    {
        foreach (var type in ScanTypes)
        {
            if (Templates.ContainsKey(type)) continue;
            var found = FindLiveInstance(type);
            if (found == null) continue;

            if (type == typeof(courseScript))
            {
                LogCourseScriptShape(found.transform);
            }

            var clone = UnityEngine.Object.Instantiate(found.gameObject, Holder.transform);
            clone.name = type.Name + "_Template";
            Templates[type] = clone.GetComponent(type);
            Debug.Log("[RechargeMaps] cached template for " + type.Name + " from " + GetPath(found.transform));
        }

        ScanTilemaps();
        ScanDecoProps();
        LogGroundDiagnostics();
        LogSwapperDiagnostics();
        LogPlatformMoverCount();
    }

    private static readonly Dictionary<string, Sprite> DecoSprites = new Dictionary<string, Sprite>();
    private static bool _treeDecoExported;

    // Decorative props (trees, etc.) - scoped to a static-sprite placeable for
    // v1, not a full replica of TreeController's hand-coded grow animation
    // (it has no Animator/AnimationClip at all - real growth is a code-driven
    // scale/position tween per trunk, confirmed via decompile). Caches the
    // real live Sprite reference (for runtime spawning) AND exports a PNG
    // (for the out-of-process Recharge editor's palette), same split as tiles.
    private static void ScanDecoProps()
    {
        if (_treeDecoExported) return;
        var tree = FindLiveInstance(typeof(TreeController)) as TreeController;
        if (tree == null) return;

        var dir = Path.Combine(MapPaths.TexturesDir, "tree");
        Directory.CreateDirectory(dir);
        var names = new List<string>();
        int i = 0;
        foreach (var sr in tree.GetComponentsInChildren<SpriteRenderer>(true))
        {
            if (sr.sprite == null || DecoSprites.ContainsKey(sr.gameObject.name)) continue;
            DecoSprites[sr.gameObject.name] = sr.sprite;
            names.Add(sr.gameObject.name);
            var pngPath = Path.Combine(dir, i + ".png");
            if (!File.Exists(pngPath))
            {
                try
                {
                    var bytes = ExtractSpritePng(sr.sprite);
                    if (bytes != null) File.WriteAllBytes(pngPath, bytes);
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[RechargeMaps] tree deco export failed for '" + sr.gameObject.name + "': " + e.Message);
                }
            }
            i++;
            if (i >= 8) break; // representative art, not every internal trunk/light part
        }
        File.WriteAllText(Path.Combine(dir, "manifest.json"), "[" + string.Join(",", names.Select(n => "\"" + n.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"")) + "]");
        _treeDecoExported = true;
        Debug.Log("[RechargeMaps] exported " + names.Count + " tree deco textures");
    }

    public static Sprite GetDecoSprite(string name)
    {
        return DecoSprites.TryGetValue(name, out var s) ? s : null;
    }

    private static void LogCourseScriptShape(Transform courseTransform)
    {
        Debug.Log("[RechargeMaps] courseScript found at " + GetPath(courseTransform) + " with " + courseTransform.childCount + " direct children:");
        foreach (Transform child in courseTransform)
        {
            var comps = child.GetComponents<Component>().Select(c => c.GetType().Name);
            Debug.Log("[RechargeMaps]   child: " + child.name + " components=[" + string.Join(",", comps) + "] grandchildren=" + child.childCount);

            if (child.name == "DisableBits")
            {
                foreach (Transform gc in child)
                {
                    var gcComps = gc.GetComponents<Component>().Select(c => c.GetType().Name);
                    Debug.Log("[RechargeMaps]     DisableBits child: " + gc.name + " components=[" + string.Join(",", gcComps) + "]");
                }
            }
        }

        var parent = courseTransform.parent;
        if (parent != null)
        {
            Debug.Log("[RechargeMaps] courseScript's parent '" + parent.name + "' has " + parent.childCount + " children (siblings of the course root):");
            foreach (Transform sib in parent)
            {
                Debug.Log("[RechargeMaps]   sibling: " + sib.name);
            }
        }
    }

    private static void ScanTilemaps()
    {
        foreach (var name in TilemapNames)
        {
            if (TilemapTemplates.ContainsKey(name)) continue;

            Tilemap found = null;
            foreach (var tm in Resources.FindObjectsOfTypeAll<Tilemap>())
            {
                if (tm.gameObject.scene.IsValid() && tm.gameObject.name == name) { found = tm; break; }
            }
            if (found == null) continue;

            if (found.layoutGrid != null) GroundCellSize = found.layoutGrid.cellSize;
            Debug.Log("[RechargeMaps] tilemap '" + name + "' cellSize=" + found.cellSize + " transform.localScale=" + found.transform.localScale + " layoutGrid.cellSize=" + (found.layoutGrid != null ? found.layoutGrid.cellSize.ToString() : "n/a"));

            var tiles = new List<TileBase>();
            foreach (var t in found.GetTilesBlock(found.cellBounds))
            {
                if (t != null && !tiles.Contains(t)) tiles.Add(t);
            }
            TilePalettes[name] = tiles;
            Debug.Log("[RechargeMaps] tilemap '" + name + "' at " + GetPath(found.transform) + " has " + tiles.Count + " distinct real tiles: " + string.Join(",", tiles.Select(t => t.name)));

            var clone = UnityEngine.Object.Instantiate(found.gameObject, Holder.transform);
            clone.name = name + "_TilemapTemplate";
            var cloneTilemap = clone.GetComponent<Tilemap>();
            cloneTilemap.ClearAllTiles();
            TilemapTemplates[name] = cloneTilemap;

            ExportTileTextures(name, tiles);
            ExportTileRules(name, tiles);
        }
    }

    // Real ground/block art is authored as Unity RuleTiles (confirmed:
    // Unity.2D.Tilemap.Extras.dll ships with the game) - each numbered tile
    // we already extract as a flat PNG may ALSO carry the artist's real
    // neighbor-matching rule(s) (the "side/inner/corner" structure), which is
    // the ground truth for auto-tiling instead of guessing from pixel art.
    // One entry per tile index, parallel to manifest.json; null if the tile
    // isn't a RuleTile or has no rules.
    private static void ExportTileRules(string tilemapName, List<TileBase> tiles)
    {
        var dir = Path.Combine(MapPaths.TexturesDir, tilemapName);
        Directory.CreateDirectory(dir);
        var rulesPath = Path.Combine(dir, "rules.json");
        if (File.Exists(rulesPath)) return; // already exported this session/run

        var sb = new System.Text.StringBuilder();
        sb.Append("[");
        int ruleBearingCount = 0;
        for (int i = 0; i < tiles.Count; i++)
        {
            if (i > 0) sb.Append(",");
            var rt = tiles[i] as RuleTile;
            if (rt == null || rt.m_TilingRules == null || rt.m_TilingRules.Count == 0)
            {
                sb.Append("null");
                continue;
            }
            ruleBearingCount++;
            sb.Append("{\"rules\":[");
            for (int r = 0; r < rt.m_TilingRules.Count; r++)
            {
                if (r > 0) sb.Append(",");
                var rule = rt.m_TilingRules[r];
                sb.Append("{\"neighbors\":[");
                int count = Math.Min(rule.m_Neighbors.Count, rule.m_NeighborPositions.Count);
                for (int n = 0; n < count; n++)
                {
                    if (n > 0) sb.Append(",");
                    var pos = rule.m_NeighborPositions[n];
                    sb.Append("{\"dx\":").Append(pos.x).Append(",\"dy\":").Append(pos.y).Append(",\"cond\":").Append(rule.m_Neighbors[n]).Append("}");
                }
                sb.Append("],\"transform\":\"").Append(rule.m_RuleTransform).Append("\",\"output\":\"").Append(rule.m_Output)
                  .Append("\",\"spriteCount\":").Append(rule.m_Sprites != null ? rule.m_Sprites.Length : 0).Append("}");
            }
            sb.Append("]}");
        }
        sb.Append("]");
        File.WriteAllText(rulesPath, sb.ToString());
        Debug.Log("[RechargeMaps] exported tiling rules for '" + tilemapName + "': " + ruleBearingCount + " rule-bearing tiles of " + tiles.Count);
    }

    // So the Recharge editor (a separate process, no live game access) can show
    // real tile art in its palette instead of flat color swatches. Extraction
    // uses a RenderTexture blit + ReadPixels round-trip specifically because the
    // source atlas texture is very likely not marked Read/Write Enabled - GetPixels()
    // would throw directly, but blitting to a RenderTexture and reading that back
    // works regardless of the source texture's own readability flag.
    private static void ExportTileTextures(string tilemapName, List<TileBase> tiles)
    {
        var dir = Path.Combine(MapPaths.TexturesDir, tilemapName);
        Directory.CreateDirectory(dir);

        var names = new List<string>();
        for (int i = 0; i < tiles.Count; i++)
        {
            names.Add(tiles[i] != null ? tiles[i].name : ("tile_" + i));
            var pngPath = Path.Combine(dir, i + ".png");
            if (File.Exists(pngPath)) continue; // already exported this session/run

            var sprite = (tiles[i] as Tile)?.sprite;
            if (sprite == null) continue;

            try
            {
                var bytes = ExtractSpritePng(sprite);
                if (bytes != null) File.WriteAllBytes(pngPath, bytes);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RechargeMaps] texture export failed for '" + names[i] + "': " + e.Message);
            }
        }

        var manifestPath = Path.Combine(dir, "manifest.json");
        var manifestJson = "[" + string.Join(",", names.Select(n => "\"" + n.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"")) + "]";
        File.WriteAllText(manifestPath, manifestJson);
    }

    private static byte[] ExtractSpritePng(Sprite sprite)
    {
        var srcTex = sprite.texture;
        var rect = sprite.textureRect;
        var rt = RenderTexture.GetTemporary(srcTex.width, srcTex.height, 0, RenderTextureFormat.ARGB32);
        var prevActive = RenderTexture.active;
        Graphics.Blit(srcTex, rt);
        RenderTexture.active = rt;
        var readable = new Texture2D((int)rect.width, (int)rect.height, TextureFormat.RGBA32, false);
        readable.ReadPixels(rect, 0, 0);
        readable.Apply();
        RenderTexture.active = prevActive;
        RenderTexture.ReleaseTemporary(rt);
        var png = readable.EncodeToPNG();
        UnityEngine.Object.Destroy(readable);
        return png;
    }

    private static bool _springFramesCaptured;

    // AnimationUtility (the normal way to read an AnimationClip's keyframe
    // sprites) is a UnityEditor-only API, unavailable in a built game. Instead
    // this actually plays the spring's real activation animation on a spare
    // clone and samples SpriteRenderer.sprite every frame, exporting each
    // distinct sprite encountered - a real recording, not static analysis.
    public static IEnumerator CaptureSpringAnimation()
    {
        if (_springFramesCaptured) yield break;
        var dir = Path.Combine(MapPaths.TexturesDir, "spring");
        if (File.Exists(Path.Combine(dir, "manifest.json"))) { _springFramesCaptured = true; yield break; }

        var template = Get<SpringScript>();
        if (template == null) yield break;

        var spawnPos = new Vector3(60000f, 60000f, 0f); // separate, far from the map-editing pocket too
        var clone = UnityEngine.Object.Instantiate(template.gameObject, spawnPos, Quaternion.identity);
        clone.SetActive(true);

        var sr = clone.GetComponentInChildren<SpriteRenderer>();
        var animField = typeof(SpringScript).GetField("anim", BindingFlags.NonPublic | BindingFlags.Instance);
        var animator = animField?.GetValue(clone.GetComponent<SpringScript>()) as Animator;

        if (sr == null || animator == null)
        {
            Debug.LogWarning("[RechargeMaps] spring animation capture: missing SpriteRenderer or Animator");
            UnityEngine.Object.Destroy(clone);
            yield break;
        }

        // The clone is spawned far from any camera - by default an Animator
        // stops advancing when nothing renders it (visibility-based culling),
        // which otherwise freezes normalizedTime at 0 forever. Confirmed via
        // diagnostic logging showing normTime stuck at 0.00 for the full 2s window.
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

        var frames = new List<Sprite>();
        yield return null; // let Start() run (ForceStateNormalizedTime resting pose)
        if (sr.sprite != null) frames.Add(sr.sprite);

        animator.SetTrigger("Trigger"); // same trigger the real hit does

        float elapsed = 0f;
        while (elapsed < 2f)
        {
            yield return null;
            elapsed += Time.unscaledDeltaTime;
            if (sr.sprite != null && !frames.Contains(sr.sprite)) frames.Add(sr.sprite);
        }

        Directory.CreateDirectory(dir);
        var names = new List<string>();
        for (int i = 0; i < frames.Count; i++)
        {
            names.Add("frame_" + i);
            try
            {
                var bytes = ExtractSpritePng(frames[i]);
                if (bytes != null) File.WriteAllBytes(Path.Combine(dir, i + ".png"), bytes);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RechargeMaps] spring frame export failed: " + e.Message);
            }
        }
        File.WriteAllText(Path.Combine(dir, "manifest.json"), "[" + string.Join(",", names.Select(n => "\"" + n + "\"")) + "]");
        Debug.Log("[RechargeMaps] captured " + frames.Count + " spring animation frames");

        UnityEngine.Object.Destroy(clone);
        _springFramesCaptured = true;
    }

    private static void LogPlatformMoverCount()
    {
        var all = Resources.FindObjectsOfTypeAll<PlatformMover>();
        var sceneValid = all.Count(p => p.gameObject.scene.IsValid());
        Debug.Log("[RechargeMaps] PlatformMover instances in memory: " + all.Length + " (scene-valid: " + sceneValid + ")");
    }

    private static Component FindLiveInstance(Type type)
    {
        var all = Resources.FindObjectsOfTypeAll(type);
        foreach (var obj in all)
        {
            var comp = obj as Component;
            if (comp != null && comp.gameObject.scene.IsValid())
            {
                return comp;
            }
        }
        return null;
    }

    private static void LogGroundDiagnostics()
    {
        var grounds = GameObject.FindGameObjectsWithTag("Ground");
        Debug.Log("[RechargeMaps] found " + grounds.Length + " active Ground-tagged objects in scene " + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name);
        foreach (var go in grounds.Take(5))
        {
            var col = go.GetComponent<Collider2D>();
            Debug.Log("[RechargeMaps]   ground: " + GetPath(go.transform) + " layer=" + LayerMask.LayerToName(go.layer) + " collider=" + (col != null ? col.GetType().Name : "none"));
        }
    }

    private static void LogSwapperDiagnostics()
    {
        var swapper = Singleton<colouredBlockSwapper>.Instance;
        Debug.Log("[RechargeMaps] colouredBlockSwapper.Instance = " + (swapper != null ? GetPath(swapper.transform) : "null"));
    }

    public static T Get<T>() where T : Component
    {
        return Templates.TryGetValue(typeof(T), out var comp) ? (T)comp : null;
    }

    public static Tilemap GetTilemapTemplate(string name)
    {
        return TilemapTemplates.TryGetValue(name, out var tm) ? tm : null;
    }

    public static TileBase GetTile(string tilemapName, int index)
    {
        if (!TilePalettes.TryGetValue(tilemapName, out var tiles) || index < 0 || index >= tiles.Count) return null;
        return tiles[index];
    }

    public static TileBase GetTileByName(string tilemapName, string tileName)
    {
        if (!TilePalettes.TryGetValue(tilemapName, out var tiles)) return null;
        return tiles.FirstOrDefault(t => t.name == tileName);
    }

    public static T Spawn<T>(Vector3 worldPos, Quaternion rotation, Transform parent) where T : Component
    {
        var template = Get<T>();
        if (template == null) return null;
        var clone = UnityEngine.Object.Instantiate(template.gameObject, worldPos, rotation, parent);
        clone.SetActive(true);
        return clone.GetComponent<T>();
    }

    public static string GetPath(Transform t)
    {
        var parts = new List<string>();
        while (t != null)
        {
            parts.Add(t.name);
            t = t.parent;
        }
        parts.Reverse();
        return string.Join("/", parts);
    }
}
