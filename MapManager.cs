using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Recharge.ModApi;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Tilemaps;

internal class MapManager : MonoBehaviour
{
    public static MapManager Instance { get; private set; }

    // The custom-map id currently spawned in the pocket, or null when playing
    // real Base Game/B-side content. Cleared on every real scene load (the
    // only way a player actually leaves the pocket - custom maps never call
    // SceneManager.LoadScene themselves), so it can't desync from reality.
    public static string CurrentMapId { get; private set; }

    private GameObject _currentCourseGo;

    // Far from any real course geometry (~-10000..20000), so nothing needs
    // hiding except the cloned course template's own DisableBits content.
    private static readonly Vector2 PocketOrigin = new Vector2(50000f, 50000f);

    // Where map coordinates are measured from: the pocket for self-contained
    // maps, the world origin for overlay maps (their coordinates are the game's).
    private Vector2 _origin = PocketOrigin;


    public static MapManager GetOrCreate()
    {
        if (Instance != null) return Instance;
        var go = new GameObject("MapManager");
        UnityEngine.Object.DontDestroyOnLoad(go);
        return go.AddComponent<MapManager>();
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            UnityEngine.Object.Destroy(gameObject);
            return;
        }
        Instance = this;
        UnityEngine.Object.DontDestroyOnLoad(gameObject);
        SceneManager.sceneLoaded += (scene, mode) =>
        {
            CurrentMapId = null;
            _currentCourseGo = null;
            _overlayUndo.Clear();
            _realTilemaps = null;
            RealAssetPalette.ScanCurrentScene();
            TryCaptureSpringAnimation();
            if (_pendingMapId != null && _pendingRoutine == null) _pendingRoutine = StartCoroutine(LoadPendingMapWhenPlayerReady());
        };
        RealAssetPalette.ScanCurrentScene();
        TryCaptureSpringAnimation();
    }

    private string _pendingMapId;
    private Coroutine _pendingRoutine;

    // Picking a map only ever spawns a pocket within whatever scene is
    // already loaded - it never itself starts a real scene transition. From
    // the title screen (no gameplay scene entered yet) there's no Player to
    // move into that pocket, so LoadMap's own MovePlayerIn silently no-ops:
    // the level spawns, nothing visible happens, and the only symptom is the
    // frame hitch from spawning it. This is the entry point the Play picker
    // should call instead of LoadMap directly - it starts Base Game first
    // when there's no Player yet, then loads the map once one actually exists.
    public void PlayMap(string mapId, pauseMenuScript menu)
    {
        var playerGo = GameObject.FindGameObjectWithTag("Player");
        if (playerGo != null && playerGo.GetComponent<Movement>() != null)
        {
            LoadMap(mapId);
            return;
        }
        _pendingMapId = mapId;
        menu.changeScene();
    }

    public void PlayMapAfterSceneChange(string mapId, pauseMenuScript menu)
    {
        _pendingMapId = mapId;
        menu.changeScene();
    }

    // Waits - across scene loads, since this lives on a DontDestroyOnLoad
    // object - for the gameplay Player to be fully set up before loading: the
    // title screen (MainMenu scene) has its own Player, and a gameplay Player
    // moves itself to its saved respawn point in Start/its save load (init),
    // which would undo a map spawned any earlier.
    private System.Collections.IEnumerator LoadPendingMapWhenPlayerReady()
    {
        float waited = 0f;
        while (true)
        {
            var playerGo = GameObject.FindGameObjectWithTag("Player");
            var movement = playerGo != null ? playerGo.GetComponent<Movement>() : null;
            if (movement != null && !Reflect.GetField<bool>(movement, "isOnMainMenu") && !Reflect.GetField<bool>(movement, "init")) break;
            if (waited > 60f)
            {
                Debug.LogWarning("[RechargeMaps] gave up waiting for a gameplay Player to load '" + _pendingMapId + "' into");
                _pendingMapId = null;
                _pendingRoutine = null;
                yield break;
            }
            yield return null;
            waited += Time.unscaledDeltaTime;
        }
        // A couple more frames so anything else reacting to the fresh Player runs first.
        yield return null;
        yield return null;
        var mapId = _pendingMapId;
        _pendingMapId = null;
        _pendingRoutine = null;
        LoadMap(mapId);
    }

    private void TryCaptureSpringAnimation()
    {
        if (RealAssetPalette.Get<SpringScript>() != null) StartCoroutine(RealAssetPalette.CaptureSpringAnimation());
    }

    public static string SaveFolder => "/Savedata" + (globalStats.difficultyLevel == 1 ? "hard" : "");

    public void LoadMap(string mapId)
    {
        try
        {
            var path = Path.Combine(MapPaths.MapsDir, mapId, "map.json");
            if (!File.Exists(path)) { Debug.LogError("[RechargeMaps] map not found: " + path); return; }

            var def = JsonConvert.DeserializeObject<MapDefinition>(File.ReadAllText(path));
            if (def?.Groups == null || def.Groups.Count == 0) { Debug.LogError("[RechargeMaps] map has no groups: " + mapId); return; }

            RevertOverlay();
            LoadCustomImages(mapId, def);
            if (def.Overlay) SpawnOverlay(mapId, def);
            else
            {
                _origin = PocketOrigin;
                SpawnGroup(mapId, def.Groups[0]);
            }
            CurrentMapId = mapId;
        }
        catch (Exception e)
        {
            Debug.LogError("[RechargeMaps] LoadMap failed: " + e);
        }
    }

    // Deletes just the given map's own course-progress file (courseScript.save/
    // load already namespace it by courseNumber - see StableCourseNumber - so
    // this never touches real Base Game/B-side data or any other custom map's
    // progress, unlike the vanilla Delete Save buttons which wipe a whole
    // folder). Works on any map id, not just the one currently loaded - the
    // Maps panel lets a player browse to and delete a map's save without
    // having to load it first.
    public static void DeleteMapSave(string mapId)
    {
        var path = Application.persistentDataPath + SaveFolder + "/course" + StableCourseNumber(mapId) + "data.txt";
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception e) { Debug.LogWarning("[RechargeMaps] delete map save failed: " + e.Message); }
    }

    // A clone of the cached real course (course 1) with its DisableBits
    // emptied: gives a map its own courseScript for timing, gates and saves.
    private courseScript CreateCourse(string mapId, Vector2 at)
    {
        var courseTemplate = RealAssetPalette.Get<courseScript>();
        if (courseTemplate == null)
        {
            Debug.LogError("[RechargeMaps] no courseScript template cached yet - visit a course area first, then reopen the Maps menu");
            return null;
        }

        if (_currentCourseGo != null) { Destroy(_currentCourseGo); _currentCourseGo = null; }

        var courseGo = Instantiate(courseTemplate.gameObject, Live(at), Quaternion.identity);
        courseGo.name = "RechargeMap_" + mapId;
        courseGo.SetActive(true);
        _currentCourseGo = courseGo;
        var course = courseGo.GetComponent<courseScript>();

        var disableBits = courseGo.transform.Find("DisableBits");
        if (disableBits != null)
        {
            foreach (Transform child in disableBits) Destroy(child.gameObject);
        }

        course.courseNumber = StableCourseNumber(mapId);
        course.init = true;
        // The cloned template is "course 1", the decorative course shown behind the
        // main menu - it has isOnPauseMenu=true, which makes load() read the bundled
        // MenuCourseData.txt (a canned demo ghost-path) instead of a real per-course
        // save file. Force it off so a fresh map starts clean, not replaying that ghost.
        Reflect.TrySetField(course, "isOnPauseMenu", false);
        try { course.load(SaveFolder); } catch (Exception e) { Debug.LogWarning("[RechargeMaps] course.load failed (expected on first play): " + e.Message); }

        return course;
    }

    private void SpawnGroup(string mapId, MapGroup group)
    {
        var course = CreateCourse(mapId, PocketOrigin);
        if (course == null) return;
        var courseGo = course.gameObject;

        _transformIndex = null;
        foreach (var obj in group.Objects)
        {
            var type = obj["type"]?.Value<string>();
            // One bad object shouldn't abort the whole map (and strand the
            // player in the real world with half a map spawned).
            try
            {
            switch (type)
            {
                case "ground": PaintTile("ground", obj, courseGo.transform); break;
                case "coloredGround": PaintColoredGround(obj, courseGo.transform); break;
                case "tile": PaintNamedTile(obj, courseGo.transform); break;
                case "clone": SpawnClone(obj, courseGo.transform); break;
                case "spike": SpawnSimple<spikeScript>(obj, courseGo.transform); break;
                case "checkpoint": SpawnSimple<checkpointScript>(obj, courseGo.transform); break;
                case "spring": SpawnSpring(obj, courseGo.transform); break;
                case "platform": SpawnPlatform(obj, courseGo.transform); break;
                case "deco": SpawnDeco(obj, courseGo.transform); break;
                case "customImage": SpawnCustomImage(obj, courseGo.transform); break;
                default: Debug.LogWarning("[RechargeMaps] unknown object type '" + type + "', skipped"); break;
            }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RechargeMaps] couldn't spawn " + type + " object " + obj.ToString(Newtonsoft.Json.Formatting.None) + ": " + e.Message);
            }
        }

        if (group.Gates) SpawnGates(group, courseGo.transform, course);
        MovePlayerIn(group);

        Debug.Log("[RechargeMaps] spawned map '" + mapId + "' (courseNumber=" + course.courseNumber + ") at pocket " + PocketOrigin);
    }

// ---- overlay maps: edits to the real world ---------------------------------

    // Every change an overlay makes to the live scene, undone (newest first)
    // before the next map loads. A scene load discards the scene anyway.
    private readonly List<Action> _overlayUndo = new List<Action>();
    private Dictionary<string, Tilemap> _realTilemaps;

    private void RevertOverlay()
    {
        for (int i = _overlayUndo.Count - 1; i >= 0; i--)
        {
            try { _overlayUndo[i](); } catch (Exception e) { Debug.LogWarning("[RechargeMaps] overlay revert step failed: " + e.Message); }
        }
        _overlayUndo.Clear();
        _origin = PocketOrigin;
    }

    // Paints the author's blocks, spikes and vines onto the game's own tilemaps,
    // clears what they erased, hides removed objects, and sets area 1's state -
    // all at the world's real coordinates, around the real level and its art.
    private void SpawnOverlay(string mapId, MapDefinition def)
    {
        var group = def.Groups[0];
        _origin = Vector2.zero;
        _transformIndex = null;
        if (_currentCourseGo != null) { Destroy(_currentCourseGo); _currentCourseGo = null; }
        ApplyBaseState(def.BaseState);

        int done = 0;
        foreach (var obj in group.Objects)
        {
            var type = obj["type"]?.Value<string>();
            try
            {
                switch (type)
                {
                    case "ground": OverlayPaint("ground", BlankGroundTile(), obj); break;
                    case "coloredGround":
                    {
                        var name = obj["color"]?.Value<string>() == "orange" ? "orangeBlocks" : "blueBlocks";
                        OverlayPaint(name, RealAssetPalette.GetTile(name, 0), obj);
                        break;
                    }
                    case "tile":
                    {
                        var name = obj["tilemap"]?.Value<string>();
                        OverlayPaint(name, ResolveTile(name, obj), obj);
                        break;
                    }
                    case "erase": OverlayErase(obj); break;
                    case "hide": OverlayHide(obj); break;
                    case "clone": SpawnClone(obj, OverlayRoot(mapId)); break;
                    default: Debug.LogWarning("[RechargeMaps] overlay: unsupported object type '" + type + "', skipped"); continue;
                }
                done++;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RechargeMaps] overlay: couldn't apply " + type + " object " + obj.ToString(Formatting.None) + ": " + e.Message);
            }
        }

        if (group.Gates)
        {
            // Gates need a courseScript parent to report to: a map-owned course
            // at the origin, with the template's own upgrade boxes and clone
            // machinery switched off so they don't duplicate the real course's.
            var course = CreateCourse(mapId, Vector2.zero);
            if (course != null)
            {
                foreach (var child in new[] { "localUpgrades", "Clones" })
                {
                    var t = course.transform.Find(child);
                    if (t != null) t.gameObject.SetActive(false);
                }
                SpawnGates(group, course.transform, course);
            }
        }
        MovePlayerIn(group);
        Debug.Log("[RechargeMaps] applied overlay map '" + mapId + "' (" + done + "/" + group.Objects.Count + " edits) to the real world");
    }

    private GameObject _overlayRootGo;
    private Transform OverlayRoot(string mapId)
    {
        if (_overlayRootGo == null)
        {
            _overlayRootGo = new GameObject("RechargeOverlay_" + mapId);
            var root = _overlayRootGo;
            _overlayUndo.Add(() => { if (root != null) Destroy(root); });
        }
        return _overlayRootGo.transform;
    }

    private static readonly string[] OverlayErasable =
    {
        "ground", "lightBlocker", "InvisibleWall", "OvergrowthDestroyedGround", "moss", "OvergrowthMoss",
        "blueBlocks", "orangeBlocks", "Spikes", "hiddenSpikes", "backgroundSpikes1", "backgroundSpikes2",
        "OvergrowthSpikes", "blueSpikes", "orangeSpikes",
    };

    private Tilemap RealTilemap(string name)
    {
        if (_realTilemaps == null)
        {
            // The title scene (MainMenu) stays loaded under the Overworld with
            // its own copies of these tilemaps, so rank candidates: the
            // Player's scene first, then active ones.
            _realTilemaps = new Dictionary<string, Tilemap>();
            var ranks = new Dictionary<string, int>();
            foreach (var tm in Resources.FindObjectsOfTypeAll<Tilemap>())
            {
                if (!tm.gameObject.scene.IsValid() || (_currentCourseGo != null && tm.transform.IsChildOf(_currentCourseGo.transform))) continue;
                var key = tm.gameObject.name == "new awesome nikki ground" ? "ground" : tm.gameObject.name;
                var rank = SceneRank(tm.transform);
                if (!ranks.TryGetValue(key, out var best) || rank > best) { ranks[key] = rank; _realTilemaps[key] = tm; }
            }
        }
        return name != null && _realTilemaps.TryGetValue(name, out var found) ? found : null;
    }

    // How much a scene object looks like the live world's: in the Player's
    // scene (not the title scene loaded alongside it), and active.
    private static int SceneRank(Transform t)
    {
        var player = GameObject.FindGameObjectWithTag("Player");
        var rank = 0;
        if (player != null && t.gameObject.scene == player.scene) rank += 2;
        if (t.gameObject.activeInHierarchy) rank += 1;
        return rank;
    }

    private static Vector3 OverlayPoint(JObject obj)
    {
        return Live(new Vector2(obj["x"]?.Value<float>() ?? 0f, obj["y"]?.Value<float>() ?? 0f));
    }

    private void OverlayPaint(string tilemapName, TileBase tile, JObject obj)
    {
        var tilemap = RealTilemap(tilemapName);
        if (tilemap == null) throw new Exception("no '" + tilemapName + "' tilemap in this scene");
        if (tile == null) throw new Exception("no tile '" + obj["tileName"] + "' for '" + tilemapName + "'");
        var cell = tilemap.WorldToCell(OverlayPoint(obj));
        var oldTile = tilemap.GetTile(cell);
        var oldMatrix = tilemap.GetTransformMatrix(cell);
        tilemap.SetTile(cell, tile);
        var m = obj["matrix"] as JArray;
        if (m != null && m.Count == 4 && m.All(v => v.Type == JTokenType.Integer || v.Type == JTokenType.Float))
        {
            var matrix = Matrix4x4.identity;
            matrix.m00 = m[0].Value<float>();
            matrix.m01 = m[1].Value<float>();
            matrix.m10 = m[2].Value<float>();
            matrix.m11 = m[3].Value<float>();
            tilemap.SetTileFlags(cell, TileFlags.None);
            tilemap.SetTransformMatrix(cell, matrix);
        }
        _overlayUndo.Add(() =>
        {
            if (tilemap == null) return;
            tilemap.SetTile(cell, oldTile);
            if (oldTile != null) { tilemap.SetTileFlags(cell, TileFlags.None); tilemap.SetTransformMatrix(cell, oldMatrix); }
        });
    }

    // Clears the real tiles under a point: on one named tilemap (a vine's), or
    // on every solid / deadly tilemap (an erased cell).
    private void OverlayErase(JObject obj)
    {
        var only = obj["tilemap"]?.Value<string>();
        var point = OverlayPoint(obj);
        foreach (var name in only != null ? new[] { only } : OverlayErasable)
        {
            var tilemap = RealTilemap(name);
            if (tilemap == null) continue;
            var cell = tilemap.WorldToCell(point);
            var oldTile = tilemap.GetTile(cell);
            if (oldTile == null) continue;
            var oldMatrix = tilemap.GetTransformMatrix(cell);
            tilemap.SetTile(cell, null);
            _overlayUndo.Add(() =>
            {
                if (tilemap == null) return;
                tilemap.SetTile(cell, oldTile);
                tilemap.SetTileFlags(cell, TileFlags.None);
                tilemap.SetTransformMatrix(cell, oldMatrix);
            });
        }
    }

    private void OverlayHide(JObject obj)
    {
        var path = obj["path"]?.Value<string>();
        var target = string.IsNullOrEmpty(path) ? null : FindSceneObject(path, SourcePos(obj), null);
        if (target == null) throw new Exception("no scene object at '" + path + "'");
        var go = target.gameObject;
        var was = go.activeSelf;
        go.SetActive(false);
        _overlayUndo.Add(() => { if (go != null) go.SetActive(was); });
    }

    // Area 1's look for this map, the way OvergrowthLoadScript.OnLoad switches
    // it - but only the objects, never globalStats, so the player's save isn't
    // pushed into (or out of) the overgrown state by testing a map.
    private void ApplyBaseState(string state)
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
        {
            foreach (var go in Reflect.GetField<GameObject[]>(loader, field) ?? Array.Empty<GameObject>())
            {
                if (go == null || go.activeSelf == active) continue;
                var was = go.activeSelf;
                go.SetActive(active);
                _overlayUndo.Add(() => { if (go != null) go.SetActive(was); });
            }
        }
    }

        private static int StableCourseNumber(string mapId)
    {
        // FNV-1a, folded into a high range unlikely to collide with real course numbers.
        unchecked
        {
            uint hash = 2166136261;
            foreach (var c in mapId) { hash ^= c; hash *= 16777619; }
            return 900000 + (int)(hash % 100000);
        }
    }

    // IGTAP keeps the player near (0,0) with a floating origin: every so often
    // it shifts all scene roots and adds the shift to currentOrigin, so a
    // level-space point (map coordinates, the extracted base map, respawn
    // points) lives at level + currentOrigin in the world right now.
    // A real tile by name - or, for one the level never places (so the game has
    // no Tile for it, e.g. a lone block), one built from tiles it does have:
    // { ref, quarters: [4 tile names] } puts together the top-left, top-right,
    // bottom-left and bottom-right quarters of those tiles; { ref, rect, ppu,
    // pivot } cuts a rect from ref's sheet.
    private static readonly Dictionary<string, Tile> BuiltTiles = new Dictionary<string, Tile>();
    private static TileBase ResolveTile(string tilemapName, JObject obj)
    {
        var name = obj["tileName"]?.Value<string>();
        var from = obj["spriteFrom"] as JObject;
        // A tile that brings its own sprite is always built from it, even if the
        // game happens to have a tile by that name.
        if (from == null) return name != null ? RealAssetPalette.GetTileByName(tilemapName, name) : null;
        if (name == null) return null;
        if (BuiltTiles.TryGetValue(name, out var built) && built != null) return built;
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
        BuiltTiles[name] = built;
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

    // Movement.respawn moves the player 12 units below their respawn point.
    private static readonly Vector2 RespawnLift = new Vector2(0f, 12f);

    // spikeScript only kills from OnTriggerStay and then ignores the player
    // until they leave its trigger - so respawning inside spikes (a spawn placed
    // in them) never killed again. Clear that latch whenever the player respawns.
    private bool _wasDead;
    private void Update()
    {
        if (CurrentMapId == null) return;
        var playerGo = GameObject.FindGameObjectWithTag("Player");
        var movement = playerGo != null ? playerGo.GetComponent<Movement>() : null;
        if (movement == null) return;
        var dead = Reflect.GetField<bool>(movement, "isDead");
        if (_wasDead && !dead)
        {
            foreach (var spike in FindObjectsByType<spikeScript>(FindObjectsSortMode.None))
            {
                Reflect.TrySetField(spike, "playerKilled", false);
                Reflect.TrySetField(spike, "counter", 0);
            }
        }
        _wasDead = dead;
    }

    private static Vector3 Live(Vector2 level)
    {
        var fo = Singleton<FloatingOrigin>.Instance;
        var o = fo != null ? fo.currentOrigin : Vector3.zero;
        return new Vector3(level.x + o.x, level.y + o.y, 0f);
    }

    private static Vector2 LevelOf(Vector3 live)
    {
        var fo = Singleton<FloatingOrigin>.Instance;
        var o = fo != null ? fo.currentOrigin : Vector3.zero;
        return new Vector2(live.x - o.x, live.y - o.y);
    }

    private static Vector3 WorldPos(JObject obj)
    {
        var x = obj["x"]?.Value<float>() ?? 0f;
        var y = obj["y"]?.Value<float>() ?? 0f;
        return Live(new Vector2(Instance._origin.x + x, Instance._origin.y + y));
    }

    private static Vector2 SourcePos(JObject obj)
    {
        return Live(new Vector2(obj["srcX"]?.Value<float>() ?? 0f, obj["srcY"]?.Value<float>() ?? 0f));
    }

    private static Quaternion Rot(JObject obj)
    {
        var r = obj["rotation"]?.Value<float>() ?? 0f;
        return Quaternion.Euler(0f, 0f, r);
    }

    private void SpawnSimple<T>(JObject obj, Transform parent) where T : Component
    {
        var spawned = RealAssetPalette.Spawn<T>(WorldPos(obj), Rot(obj), parent);
        if (spawned == null) { Debug.LogWarning("[RechargeMaps] no template cached for " + typeof(T).Name + " yet - visit a course containing one first"); return; }
        ForceTriggerColliders(spawned.gameObject);
    }

    private void SpawnSpring(JObject obj, Transform parent)
    {
        var spring = RealAssetPalette.Spawn<SpringScript>(WorldPos(obj), Rot(obj), parent);
        if (spring == null) { Debug.LogWarning("[RechargeMaps] no SpringScript template cached yet"); return; }
        ForceTriggerColliders(spring.gameObject);

        if (obj["strength"] != null) SetPrivate(spring, "strength", obj["strength"].Value<float>());
        if (obj["upForce"] != null) SetPrivate(spring, "upForce", obj["upForce"].Value<float>());
    }

    private void SpawnPlatform(JObject obj, Transform parent)
    {
        var platform = RealAssetPalette.Spawn<PlatformMover>(WorldPos(obj), Rot(obj), parent);
        if (platform == null) { Debug.LogWarning("[RechargeMaps] no PlatformMover template cached yet"); return; }

        var positionsToken = obj["positions"] as JArray;
        if (positionsToken == null || positionsToken.Count == 0) return;

        var positionDataType = Reflect.TryNestedType<PlatformMover>("PositionData");
        var tweenType = Reflect.TryNestedType<PlatformMover>("TweenType");
        if (positionDataType == null) { Debug.LogWarning("[RechargeMaps] PlatformMover.PositionData not found via reflection"); return; }

        var array = Array.CreateInstance(positionDataType, positionsToken.Count);
        for (int i = 0; i < positionsToken.Count; i++)
        {
            var p = (JObject)positionsToken[i];
            object boxed = Activator.CreateInstance(positionDataType);
            SetStructField(positionDataType, ref boxed, "position", new Vector2(p["x"]?.Value<float>() ?? 0f, p["y"]?.Value<float>() ?? 0f));
            SetStructField(positionDataType, ref boxed, "timeToReachFromPrevious", p["timeToReachFromPrevious"]?.Value<float>() ?? 0f);
            SetStructField(positionDataType, ref boxed, "autoStartNextPhase", p["autoStartNextPhase"]?.Value<bool>() ?? false);
            SetStructField(positionDataType, ref boxed, "nextPhaseOnEnter", p["nextPhaseOnEnter"]?.Value<bool>() ?? false);
            SetStructField(positionDataType, ref boxed, "nextPhaseOnExit", p["nextPhaseOnExit"]?.Value<bool>() ?? false);
            SetStructField(positionDataType, ref boxed, "waitOnPhaseEnd", p["waitOnPhaseEnd"]?.Value<float>() ?? 0f);
            if (tweenType != null)
            {
                var tweenName = p["tween"]?.Value<string>() ?? "linear";
                object tweenValue;
                try { tweenValue = Enum.Parse(tweenType, tweenName, ignoreCase: true); }
                catch { tweenValue = Enum.ToObject(tweenType, 0); }
                SetStructField(positionDataType, ref boxed, "wayToTweenTo", tweenValue);
            }
            array.SetValue(boxed, i);
        }

        Reflect.TrySetField(platform, "Positions", array);
        var platformTypeField = Reflect.FieldOf<PlatformMover>("PlatformType");
        if (platformTypeField != null) platformTypeField.SetValue(platform, Enum.ToObject(platformTypeField.FieldType, 0)); // NONE - avoids the ZipMoversUnlocked gate
        platform.JumpToState(0);
    }

    private readonly Dictionary<string, Sprite> _customImageSprites = new Dictionary<string, Sprite>();

    // Loaded fresh per LoadMap call (not cached across maps) - assetIds are
    // generated client-side per editor session so collisions across different
    // maps are possible in principle, and images are small/cheap to reload.
    private void LoadCustomImages(string mapId, MapDefinition def)
    {
        _customImageSprites.Clear();
        if (def.CustomImages == null) return;
        foreach (var ci in def.CustomImages)
        {
            if (string.IsNullOrEmpty(ci.AssetId) || string.IsNullOrEmpty(ci.Path)) continue;
            var path = Path.Combine(MapPaths.MapsDir, mapId, ci.Path);
            if (!File.Exists(path)) { Debug.LogWarning("[RechargeMaps] custom image file missing: " + path); continue; }
            try
            {
                var bytes = File.ReadAllBytes(path);
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!ImageConversion.LoadImage(tex, bytes)) { Debug.LogWarning("[RechargeMaps] custom image decode failed: " + path); continue; }
                var sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
                _customImageSprites[ci.AssetId] = sprite;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RechargeMaps] custom image load failed for '" + ci.Path + "': " + e.Message);
            }
        }
    }

    private void SpawnDeco(JObject obj, Transform parent)
    {
        var name = obj["decoName"]?.Value<string>();
        var sprite = name != null ? RealAssetPalette.GetDecoSprite(name) : null;
        if (sprite == null) { Debug.LogWarning("[RechargeMaps] no deco sprite cached for '" + name + "' - visit a course with that prop first"); return; }
        SpawnSprite("Deco_" + name, sprite, obj, parent);
    }

    private void SpawnCustomImage(JObject obj, Transform parent)
    {
        var assetId = obj["assetId"]?.Value<string>();
        if (assetId == null || !_customImageSprites.TryGetValue(assetId, out var sprite))
        {
            Debug.LogWarning("[RechargeMaps] no custom image loaded for '" + assetId + "'");
            return;
        }
        SpawnSprite("CustomImage_" + assetId, sprite, obj, parent);
    }

    private void SpawnSprite(string goName, Sprite sprite, JObject obj, Transform parent)
    {
        var go = new GameObject(goName);
        go.transform.SetParent(parent, false);
        go.transform.position = WorldPos(obj);
        go.transform.rotation = Rot(obj);
        var scale = obj["scale"]?.Value<float>() ?? 1f;
        go.transform.localScale = Vector3.one * scale;
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
    }

    private void PaintTile(string tilemapName, JObject obj, Transform parent)
    {
        var template = RealAssetPalette.GetTilemapTemplate(tilemapName);
        if (template == null) { Debug.LogWarning("[RechargeMaps] no '" + tilemapName + "' tilemap template cached yet"); return; }

        EnsureTilemapChild(parent, tilemapName, template);
        var tilemap = parent.Find("Grid/" + tilemapName).GetComponent<Tilemap>();
        var cellX = obj["cellX"]?.Value<int>() ?? 0;
        var cellY = obj["cellY"]?.Value<int>() ?? 0;
        var tileName = obj["tileName"]?.Value<string>();
        var tile = tileName != null ? ResolveTile(tilemapName, obj)
            : tilemapName == "ground" && obj["tileIndex"] == null ? BlankGroundTile()
            : RealAssetPalette.GetTile(tilemapName, obj["tileIndex"]?.Value<int>() ?? 0);
        if (tile == null) { Debug.LogWarning("[RechargeMaps] no real tile '" + (tileName ?? "#0") + "' cached for '" + tilemapName + "'"); return; }
        var cellPos = new Vector3Int(cellX, cellY, 0);
        tilemap.SetTile(cellPos, tile);
        Debug.Log("[RechargeMaps] painted " + tilemapName + " cell " + cellPos + " -> world bottomLeft=" + tilemap.CellToWorld(cellPos) + " worldCenter=" + tilemap.GetCellCenterWorld(cellPos));
    }

    private void PaintColoredGround(JObject obj, Transform parent)
    {
        var color = obj["color"]?.Value<string>() ?? "blue";
        var tilemapName = color == "orange" ? "orangeBlocks" : "blueBlocks";
        PaintTile(tilemapName, obj, parent);
        RegisterWithSwapper(parent, tilemapName, color == "orange");
    }

    // A real tile on one of the game's own tilemaps: { tilemap, tileName,
    // cellX, cellY, matrix: [m00, m01, m10, m11] }. Spikes, kill blocks and the
    // overgrowth's thorn vines all come this way - the cloned tilemap keeps its
    // spikeScript, so they're deadly exactly as in the base game.
    private void PaintNamedTile(JObject obj, Transform parent)
    {
        var tilemapName = obj["tilemap"]?.Value<string>();
        if (string.IsNullOrEmpty(tilemapName)) { Debug.LogWarning("[RechargeMaps] tile object without a tilemap, skipped"); return; }
        PaintTile(tilemapName, obj, parent);

        var tilemap = parent.Find("Grid/" + tilemapName)?.GetComponent<Tilemap>();
        var m = obj["matrix"] as JArray;
        if (tilemap != null && m != null && m.Count == 4 && m.All(v => v.Type == JTokenType.Integer || v.Type == JTokenType.Float))
        {
            var cellPos = new Vector3Int(obj["cellX"]?.Value<int>() ?? 0, obj["cellY"]?.Value<int>() ?? 0, 0);
            var matrix = Matrix4x4.identity;
            matrix.m00 = m[0].Value<float>();
            matrix.m01 = m[1].Value<float>();
            matrix.m10 = m[2].Value<float>();
            matrix.m11 = m[3].Value<float>();
            tilemap.SetTileFlags(cellPos, TileFlags.None);
            tilemap.SetTransformMatrix(cellPos, matrix);
        }
        if (tilemapName.StartsWith("blue")) RegisterWithSwapper(parent, tilemapName, false);
        else if (tilemapName.StartsWith("orange")) RegisterWithSwapper(parent, tilemapName, true);
    }

    // Scene objects by name, built once per map load for "clone" lookups.
    private Dictionary<string, List<Transform>> _transformIndex;

    private static string HierarchyPath(Transform t)
    {
        var path = t.name;
        for (var p = t.parent; p != null; p = p.parent) path = p.name + "/" + path;
        return path;
    }

    // A copy of a real scene object (upgrade box, cutscene trigger...) found by
    // its hierarchy path - the one nearest its recorded spot when names repeat -
    // placed at the map position. Being a clone of the live object, it keeps all
    // its behaviour and references, exactly as in the base game.
    private void SpawnClone(JObject obj, Transform parent)
    {
        var path = obj["path"]?.Value<string>();
        if (string.IsNullOrEmpty(path)) return;
        var best = FindSceneObject(path, SourcePos(obj), parent);
        if (best == null) { Debug.LogWarning("[RechargeMaps] no scene object at '" + path + "' to clone"); return; }

        var clone = Instantiate(best.gameObject, WorldPos(obj), best.rotation, parent);
        clone.name = best.name;
        clone.SetActive(true);
    }

    private Transform FindSceneObject(string path, Vector2 src, Transform exclude)
    {
        if (_transformIndex == null)
        {
            _transformIndex = new Dictionary<string, List<Transform>>();
            foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (!t.gameObject.scene.IsValid()) continue;
                if (!_transformIndex.TryGetValue(t.name, out var list)) _transformIndex[t.name] = list = new List<Transform>();
                list.Add(t);
            }
        }
        var name = path.Substring(path.LastIndexOf('/') + 1);
        Transform best = null;
        var bestD = float.MaxValue;
        if (_transformIndex.TryGetValue(name, out var candidates))
        {
            foreach (var t in candidates)
            {
                if (t == null || (exclude != null && t.IsChildOf(exclude)) || HierarchyPath(t) != path) continue;
                // Prefer the live world's copy over the title scene's, then the nearest.
                var d = ((Vector2)t.position - src).sqrMagnitude - SceneRank(t) * 1e12f;
                if (d < bestD) { bestD = d; best = t; }
            }
        }
        return best;
    }

    private void EnsureTilemapChild(Transform parent, string name, Tilemap template)
    {
        // The clone lives under the Grid child, so look there - checking the
        // course root never matched and re-cloned the tilemap for every tile.
        if (parent.Find("Grid/" + name) != null) return;

        // Tilemap.CellToWorld/collision alignment need a Grid ancestor with the
        // real cellSize - the cached template was cloned as a bare Tilemap (no
        // Grid parent), which silently made CellToWorld collapse every cell to
        // the same position. Give it one, matching the real Grid found at scan time.
        var gridGo = parent.Find("Grid");
        if (gridGo == null)
        {
            gridGo = new GameObject("Grid").transform;
            gridGo.SetParent(parent, false);
            var grid = gridGo.gameObject.AddComponent<Grid>();
            grid.cellSize = RealAssetPalette.GroundCellSize;
        }

        var clone = Instantiate(template.gameObject, gridGo);
        clone.name = name;
        clone.SetActive(true);
    }

    private void RegisterWithSwapper(Transform parent, string tilemapName, bool isOrange)
    {
        var swapper = Singleton<colouredBlockSwapper>.Instance;
        var tilemapGo = parent.Find("Grid/" + tilemapName)?.gameObject;
        if (swapper == null || tilemapGo == null) return;

        var fieldName = isOrange ? "orange" : "blue";
        if (Reflect.FieldOf<colouredBlockSwapper>(fieldName) == null) return;

        var current = Reflect.GetField<GameObject[]>(swapper, fieldName) ?? Array.Empty<GameObject>();
        if (current.Contains(tilemapGo)) return;
        var updated = current.Concat(new[] { tilemapGo }).ToArray();
        Reflect.SetField(swapper, fieldName, updated);
    }

    private void SpawnGates(MapGroup group, Transform courseTransform, courseScript course)
    {
        var startPos = Live(new Vector2(_origin.x + group.StartX, _origin.y + group.StartY));
        var endPos = Live(new Vector2(_origin.x + group.EndX, _origin.y + group.EndY));

        var start = RealAssetPalette.Spawn<startGate>(startPos, Quaternion.identity, courseTransform);
        var end = RealAssetPalette.Spawn<endGate>(endPos, Quaternion.identity, courseTransform);

        // Gates are level fixtures, not physics props - force kinematic so gravity/
        // collision impulses from other spawned colliders can't drift them off their
        // intended spot. Also force isTrigger: the player spawns at the exact same
        // position as the start gate, and if its collider were ever non-trigger,
        // Unity's overlap-separation solver launches the (dynamic) player at high
        // speed to resolve the interpenetration - confirmed via position diagnostics
        // showing an instant ~350-unit horizontal launch immediately after spawn.
        foreach (var gateGo in new[] { start != null ? start.gameObject : null, end != null ? end.gameObject : null })
        {
            if (gateGo == null) continue;
            var gateBody = gateGo.GetComponent<Rigidbody2D>();
            if (gateBody != null) gateBody.bodyType = RigidbodyType2D.Kinematic;
            foreach (var col in gateGo.GetComponents<Collider2D>()) col.isTrigger = true;
        }

        if (start != null)
        {
            // resetPoint is a private GameObject ref the source scene wired to some
            // external marker that isn't part of the cloned subtree, so it comes
            // across null and startGate.OnTriggerStay2D NullRefs on every touch.
            if (Reflect.FieldOf<startGate>("resetPoint") != null && Reflect.GetField<GameObject>(start, "resetPoint") == null)
            {
                Reflect.SetField(start, "resetPoint", start.gameObject);
            }
        }

        if (end != null)
        {
            // The real endGate would otherwise call courseScript.stopTracking(player, true),
            // triggering the tier-multiplier reward calc. Rewards here are author-configured
            // (see MapRewardTrigger), so force this off - keeps every other real side effect
            // (tracking stop, courseResetPoint reset) intact.
            Reflect.TrySetField(end, "isEndOfCourse", false);

            if (group.Reward != null && group.Reward.Amount > 0 && Enum.TryParse(group.Reward.Currency, out globalStats.Currencies currency))
            {
                var trigger = end.gameObject.AddComponent<MapRewardTrigger>();
                trigger.Currency = currency;
                trigger.Amount = group.Reward.Amount;
            }
        }

        if (start == null || end == null)
        {
            Debug.LogWarning("[RechargeMaps] no startGate/endGate template cached yet - visit a real course first");
        }
    }

    private void MovePlayerIn(MapGroup group)
    {
        var playerGo = GameObject.FindGameObjectWithTag("Player");
        var movement = playerGo != null ? playerGo.GetComponent<Movement>() : null;
        if (movement == null) { Debug.LogWarning("[RechargeMaps] no controllable Player (with Movement) in scene - start the demo first, then load a map from the pause menu"); return; }

        var levelSpawn = new Vector2(_origin.x + (group.SpawnX ?? group.StartX), _origin.y + (group.SpawnY ?? group.StartY));
        var spawnPos = Live(levelSpawn);
        playerGo.transform.position = spawnPos;
        // A Rigidbody2D caches its own position and can silently snap transform.position
        // back on the next physics step unless the body itself is told too.
        var body = playerGo.GetComponent<Rigidbody2D>();
        if (body != null)
        {
            body.position = spawnPos;
            body.linearVelocity = Vector2.zero; // don't carry over pre-teleport momentum
        }
        // Level space (the game adds currentOrigin on respawn), and raised by the
        // 12 units Movement.respawn drops the player by, so they land on it.
        movement.respawnPoint = levelSpawn + RespawnLift;

        // The pocket is far from any real scene lighting - IGTAP uses URP 2D
        // (Light2D-lit), so without this everything renders pure black even
        // though it's all really there (confirmed via position diagnostics).
        if (_origin == PocketOrigin)
        {
            movement.lightActive = true;
            if (movement.personalLight != null) movement.personalLight.enabled = true;
        }

        if (movement.cam != null)
        {
            movement.cam.setup(spawnPos, movement.cam.camSize); // same as the game's own respawn: Movement's load does cam.setup(respawnPoint, savedCamSize)
            movement.cam.newTarget(playerGo, movement.cam.defaultoffset, true, Vector2.zero);
        }

        StartCoroutine(ReassertSpawnAfterDash(playerGo.transform, body, movement, levelSpawn));
    }

    // A dash (or any other in-flight input/impulse active on the exact frame a
    // map loads) can carry the player away from the intended spawn point before
    // this frame's position set takes visible effect - confirmed via diagnostics
    // showing dashActive=true immediately after teleport, launching the player
    // ~350 units before dashActive naturally clears ~0.3s later. Once nothing is
    // active, re-assert the real spawn position/velocity so it sticks.
    //
    // Also re-applies the camera setup at the end of this same wait: loading a
    // map on the very first frame a Player exists (PlayMap's deferred path,
    // used when a map is picked from the title screen with no gameplay scene
    // entered yet) races the real game's own camera/HUD initialization for
    // that fresh scene, which can run after MovePlayerIn's own cam.setup and
    // stomp it - producing a stuck, wrongly-zoomed camera. Re-asserting once
    // more here, after whatever's mid-flight has settled, wins that race.
    private System.Collections.IEnumerator ReassertSpawnAfterDash(Transform playerTransform, Rigidbody2D body, Movement movement, Vector2 levelSpawn)
    {
        float waited = 0f;
        while (movement.dashActive && waited < 1f)
        {
            yield return null;
            waited += Time.unscaledDeltaTime;
        }
        if (playerTransform == null) yield break;

        // Tilemap colliders painted this frame only exist after a physics step.
        yield return new WaitForFixedUpdate();
        yield return new WaitForFixedUpdate();
        if (playerTransform == null) yield break;
        // Recomputed from level space now: teleporting far (e.g. into the
        // pocket) makes FloatingOrigin shift the world in between.
        var spawnPos = SnapToGround(playerTransform, Live(levelSpawn));
        movement.respawnPoint = LevelOf(spawnPos) + RespawnLift;

        playerTransform.position = spawnPos;
        if (body != null)
        {
            body.position = spawnPos;
            body.linearVelocity = Vector2.zero;
        }

        if (movement.cam != null)
        {
            movement.cam.setup(spawnPos, movement.cam.camSize);
            movement.cam.newTarget(playerTransform.gameObject, movement.cam.defaultoffset, true, Vector2.zero);
        }
    }

    // Stands the player on the first solid surface below the spawn, so they
    // (and every respawn) start on the ground instead of dropping in mid-air.
    private static Vector3 SnapToGround(Transform player, Vector3 spawnPos)
    {
        var col = player.GetComponent<Collider2D>();
        if (col == null) return spawnPos;
        player.position = spawnPos;
        Physics2D.SyncTransforms();
        var feet = spawnPos.y - col.bounds.min.y;
        var ground = LayerMask.GetMask("Ground");
        var mask = ground != 0 ? ground : ~0;
        // A spawn inside ground (a raycast from in there hits at once) climbs
        // out the top first, so the player stands on it rather than in it.
        for (int n = 0; n < 400 && InSolid(spawnPos, mask, player); n++) spawnPos.y += 4f;
        RaycastHit2D best = default;
        foreach (var hit in Physics2D.RaycastAll(spawnPos, Vector2.down, 3000f, mask))
        {
            if (hit.collider == null || hit.collider.isTrigger || hit.collider.transform.IsChildOf(player)) continue;
            if (best.collider == null || hit.distance < best.distance) best = hit;
        }
        if (best.collider == null) return spawnPos;
        return new Vector3(spawnPos.x, best.point.y + feet + 0.5f, spawnPos.z);
    }

    private static bool InSolid(Vector2 point, int mask, Transform player)
    {
        foreach (var c in Physics2D.OverlapPointAll(point, mask))
            if (c != null && !c.isTrigger && !c.transform.IsChildOf(player)) return true;
        return false;
    }

    // Author-placed blocks: a plain grey tile (the map maker's block colour)
    // with a full-cell collider, so they read as "yours" against the real art.
    private static Tile _blankGround;
    private static Tile BlankGroundTile()
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

    private static void ForceTriggerColliders(GameObject go)
    {
        foreach (var col in go.GetComponents<Collider2D>()) col.isTrigger = true;
    }

    private static void SetPrivate(object target, string fieldName, object value)
    {
        Reflect.TrySetField(target, fieldName, value);
    }

    private static void SetStructField(Type structType, ref object boxed, string fieldName, object value)
    {
        Reflect.TrySetField(boxed, fieldName, value); // boxed is a reference, so this mutates the boxed struct in place
    }
}
