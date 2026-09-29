using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Recharge.ModApi;
using UnityEngine;
using UnityEngine.Tilemaps;

// Every object in a map's group, built into a MapWorld. One handler per
// object type, the same for overlay and custom maps; the few that only make
// sense on the real level (erasing, hiding, editing its objects) say so.
internal static class MapObjects
{
    private static readonly Dictionary<string, Action<MapWorld, JObject>> Handlers = new Dictionary<string, Action<MapWorld, JObject>>
    {
        ["ground"] = (w, o) => PaintTile(w, "ground", o),
        ["coloredGround"] = (w, o) => PaintTile(w, o["color"]?.Value<string>() == "orange" ? "orangeBlocks" : "blueBlocks", o),
        ["tile"] = (w, o) => PaintTile(w, o["tilemap"]?.Value<string>() ?? throw new Exception("tile without a tilemap"), o),
        ["trueSpike"] = SpawnTrueSpike,
        ["clone"] = SpawnClone,
        ["erase"] = LevelOnly(Erase),
        ["hide"] = LevelOnly(Hide),
        ["modify"] = LevelOnly(Modify),
        // Older map files.
        ["spike"] = (w, o) => SpawnTemplate<spikeScript>(w, o),
        ["checkpoint"] = (w, o) => SpawnTemplate<checkpointScript>(w, o),
        ["spring"] = SpawnSpring,
        ["platform"] = SpawnPlatform,
        ["deco"] = SpawnDeco,
        ["customImage"] = SpawnCustomImage,
    };

    private static Action<MapWorld, JObject> LevelOnly(Action<MapWorld, JObject> handler) => (w, o) =>
    {
        if (!w.Overlay) throw new Exception("only works on a map made on the real level");
        handler(w, o);
    };

    // Returns how many objects were built. One bad object never stops the rest.
    public static int Build(MapWorld w)
    {
        LoadCustomImages(w);
        int done = 0;
        foreach (var obj in w.Group.Objects)
        {
            var type = obj["type"]?.Value<string>() ?? "";
            try
            {
                if (!Handlers.TryGetValue(type, out var handler)) throw new Exception("unknown object type");
                handler(w, obj);
                done++;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[RechargeMaps] couldn't build " + type + " " + obj.ToString(Formatting.None) + ": " + e.Message);
            }
        }
        return done;
    }

    // ---- tiles ----

    private static void PaintTile(MapWorld w, string tilemapName, JObject obj)
    {
        var tilemap = w.Tilemap(tilemapName);
        var tile = TileFor(tilemapName, obj) ?? throw new Exception("no tile '" + (obj["tileName"] ?? obj["tileIndex"]) + "' for '" + tilemapName + "'");
        w.Paint(tilemap, w.CellOf(tilemap, obj), tile, MapWorld.MatrixOf(obj));
    }

    private static TileBase TileFor(string tilemapName, JObject obj)
    {
        if (obj["tileName"] != null) return MapTiles.Resolve(tilemapName, obj);
        if (obj["tileIndex"] != null) return RealAssetPalette.GetTile(tilemapName, obj["tileIndex"].Value<int>());
        return tilemapName == "ground" ? MapTiles.BlankGround() : RealAssetPalette.GetTile(tilemapName, 0);
    }

    // Clears the real tiles under a point: on one named tilemap (a vine's,
    // a decoration's), or on every solid / deadly one (an erased cell).
    private static readonly string[] Erasable =
    {
        "ground", "lightBlocker", "InvisibleWall", "OvergrowthDestroyedGround", "moss", "OvergrowthMoss",
        "blueBlocks", "orangeBlocks", "Spikes", "hiddenSpikes", "backgroundSpikes1", "backgroundSpikes2",
        "OvergrowthSpikes", "blueSpikes", "orangeSpikes",
    };

    private static void Erase(MapWorld w, JObject obj)
    {
        var only = obj["tilemap"]?.Value<string>();
        var point = w.Point(obj);
        foreach (var name in only != null ? new[] { only } : Erasable)
        {
            var tilemap = w.RealTilemap(name);
            if (tilemap != null) w.Erase(tilemap, tilemap.WorldToCell(point));
        }
    }

    // A "true spike": a real spike tile on the map's own tinted copy of the
    // Spikes tilemap - the game's spikeScript kills with it like any spike -
    // plus a trigger reaching further than a normal spike's.
    private const string TrueSpikes = "TrueSpikes";

    private static void SpawnTrueSpike(MapWorld w, JObject obj)
    {
        var tilemap = w.OwnTilemap(TrueSpikes, "Spikes");
        if (obj["color"] is JArray c && c.Count >= 3)
        {
            tilemap.color = new Color(c[0].Value<float>(), c[1].Value<float>(), c[2].Value<float>(), 1f);
            // The tilemap's own material ignores tint; a sprite one doesn't.
            var lit = RealAssetPalette.Get<SpringScript>()?.GetComponentInChildren<SpriteRenderer>(true);
            var rend = tilemap.GetComponent<TilemapRenderer>();
            if (lit != null && rend != null) rend.sharedMaterial = lit.sharedMaterial;
        }
        var tile = MapTiles.Resolve("Spikes", obj) ?? throw new Exception("no spike tile '" + obj["tileName"] + "'");
        var cell = w.CellOf(tilemap, obj);
        var turn = MapWorld.Rot(obj);
        w.Paint(tilemap, cell, tile, MapWorld.MatrixOf(obj) ?? Matrix4x4.Rotate(turn));

        if (!(obj["hitbox"] is JArray hb) || hb.Count != 4) return;
        float x0 = hb[0].Value<float>(), y0 = hb[1].Value<float>(), x1 = hb[2].Value<float>(), y1 = hb[3].Value<float>();
        var centre = tilemap.GetCellCenterWorld(cell);
        var poly = tilemap.gameObject.AddComponent<PolygonCollider2D>();
        poly.isTrigger = true;
        poly.points = new[] { new Vector2(x0, y0), new Vector2(x1, y0), new Vector2(x1, y1), new Vector2(x0, y1) }
            .Select(p => (Vector2)tilemap.transform.InverseTransformPoint(centre + turn * (Vector3)p)).ToArray();
    }

    // ---- the level's own objects ----

    // A copy of a real scene object (upgrade box, teleporter, zip mover...) at
    // the map position. Being a clone of the live object, it keeps all its
    // behaviour and references, exactly as in the base game.
    private static void SpawnClone(MapWorld w, JObject obj)
    {
        var path = obj["path"]?.Value<string>();
        var source = w.FindSceneObject(path, MapWorld.SourcePoint(obj)) ?? throw new Exception("no scene object at '" + path + "' to copy");
        var clone = UnityEngine.Object.Instantiate(source.gameObject, w.Point(obj), source.rotation, w.Root);
        clone.name = source.name;
        clone.SetActive(true);
        var course = obj["course"]?.Value<string>();
        if (!string.IsNullOrEmpty(course)) w.Links.Add((clone, course));
        if (obj["teleport"] is JObject teleport) w.Teleports.Add((clone, teleport));
        // Course checkpoints: flagged by the editor, or (older maps) a copy of one of the level's.
        if (obj["courseCheckpoint"]?.Value<bool>() == true || (path.StartsWith("Courses/") && clone.GetComponentInChildren<checkpointScript>(true) != null))
            MapRespawn.MakeCourseCheckpoint(clone);
        try { MapCloneConfig.Apply(clone, obj); }
        catch (Exception e) { Debug.LogWarning("[RechargeMaps] settings for '" + path + "' failed: " + e.Message); }
    }

    private static Transform LevelObject(MapWorld w, JObject obj)
    {
        var path = obj["path"]?.Value<string>();
        return w.FindSceneObject(path, MapWorld.SourcePoint(obj)) ?? throw new Exception("no scene object at '" + path + "'");
    }

    private static void Hide(MapWorld w, JObject obj)
    {
        var target = LevelObject(w, obj).gameObject;
        if (obj["rendererOnly"]?.Value<bool>() != true) { w.SetActive(target, false); return; }
        var sr = target.GetComponent<SpriteRenderer>() ?? throw new Exception("no sprite to hide");
        var shown = sr.enabled;
        sr.enabled = false;
        w.OnUnload(() => { if (sr != null) sr.enabled = shown; });
    }

    private static void Modify(MapWorld w, JObject obj)
    {
        var target = LevelObject(w, obj).gameObject;
        if (!(obj["upgrade"] is JObject upgrade)) return;
        var undo = MapUpgrades.OverrideBox(target, upgrade);
        if (undo != null) w.OnUnload(undo);
    }

    // ---- older map files ----

    private static T SpawnTemplate<T>(MapWorld w, JObject obj) where T : Component
    {
        var spawned = RealAssetPalette.Spawn<T>(w.Point(obj), MapWorld.Rot(obj), w.Root) ?? throw new Exception("no " + typeof(T).Name + " seen in the level yet");
        foreach (var col in spawned.GetComponents<Collider2D>()) col.isTrigger = true;
        return spawned;
    }

    private static void SpawnSpring(MapWorld w, JObject obj)
    {
        var spring = SpawnTemplate<SpringScript>(w, obj);
        if (obj["strength"] != null) Reflect.TrySetField(spring, "strength", obj["strength"].Value<float>());
        if (obj["upForce"] != null) Reflect.TrySetField(spring, "upForce", obj["upForce"].Value<float>());
    }

    private static void SpawnPlatform(MapWorld w, JObject obj)
    {
        var platform = RealAssetPalette.Spawn<PlatformMover>(w.Point(obj), MapWorld.Rot(obj), w.Root) ?? throw new Exception("no PlatformMover seen in the level yet");
        if (!(obj["positions"] is JArray list) || list.Count == 0) return;
        var dataType = Reflect.TryNestedType<PlatformMover>("PositionData") ?? throw new Exception("PlatformMover.PositionData not found");
        var tweenType = Reflect.TryNestedType<PlatformMover>("TweenType");
        var array = Array.CreateInstance(dataType, list.Count);
        for (int i = 0; i < list.Count; i++)
        {
            var p = (JObject)list[i];
            object boxed = Activator.CreateInstance(dataType);
            Reflect.TrySetField(boxed, "position", new Vector2(p["x"]?.Value<float>() ?? 0f, p["y"]?.Value<float>() ?? 0f));
            Reflect.TrySetField(boxed, "timeToReachFromPrevious", p["timeToReachFromPrevious"]?.Value<float>() ?? 0f);
            Reflect.TrySetField(boxed, "autoStartNextPhase", p["autoStartNextPhase"]?.Value<bool>() ?? false);
            Reflect.TrySetField(boxed, "nextPhaseOnEnter", p["nextPhaseOnEnter"]?.Value<bool>() ?? false);
            Reflect.TrySetField(boxed, "nextPhaseOnExit", p["nextPhaseOnExit"]?.Value<bool>() ?? false);
            Reflect.TrySetField(boxed, "waitOnPhaseEnd", p["waitOnPhaseEnd"]?.Value<float>() ?? 0f);
            if (tweenType != null)
            {
                object tween;
                try { tween = Enum.Parse(tweenType, p["tween"]?.Value<string>() ?? "linear", ignoreCase: true); }
                catch { tween = Enum.ToObject(tweenType, 0); }
                Reflect.TrySetField(boxed, "wayToTweenTo", tween);
            }
            array.SetValue(boxed, i);
        }
        Reflect.TrySetField(platform, "Positions", array);
        // NONE: not gated behind the zip mover unlock.
        var typeField = Reflect.FieldOf<PlatformMover>("PlatformType");
        if (typeField != null) typeField.SetValue(platform, Enum.ToObject(typeField.FieldType, 0));
        platform.JumpToState(0);
    }

    private static void SpawnDeco(MapWorld w, JObject obj)
    {
        var name = obj["decoName"]?.Value<string>();
        var sprite = (name != null ? RealAssetPalette.GetDecoSprite(name) : null) ?? throw new Exception("no decoration '" + name + "' seen in the level yet");
        SpawnSprite(w, "Deco_" + name, sprite, obj);
    }

    private static void SpawnCustomImage(MapWorld w, JObject obj)
    {
        var id = obj["assetId"]?.Value<string>();
        if (id == null || !w.CustomImages.TryGetValue(id, out var sprite)) throw new Exception("no image '" + id + "' in the map");
        SpawnSprite(w, "CustomImage_" + id, sprite, obj);
    }

    private static void SpawnSprite(MapWorld w, string name, Sprite sprite, JObject obj)
    {
        var go = new GameObject(name);
        go.transform.SetParent(w.Root, false);
        go.transform.SetPositionAndRotation(w.Point(obj), MapWorld.Rot(obj));
        go.transform.localScale = Vector3.one * (obj["scale"]?.Value<float>() ?? 1f);
        go.AddComponent<SpriteRenderer>().sprite = sprite;
    }

    private static void LoadCustomImages(MapWorld w)
    {
        foreach (var image in w.Def.CustomImages ?? new List<MapCustomImage>())
        {
            if (string.IsNullOrEmpty(image.AssetId) || string.IsNullOrEmpty(image.Path)) continue;
            var path = Path.Combine(MapPaths.MapsDir, w.MapId, image.Path);
            try
            {
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(path))) throw new Exception("not an image");
                w.CustomImages[image.AssetId] = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            }
            catch (Exception e) { Debug.LogWarning("[RechargeMaps] custom image '" + image.Path + "': " + e.Message); }
        }
    }
}
