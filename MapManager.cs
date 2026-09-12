using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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

    private static readonly BindingFlags NonPublicInstance = BindingFlags.NonPublic | BindingFlags.Instance;

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
            RealAssetPalette.ScanCurrentScene();
            TryCaptureSpringAnimation();
            if (_pendingMapId != null) StartCoroutine(LoadPendingMapWhenPlayerReady());
        };
        RealAssetPalette.ScanCurrentScene();
        TryCaptureSpringAnimation();
    }

    private string _pendingMapId;

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

    private System.Collections.IEnumerator LoadPendingMapWhenPlayerReady()
    {
        var mapId = _pendingMapId;
        _pendingMapId = null;
        float waited = 0f;
        while (waited < 10f)
        {
            var playerGo = GameObject.FindGameObjectWithTag("Player");
            if (playerGo != null && playerGo.GetComponent<Movement>() != null) break;
            yield return null;
            waited += Time.unscaledDeltaTime;
        }
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

            LoadCustomImages(mapId, def);
            SpawnGroup(mapId, def.Groups[0]);
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

    private void SpawnGroup(string mapId, MapGroup group)
    {
        var courseTemplate = RealAssetPalette.Get<courseScript>();
        if (courseTemplate == null)
        {
            Debug.LogError("[RechargeMaps] no courseScript template cached yet - visit a course area first, then reopen the Maps menu");
            return;
        }

        if (_currentCourseGo != null) { Destroy(_currentCourseGo); _currentCourseGo = null; }

        var courseGo = Instantiate(courseTemplate.gameObject, PocketOrigin, Quaternion.identity);
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
        typeof(courseScript).GetField("isOnPauseMenu", NonPublicInstance)?.SetValue(course, false);
        try { course.load(SaveFolder); } catch (Exception e) { Debug.LogWarning("[RechargeMaps] course.load failed (expected on first play): " + e.Message); }

        foreach (var obj in group.Objects)
        {
            var type = obj["type"]?.Value<string>();
            switch (type)
            {
                case "ground": PaintTile("ground", obj, courseGo.transform); break;
                case "coloredGround": PaintColoredGround(obj, courseGo.transform); break;
                case "spike": SpawnSimple<spikeScript>(obj, courseGo.transform); break;
                case "checkpoint": SpawnSimple<checkpointScript>(obj, courseGo.transform); break;
                case "spring": SpawnSpring(obj, courseGo.transform); break;
                case "platform": SpawnPlatform(obj, courseGo.transform); break;
                case "deco": SpawnDeco(obj, courseGo.transform); break;
                case "customImage": SpawnCustomImage(obj, courseGo.transform); break;
                default: Debug.LogWarning("[RechargeMaps] unknown object type '" + type + "', skipped"); break;
            }
        }

        SpawnGates(group, courseGo.transform, course);
        MovePlayerIn(group);

        Debug.Log("[RechargeMaps] spawned map '" + mapId + "' (courseNumber=" + course.courseNumber + ") at pocket " + PocketOrigin);
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

    private static Vector3 WorldPos(JObject obj)
    {
        var x = obj["x"]?.Value<float>() ?? 0f;
        var y = obj["y"]?.Value<float>() ?? 0f;
        return new Vector3(PocketOrigin.x + x, PocketOrigin.y + y, 0f);
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

        var positionDataType = typeof(PlatformMover).GetNestedType("PositionData", BindingFlags.NonPublic);
        var tweenType = typeof(PlatformMover).GetNestedType("TweenType", BindingFlags.NonPublic);
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

        typeof(PlatformMover).GetField("Positions", NonPublicInstance)?.SetValue(platform, array);
        var platformTypeField = typeof(PlatformMover).GetField("PlatformType", NonPublicInstance);
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
        var tile = tileName != null
            ? RealAssetPalette.GetTileByName(tilemapName, tileName)
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

    private void EnsureTilemapChild(Transform parent, string name, Tilemap template)
    {
        if (parent.Find(name) != null) return;

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
        var field = typeof(colouredBlockSwapper).GetField(fieldName, NonPublicInstance);
        if (field == null) return;

        var current = (GameObject[])field.GetValue(swapper) ?? Array.Empty<GameObject>();
        if (current.Contains(tilemapGo)) return;
        var updated = current.Concat(new[] { tilemapGo }).ToArray();
        field.SetValue(swapper, updated);
    }

    private void SpawnGates(MapGroup group, Transform courseTransform, courseScript course)
    {
        var startPos = new Vector3(PocketOrigin.x + group.StartX, PocketOrigin.y + group.StartY, 0f);
        var endPos = new Vector3(PocketOrigin.x + group.EndX, PocketOrigin.y + group.EndY, 0f);

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
            var resetPointField = typeof(startGate).GetField("resetPoint", NonPublicInstance);
            if (resetPointField != null && resetPointField.GetValue(start) == null)
            {
                resetPointField.SetValue(start, start.gameObject);
            }
        }

        if (end != null)
        {
            // The real endGate would otherwise call courseScript.stopTracking(player, true),
            // triggering the tier-multiplier reward calc. Rewards here are author-configured
            // (see MapRewardTrigger), so force this off - keeps every other real side effect
            // (tracking stop, courseResetPoint reset) intact.
            typeof(endGate).GetField("isEndOfCourse", NonPublicInstance)?.SetValue(end, false);

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

        var spawnPos = new Vector3(PocketOrigin.x + group.StartX, PocketOrigin.y + group.StartY, 0f);
        playerGo.transform.position = spawnPos;
        // A Rigidbody2D caches its own position and can silently snap transform.position
        // back on the next physics step unless the body itself is told too.
        var body = playerGo.GetComponent<Rigidbody2D>();
        if (body != null)
        {
            body.position = spawnPos;
            body.linearVelocity = Vector2.zero; // don't carry over pre-teleport momentum
        }
        movement.respawnPoint = spawnPos;

        // The pocket is far from any real scene lighting - IGTAP uses URP 2D
        // (Light2D-lit), so without this everything renders pure black even
        // though it's all really there (confirmed via position diagnostics).
        movement.lightActive = true;
        if (movement.personalLight != null) movement.personalLight.enabled = true;

        if (movement.cam != null)
        {
            movement.cam.setup(spawnPos, 40f); // TEMP: wider than default for Phase-1 visual verification
            movement.cam.newTarget(playerGo, movement.cam.defaultoffset, true, Vector2.zero);
        }

        StartCoroutine(ReassertSpawnAfterDash(playerGo.transform, body, movement, spawnPos));
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
    private System.Collections.IEnumerator ReassertSpawnAfterDash(Transform playerTransform, Rigidbody2D body, Movement movement, Vector3 spawnPos)
    {
        float waited = 0f;
        while (movement.dashActive && waited < 1f)
        {
            yield return null;
            waited += Time.unscaledDeltaTime;
        }
        if (playerTransform == null) yield break;

        playerTransform.position = spawnPos;
        if (body != null)
        {
            body.position = spawnPos;
            body.linearVelocity = Vector2.zero;
        }

        if (movement.cam != null)
        {
            movement.cam.setup(spawnPos, 40f);
            movement.cam.newTarget(playerTransform.gameObject, movement.cam.defaultoffset, true, Vector2.zero);
        }
    }

    private static void ForceTriggerColliders(GameObject go)
    {
        foreach (var col in go.GetComponents<Collider2D>()) col.isTrigger = true;
    }

    private static void SetPrivate(object target, string fieldName, object value)
    {
        target.GetType().GetField(fieldName, NonPublicInstance)?.SetValue(target, value);
    }

    private static void SetStructField(Type structType, ref object boxed, string fieldName, object value)
    {
        var field = structType.GetField(fieldName, NonPublicInstance | BindingFlags.Public);
        field?.SetValue(boxed, value);
    }
}
