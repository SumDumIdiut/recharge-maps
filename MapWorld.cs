using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Recharge.ModApi;
using UnityEngine;
using UnityEngine.Tilemaps;

// One loaded map: where it sits, what it changed, and how to take it back out.
//
// Overlay maps are edits to the real world at the game's own coordinates
// (origin 0) and paint the real tilemaps. Custom maps are built from nothing
// in the far-away pocket and paint their own copies of those tilemaps. Past
// that, both load the same way - only Origin and Tilemap() differ.
//
// Coordinates: map objects give level space (the game's coordinates, relative
// to Origin); the world is shifted by FloatingOrigin, so Live() converts.
internal class MapWorld
{
    // Far from any real geometry (~-10000..20000).
    public static readonly Vector2 PocketOrigin = new Vector2(50000f, 50000f);
    private const string RootPrefix = "RechargeMap_";

    public readonly string MapId;
    public readonly MapDefinition Def;
    public readonly MapGroup Group;
    public readonly bool Overlay;
    public readonly Vector2 Origin;
    public readonly Transform Root;
    // Custom maps are laid out in the editor's level space and moved to the
    // pocket: level point + Shift is where it sits. (Overlays: no shift.)
    private readonly Vector2 _shift;
    private const float EditorOffsetY = 9f;

    // Collected while objects spawn, resolved once everything exists.
    public readonly List<(GameObject go, string course)> Links = new List<(GameObject, string)>();
    public readonly List<(GameObject go, JObject cfg)> Teleports = new List<(GameObject, JObject)>();
    public readonly Dictionary<string, Sprite> CustomImages = new Dictionary<string, Sprite>();
    // Placed objects and free spikes with their place in the editor's stack.
    public readonly List<(int order, GameObject go)> Stacked = new List<(int, GameObject)>();

    private readonly List<Action> _undo = new List<Action>();
    private readonly Dictionary<string, Tilemap> _own = new Dictionary<string, Tilemap>();
    private readonly Dictionary<Grid, Transform> _grids = new Dictionary<Grid, Transform>();
    private readonly Dictionary<string, Transform> _holders = new Dictionary<string, Transform>();
    private Dictionary<string, Tilemap> _real;
    private Dictionary<string, List<Transform>> _byName;

    public MapWorld(string mapId, MapDefinition def)
    {
        MapId = mapId;
        Def = def;
        Group = def.Groups[0];
        Overlay = def.Overlay;
        Origin = Overlay ? Vector2.zero : PocketOrigin;
        var editorOrigin = def.LevelOrigin != null && def.LevelOrigin.Length == 2 ? new Vector2(def.LevelOrigin[0], def.LevelOrigin[1]) : new Vector2(0f, EditorOffsetY);
        _shift = Overlay ? Vector2.zero : Origin - editorOrigin;
        var root = new GameObject(RootPrefix + mapId);
        root.transform.position = Live(Origin);
        Root = root.transform;
        OnUnload(() => { if (root != null) UnityEngine.Object.Destroy(root); });
    }

    public static bool IsMapObject(Transform t) => t != null && t.root.name.StartsWith(RootPrefix);

    // ---- unloading ----

    public void OnUnload(Action undo) => _undo.Add(undo);

    // Newest first, so layered changes unwind in order.
    public void Unload()
    {
        for (int i = _undo.Count - 1; i >= 0; i--)
        {
            try { _undo[i](); }
            catch (Exception e) { Debug.LogWarning("[RechargeMaps] unloading a map step failed: " + e.Message); }
        }
        _undo.Clear();
    }

    public void SetActive(GameObject go, bool active)
    {
        if (go == null || go.activeSelf == active) return;
        var was = go.activeSelf;
        go.SetActive(active);
        OnUnload(() => { if (go != null) go.SetActive(was); });
    }

    // ---- coordinates ----

    private static Vector2 FloatShift()
    {
        var fo = Singleton<FloatingOrigin>.Instance;
        return fo != null ? (Vector2)fo.currentOrigin : Vector2.zero;
    }

    public static Vector3 Live(Vector2 level) => level + FloatShift();
    public static Vector2 LevelOf(Vector3 live) => (Vector2)live - FloatShift();

    public Vector2 LevelPoint(float x, float y) => Origin + new Vector2(x, y);
    public Vector2 LevelPoint(JObject obj) => LevelPoint(obj["x"]?.Value<float>() ?? 0f, obj["y"]?.Value<float>() ?? 0f);
    public Vector3 Point(JObject obj) => Live(LevelPoint(obj));

    // Where a cloned object's original sits in the real level.
    public static Vector3 SourcePoint(JObject obj) => Live(new Vector2(obj["srcX"]?.Value<float>() ?? 0f, obj["srcY"]?.Value<float>() ?? 0f));

    public static Quaternion Rot(JObject obj) => Quaternion.Euler(0f, 0f, obj["rotation"]?.Value<float>() ?? 0f);

    // ---- tilemaps ----

    // The tilemap a map tile named `name` is painted on.
    public Tilemap Tilemap(string name)
    {
        if (!Overlay) return OwnTilemap(name, name);
        return RealTilemap(name) ?? throw new Exception("no '" + name + "' tilemap in this scene");
    }

    // A tilemap of the map's own, made like the game's `template` tilemap (and
    // so behaving like it: its colliders, spikeScript...). In an overlay it
    // sits beside the real one, on the same grid.
    public Tilemap OwnTilemap(string name, string template)
    {
        if (_own.TryGetValue(name, out var existing) && existing != null) return existing;
        GameObject go;
        var real = RealTilemap(template);
        if (Overlay)
        {
            if (real == null) throw new Exception("no '" + template + "' tilemap in this scene");
            go = UnityEngine.Object.Instantiate(real.gameObject, real.transform.parent);
            var made = go;
            OnUnload(() => { if (made != null) UnityEngine.Object.Destroy(made); });
        }
        else if (real != null && real.layoutGrid != null)
        {
            // A copy of the level's tilemap on a copy of its grid, moved to the
            // pocket: same cell size and alignment, so every layer lines up as it does there.
            go = UnityEngine.Object.Instantiate(real.gameObject, GridLike(real.layoutGrid));
            go.transform.SetPositionAndRotation(Live(LevelOf(real.transform.position) + _shift), real.transform.rotation);
            go.transform.localScale = real.transform.lossyScale;
        }
        else
        {
            var source = RealAssetPalette.GetTilemapTemplate(template) ?? throw new Exception("no '" + template + "' tilemap in this scene");
            go = UnityEngine.Object.Instantiate(source.gameObject, OwnGrid());
        }
        go.name = name;
        // Swapped block layers start as the level's are right now; the rest always show.
        var swaps = name.StartsWith("blue") || name.StartsWith("orange");
        go.SetActive(!swaps || real == null || real.gameObject.activeSelf);
        var tilemap = go.GetComponent<Tilemap>();
        tilemap.ClearAllTiles();
        _own[name] = tilemap;
        if (name.StartsWith("blue")) JoinBlockSwap(go, false);
        else if (name.StartsWith("orange")) JoinBlockSwap(go, true);
        return tilemap;
    }

    // Where spikes placed off the grid go, one holder per spike tilemap: on its
    // layer, and for blue / orange ones swapped with the level's blocks.
    public Transform FreeSpikeHolder(string tilemapName)
    {
        if (_holders.TryGetValue(tilemapName, out var held) && held != null) return held;
        var go = new GameObject("FreeSpikes_" + tilemapName);
        go.transform.SetParent(Root, false);
        var real = RealTilemap(tilemapName);
        if (real != null) go.layer = real.gameObject.layer;
        var orange = tilemapName.StartsWith("orange");
        if (orange || tilemapName.StartsWith("blue"))
        {
            go.SetActive(real == null || real.gameObject.activeSelf);
            JoinBlockSwap(go, orange);
        }
        _holders[tilemapName] = go.transform;
        return go.transform;
    }

    // Custom maps' tilemaps need a Grid parent with the game's cell size, or
    // CellToWorld collapses every cell onto one spot.
    private Transform OwnGrid()
    {
        var grid = Root.Find("Grid");
        if (grid != null) return grid;
        grid = new GameObject("Grid").transform;
        grid.SetParent(Root, false);
        grid.gameObject.AddComponent<Grid>().cellSize = RealAssetPalette.GroundCellSize;
        return grid;
    }

    private Transform GridLike(Grid real)
    {
        if (_grids.TryGetValue(real, out var made) && made != null) return made;
        var go = new GameObject("Grid_" + real.name);
        go.transform.SetParent(Root, true);
        go.transform.SetPositionAndRotation(Live(LevelOf(real.transform.position) + _shift), real.transform.rotation);
        go.transform.localScale = real.transform.lossyScale;
        var grid = go.AddComponent<Grid>();
        grid.cellSize = real.cellSize;
        grid.cellGap = real.cellGap;
        grid.cellLayout = real.cellLayout;
        grid.cellSwizzle = real.cellSwizzle;
        _grids[real] = go.transform;
        return go.transform;
    }

    // Custom maps give some tiles as cells of the editor's 32 grid (cell 0 at
    // the map origin); everything else as a position.
    public Vector3Int CellOf(Tilemap tilemap, JObject obj)
    {
        if (Overlay || obj["cellX"] == null) return tilemap.WorldToCell(Point(obj));
        var size = RealAssetPalette.GroundCellSize;
        var centre = LevelPoint((obj["cellX"].Value<int>() + 0.5f) * size.x, ((obj["cellY"]?.Value<int>() ?? 0) + 0.5f) * size.y);
        return tilemap.WorldToCell(Live(centre));
    }

    public void Paint(Tilemap tilemap, Vector3Int cell, TileBase tile, Matrix4x4? matrix)
    {
        if (Overlay && !_own.ContainsValue(tilemap)) RememberCell(tilemap, cell);
        tilemap.SetTile(cell, tile);
        if (matrix == null) return;
        tilemap.SetTileFlags(cell, TileFlags.None);
        tilemap.SetTransformMatrix(cell, matrix.Value);
    }

    public void Erase(Tilemap tilemap, Vector3Int cell)
    {
        if (tilemap.GetTile(cell) == null) return;
        if (!_own.ContainsValue(tilemap)) RememberCell(tilemap, cell);
        tilemap.SetTile(cell, null);
    }

    private void RememberCell(Tilemap tilemap, Vector3Int cell)
    {
        var oldTile = tilemap.GetTile(cell);
        var oldMatrix = tilemap.GetTransformMatrix(cell);
        OnUnload(() =>
        {
            if (tilemap == null) return;
            tilemap.SetTile(cell, oldTile);
            if (oldTile == null) return;
            tilemap.SetTileFlags(cell, TileFlags.None);
            tilemap.SetTransformMatrix(cell, oldMatrix);
        });
    }

    public static Matrix4x4? MatrixOf(JObject obj)
    {
        if (!(obj["matrix"] is JArray m) || m.Count != 4 || !m.All(v => v.Type == JTokenType.Integer || v.Type == JTokenType.Float)) return null;
        var matrix = Matrix4x4.identity;
        matrix.m00 = m[0].Value<float>();
        matrix.m01 = m[1].Value<float>();
        matrix.m10 = m[2].Value<float>();
        matrix.m11 = m[3].Value<float>();
        return matrix;
    }

    private void JoinBlockSwap(GameObject tilemapGo, bool orange)
    {
        var swapper = Singleton<colouredBlockSwapper>.Instance;
        var field = orange ? "orange" : "blue";
        if (swapper == null || Reflect.FieldOf<colouredBlockSwapper>(field) == null) return;
        var current = Reflect.GetField<GameObject[]>(swapper, field) ?? Array.Empty<GameObject>();
        if (current.Contains(tilemapGo)) return;
        Reflect.SetField(swapper, field, current.Concat(new[] { tilemapGo }).ToArray());
        OnUnload(() =>
        {
            if (swapper == null) return;
            var now = Reflect.GetField<GameObject[]>(swapper, field) ?? Array.Empty<GameObject>();
            Reflect.SetField(swapper, field, now.Where(g => g != null && g != tilemapGo).ToArray());
        });
    }

    // ---- the real scene ----

    // The game's own tilemap by name. The title scene can stay loaded beside
    // the world with its own copies, so the Player's scene wins.
    public Tilemap RealTilemap(string name)
    {
        if (_real == null)
        {
            _real = new Dictionary<string, Tilemap>();
            var ranks = new Dictionary<string, int>();
            foreach (var tm in Resources.FindObjectsOfTypeAll<Tilemap>())
            {
                if (!tm.gameObject.scene.IsValid() || IsMapObject(tm.transform) || _own.ContainsValue(tm)) continue;
                var key = tm.gameObject.name == "new awesome nikki ground" ? "ground" : tm.gameObject.name;
                var rank = SceneRank(tm.transform);
                if (!ranks.TryGetValue(key, out var best) || rank > best) { ranks[key] = rank; _real[key] = tm; }
            }
        }
        return name != null && _real.TryGetValue(name, out var found) ? found : null;
    }

    // How much a scene object looks like the live world's: in the Player's scene, and active.
    public static int SceneRank(Transform t)
    {
        var player = MapUpgrades.GamePlayer()?.gameObject;
        var rank = 0;
        if (player != null && t.gameObject.scene == player.scene) rank += 2;
        if (t.gameObject.activeInHierarchy) rank += 1;
        return rank;
    }

    // A real scene object by hierarchy path - the live world's copy, and the
    // one nearest `near` when the name repeats. Never one of the map's own.
    public Transform FindSceneObject(string path, Vector2 near)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (_byName == null)
        {
            _byName = new Dictionary<string, List<Transform>>();
            foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (!t.gameObject.scene.IsValid()) continue;
                if (!_byName.TryGetValue(t.name, out var list)) _byName[t.name] = list = new List<Transform>();
                list.Add(t);
            }
        }
        var name = path.Substring(path.LastIndexOf('/') + 1);
        if (!_byName.TryGetValue(name, out var candidates)) return null;
        Transform best = null;
        var bestScore = float.MaxValue;
        foreach (var t in candidates)
        {
            if (t == null || IsMapObject(t) || HierarchyPath(t) != path) continue;
            var score = ((Vector2)t.position - near).sqrMagnitude - SceneRank(t) * 1e12f;
            if (score < bestScore) { bestScore = score; best = t; }
        }
        return best;
    }

    private static string HierarchyPath(Transform t)
    {
        var path = t.name;
        for (var p = t.parent; p != null; p = p.parent) path = p.name + "/" + path;
        return path;
    }

    // Area 1's look ("start" or "overgrown"), switched the way
    // OvergrowthLoadScript does it - objects only, never globalStats.
    public void ApplyBaseState(string state)
    {
        if (state != "start" && state != "overgrown") return;
        OvergrowthLoadScript loader = null;
        foreach (var candidate in Resources.FindObjectsOfTypeAll<OvergrowthLoadScript>())
        {
            if (!candidate.gameObject.scene.IsValid()) continue;
            if (loader == null || SceneRank(candidate.transform) > SceneRank(loader.transform)) loader = candidate;
        }
        if (loader == null) return;
        var overgrown = state == "overgrown";
        foreach (var (field, active) in new[] { ("overgrowthEnable", overgrown), ("overgrowthDisable", !overgrown) })
            foreach (var go in Reflect.GetField<GameObject[]>(loader, field) ?? Array.Empty<GameObject>())
                SetActive(go, active);
    }
}
