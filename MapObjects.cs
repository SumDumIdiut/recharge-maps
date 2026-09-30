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
        ["freeSpike"] = SpawnFreeSpike,
        ["sign"] = SpawnSign,
        ["trigger"] = SpawnTrigger,
        ["customSprite"] = SpawnCustomSprite,
        ["gameSprite"] = SpawnGameSprite,
        ["clone"] = SpawnClone,
        ["erase"] = LevelOnly(Erase),
        ["hide"] = LevelOnly(Hide),
        ["modify"] = LevelOnly(Modify),
        ["move"] = LevelOnly(Move),
        ["transform"] = LevelOnly(TransformLevel),
        ["order"] = LevelOnly(OrderLevel),
        ["group"] = LevelOnly(GroupLevel),
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
        try { return BuildAll(w); }
        finally
        {
            MapLayering.Apply(w);
            try { MapDoors.Apply(w); }
            catch (Exception e) { Debug.LogWarning("[RechargeMaps] linking doors failed: " + e.Message); }
            try { MapLongFalls.Apply(w); }
            catch (Exception e) { Debug.LogWarning("[RechargeMaps] long-fall zones failed: " + e.Message); }
        }
    }

    private static int BuildAll(MapWorld w)
    {
        try { return BuildEach(w); }
        finally { w.FlushPaint(); MapGroups.HideStarting(w); }
    }

    // The overgrown stages' edits wait for MapStages, which builds the set for the level's state.
    private static int BuildEach(MapWorld w)
    {
        var shared = new List<JObject>();
        foreach (var obj in w.Group.Objects)
        {
            var state = obj["state"]?.Value<string>();
            if (state == null) { shared.Add(obj); continue; }
            if (!w.StageObjects.TryGetValue(state, out var list)) w.StageObjects[state] = list = new List<JObject>();
            list.Add(obj);
        }
        return BuildObjects(w, shared);
    }

    public static int BuildStage(MapWorld w, string state)
    {
        if (!w.StageObjects.TryGetValue(state, out var list)) return 0;
        w.BeginStage(state);
        try
        {
            var built = BuildObjects(w, list);
            w.FlushPaint();
            MapGroups.HideStarting(w);
            MapLayering.Apply(w);
            return built;
        }
        finally { w.EndStage(); }
    }

    private static int BuildObjects(MapWorld w, List<JObject> objects)
    {
        LoadCustomImages(w);
        int done = 0;
        foreach (var obj in objects)
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
        // A level tilemap that's switched off in this map's state (the overgrowth's
        // thorn vines at the start of the game): paint the map's own copy, which is on.
        if (w.Overlay && !tilemap.gameObject.activeInHierarchy) tilemap = w.OwnTilemap(tilemapName + " (map)", tilemapName);
        tilemap = Grouped(w, tilemap, tilemapName, obj);
        var tile = TileFor(tilemapName, obj) ?? throw new Exception("no tile '" + (obj["tileName"] ?? obj["tileIndex"]) + "' for '" + tilemapName + "'");
        w.Paint(tilemap, w.CellOf(tilemap, obj), tile, MapWorld.MatrixOf(obj));
    }

    // A grouped tile goes on the group's own copy of its tilemap, so the group can switch it off or move it.
    private static Tilemap Grouped(MapWorld w, Tilemap tilemap, string tilemapName, JObject obj)
    {
        var g = MapGroups.Of(obj);
        if (g == null) return tilemap;
        var own = w.OwnTilemap(tilemap.name + " #" + g, tilemapName == TrueSpikes ? "Spikes" : tilemapName);
        if (tilemapName == TrueSpikes || tilemap.name.EndsWith(" (map)"))
        {
            own.color = tilemap.color;
            var from = tilemap.GetComponent<TilemapRenderer>();
            var to = own.GetComponent<TilemapRenderer>();
            if (from != null && to != null) to.sharedMaterial = from.sharedMaterial;
        }
        MapGroups.Add(w, obj, own.gameObject);
        return own;
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
        tilemap = Grouped(w, tilemap, TrueSpikes, obj);
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

    // A spike placed off the grid: the spike tile's sprite, turned as the tile
    // would be, with its hitbox shapes and the game's own spikeScript.
    private static void SpawnFreeSpike(MapWorld w, JObject obj)
    {
        var tilemapName = obj["tilemap"]?.Value<string>() ?? "Spikes";
        var tile = MapTiles.Resolve(tilemapName, obj) as Tile;
        if (tile == null || tile.sprite == null) throw new Exception("no spike tile '" + obj["tileName"] + "'");
        var go = new GameObject("FreeSpike");
        var holder = w.FreeSpikeHolder(tilemapName);
        go.layer = holder.gameObject.layer;
        go.transform.SetParent(holder, false);
        go.transform.position = w.Point(obj);

        var art = new GameObject("Sprite");
        art.transform.SetParent(go.transform, false);
        var m = MapWorld.MatrixOf(obj) ?? Matrix4x4.identity;
        var sx = Mathf.Sqrt(m.m00 * m.m00 + m.m10 * m.m10);
        var det = m.m00 * m.m11 - m.m01 * m.m10;
        art.transform.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(m.m10, m.m00) * Mathf.Rad2Deg);
        art.transform.localScale = new Vector3(sx, sx > 0f ? det / sx : 1f, 1f);
        var sr = art.AddComponent<SpriteRenderer>();
        sr.sprite = tile.sprite;
        var real = w.RealTilemap(tilemapName);
        var rend = real != null ? real.GetComponent<TilemapRenderer>() : null;
        if (rend != null) { sr.sortingLayerID = rend.sortingLayerID; sr.sortingOrder = rend.sortingOrder; }
        // A tilemap's material ignores colour on a sprite; a sprite one doesn't.
        var lit = RealAssetPalette.Get<SpringScript>()?.GetComponentInChildren<SpriteRenderer>(true);
        if (lit != null) sr.sharedMaterial = lit.sharedMaterial;
        if (obj["color"] is JArray c && c.Count >= 3) sr.color = new Color(c[0].Value<float>(), c[1].Value<float>(), c[2].Value<float>(), 1f);

        if (obj["shape"] is JArray shape)
            foreach (var poly in shape.OfType<JArray>())
            {
                var points = poly.OfType<JArray>().Where(p => p.Count == 2).Select(p => new Vector2(p[0].Value<float>(), p[1].Value<float>())).ToArray();
                if (points.Length < 3) continue;
                var col = go.AddComponent<PolygonCollider2D>();
                col.isTrigger = true;
                col.points = points;
            }
        go.AddComponent<spikeScript>();
        MapGroups.Add(w, obj, go);
        if (obj["order"] != null) w.Stacked.Add((obj["order"].Value<int>(), go));
        if (obj["behind"]?.Value<bool>() == true) (obj["depth"]?.Value<int>() == 2 ? w.BehindWalls : w.Behind).Add(go);
    }

    // A zone acting as the player walks in: switching the music / background,
    // a group, or doing something to the player. A kill zone is the game's own spikeScript.
    private static void SpawnTrigger(MapWorld w, JObject obj)
    {
        var kind = obj["kind"]?.Value<string>() ?? "media";
        var go = new GameObject("Trigger_" + kind);
        go.transform.SetParent(w.Root, false);
        go.transform.position = w.Point(obj);
        var box = go.AddComponent<BoxCollider2D>();
        box.isTrigger = true;
        box.size = new Vector2(obj["w"]?.Value<float>() ?? 256f, obj["h"]?.Value<float>() ?? 256f);
        if (kind == "media")
        {
            var media = go.AddComponent<MapMediaTrigger>();
            media.Music = obj["music"];
            media.Background = obj["background"];
            return;
        }
        if (kind == "kill")
        {
            var spikes = w.RealTilemap("Spikes");
            if (spikes != null) go.layer = spikes.gameObject.layer;
            go.AddComponent<spikeScript>();
            return;
        }
        var t = go.AddComponent<MapGroupTrigger>();
        t.World = w;
        t.Kind = kind;
        t.Group = obj["group"]?.Value<string>();
        t.Once = obj["once"]?.Value<bool>() ?? false;
        t.Offset = new Vector2(obj["dx"]?.Value<float>() ?? 0f, obj["dy"]?.Value<float>() ?? 0f);
        t.Back = obj["back"]?.Value<bool>() ?? false;
        t.Seconds = obj[kind == "message" ? "seconds" : "time"]?.Value<float>() ?? (kind == "message" ? 3f : 1f);
        t.Target = w.LevelPoint(obj["tx"]?.Value<float>() ?? 0f, obj["ty"]?.Value<float>() ?? 0f);
        t.Zoom = Mathf.Max(0.2f, obj["size"]?.Value<float>() ?? 1f);
        t.Text = obj["text"]?.Value<string>();
    }

    // One of the game's own sprites the level never places (its plant art), by name, centred where the editor put it.
    private static void SpawnGameSprite(MapWorld w, JObject obj)
    {
        var name = obj["sprite"]?.Value<string>();
        var sprite = RealAssetPalette.SpriteByName(name) ?? throw new Exception("no sprite '" + name + "' in the game");
        var go = new GameObject("Plant_" + name);
        go.transform.SetParent(w.Root, false);
        go.transform.SetPositionAndRotation(w.Point(obj), MapWorld.Rot(obj));
        go.transform.localScale = new Vector3(obj["scaleX"]?.Value<float>() ?? 1f, obj["scaleY"]?.Value<float>() ?? 1f, 1f);
        var art = new GameObject("Sprite");
        art.transform.SetParent(go.transform, false);
        // The editor places the sprite's centre; the sprite's own pivot may be elsewhere.
        art.transform.localPosition = (sprite.pivot - sprite.rect.size / 2f) / sprite.pixelsPerUnit;
        var sr = art.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.flipX = obj["flipX"]?.Value<bool>() ?? false;
        sr.flipY = obj["flipY"]?.Value<bool>() ?? false;
        if (sr.flipX) art.transform.localPosition = new Vector3(-art.transform.localPosition.x, art.transform.localPosition.y, 0f);
        if (sr.flipY) art.transform.localPosition = new Vector3(art.transform.localPosition.x, -art.transform.localPosition.y, 0f);
        if (obj["alpha"] != null) sr.color = new Color(1f, 1f, 1f, Mathf.Clamp01(obj["alpha"].Value<float>()));
        var lit = RealAssetPalette.Get<SpringScript>()?.GetComponentInChildren<SpriteRenderer>(true);
        if (lit != null) sr.sharedMaterial = lit.sharedMaterial;
        var deco = w.RealTilemap("ground")?.GetComponent<TilemapRenderer>();
        if (deco != null) { sr.sortingLayerID = deco.sortingLayerID; sr.sortingOrder = deco.sortingOrder - 1; }
        MapGroups.Add(w, obj, go);
        if (obj["order"] != null) w.Stacked.Add((obj["order"].Value<int>(), go));
        if (obj["behind"]?.Value<bool>() == true) (obj["depth"]?.Value<int>() == 2 ? w.BehindWalls : w.Behind).Add(go);
    }

    // One of the map's own images, placed like a decoration.
    private static void SpawnCustomSprite(MapWorld w, JObject obj)
    {
        var file = obj["image"]?.Value<string>();
        var sprite = (file != null ? MapMedia.LoadImage(file, false) : null) ?? throw new Exception("no image '" + file + "' in the map's assets");
        var go = new GameObject("Image_" + file);
        go.transform.SetParent(w.Root, false);
        go.transform.SetPositionAndRotation(w.Point(obj), MapWorld.Rot(obj));
        var scale = obj["scale"]?.Value<float>() ?? 1f;
        go.transform.localScale = new Vector3(obj["scaleX"]?.Value<float>() ?? scale, obj["scaleY"]?.Value<float>() ?? scale, 1f);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.flipX = obj["flipX"]?.Value<bool>() ?? false;
        sr.flipY = obj["flipY"]?.Value<bool>() ?? false;
        if (obj["alpha"] != null) sr.color = new Color(1f, 1f, 1f, Mathf.Clamp01(obj["alpha"].Value<float>()));
        // Drawn like the level's decorations: in front of the walls, behind the ground.
        var deco = w.RealTilemap("ground")?.GetComponent<TilemapRenderer>();
        if (deco != null) { sr.sortingLayerID = deco.sortingLayerID; sr.sortingOrder = deco.sortingOrder - 1; }
        MapGroups.Add(w, obj, go);
        if (obj["order"] != null) w.Stacked.Add((obj["order"].Value<int>(), go));
        if (obj["behind"]?.Value<bool>() == true) (obj["depth"]?.Value<int>() == 2 ? w.BehindWalls : w.Behind).Add(go);
    }

    // Text saying anything: a copy of one of the level's own sign texts (the
    // zone 2 statue's green line), so it has the game's font, colour and fit.
    private static void SpawnSign(MapWorld w, JObject obj)
    {
        var path = obj["path"]?.Value<string>();
        var source = w.FindSceneObject(path, Vector2.zero) ?? throw new Exception("no sign text at '" + path + "' to copy");
        var scale = source.lossyScale;
        var go = UnityEngine.Object.Instantiate(source.gameObject, w.Point(obj), Quaternion.Euler(0f, 0f, obj["rotation"]?.Value<float>() ?? 0f), w.Root);
        go.name = "Text";
        go.transform.localScale = scale;
        go.SetActive(true);
        foreach (var c in go.GetComponents<MonoBehaviour>())
            if (c != null && c.GetType().Name.StartsWith("Localize")) c.enabled = false;
        var text = go.GetComponent<TMPro.TMP_Text>() ?? throw new Exception("the sign has no text");
        text.text = obj["text"]?.Value<string>() ?? "";
        if (obj["color"] is JArray col && col.Count >= 3) text.color = new Color(col[0].Value<float>(), col[1].Value<float>(), col[2].Value<float>(), 1f);
        else text.color = new Color(text.color.r, text.color.g, text.color.b, 1f);
        if (obj["alpha"] != null) text.alpha = Mathf.Clamp01(obj["alpha"].Value<float>());
        if (go.transform is RectTransform rect && scale.x != 0f && scale.y != 0f)
            rect.sizeDelta = new Vector2((obj["width"]?.Value<float>() ?? rect.sizeDelta.x * scale.x) / scale.x, (obj["height"]?.Value<float>() ?? rect.sizeDelta.y * scale.y) / scale.y);
        MapGroups.Add(w, obj, go);
        if (obj["order"] != null) w.Stacked.Add((obj["order"].Value<int>(), go));
        if (obj["behind"]?.Value<bool>() == true) (obj["depth"]?.Value<int>() == 2 ? w.BehindWalls : w.Behind).Add(go);
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
        if (!string.IsNullOrEmpty(path)) w.ClonesByPath[path] = clone;
        var doorPath = obj["door"]?.Value<string>();
        if (!string.IsNullOrEmpty(doorPath)) w.DoorLinks.Add((clone, doorPath));
        // The editor's layering: among sprites drawn at the same level, nearer the camera is in front.
        if (obj["order"] != null)
        {
            var at = clone.transform.position;
            clone.transform.position = new Vector3(at.x, at.y, at.z - 0.01f * obj["order"].Value<int>());
        }
        clone.SetActive(true);
        MapGroups.Add(w, obj, clone);
        if (obj["order"] != null) w.Stacked.Add((obj["order"].Value<int>(), clone));
        if (obj["behind"]?.Value<bool>() == true) (obj["depth"]?.Value<int>() == 2 ? w.BehindWalls : w.Behind).Add(clone);
        var course = obj["course"]?.Value<string>();
        if (!string.IsNullOrEmpty(course)) w.Links.Add((clone, course));
        if (obj["teleport"] is JObject teleport) w.Teleports.Add((clone, teleport));
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
        // A refresher is several sprites (its wings, its dotted recharging outline): hide all of it.
        var refresher = target.GetComponentInParent<JiggleDropScript>(true);
        if (refresher != null) { w.SetActive(refresher.gameObject, false); return; }
        if (obj["rendererOnly"]?.Value<bool>() != true) { w.SetActive(target, false); return; }
        var sr = target.GetComponent<SpriteRenderer>() ?? throw new Exception("no sprite to hide");
        var shown = sr.enabled;
        sr.enabled = false;
        w.OnUnload(() => { if (sr != null) sr.enabled = shown; });
    }

    // One of the level's own things, moved in the editor: the real object goes along by the same amount.
    private static void Move(MapWorld w, JObject obj)
    {
        var target = LevelObject(w, obj);
        var by = new Vector3(obj["dx"]?.Value<float>() ?? 0f, obj["dy"]?.Value<float>() ?? 0f, 0f);
        target.position += by;
        var t = target;
        w.OnUnload(() => { if (t != null) t.position -= by; });
    }

    // A level thing turned, sized or flipped in the editor, about its own position.
    private static void TransformLevel(MapWorld w, JObject obj)
    {
        var t = LevelObject(w, obj);
        Quaternion rot = t.rotation;
        Vector3 scale = t.localScale;
        if (obj["rotation"] != null) t.rotation = Quaternion.Euler(0f, 0f, obj["rotation"].Value<float>()) * t.rotation;
        if (obj["scale"] is JArray sc && sc.Count == 2) t.localScale = new Vector3(scale.x * sc[0].Value<float>(), scale.y * sc[1].Value<float>(), scale.z);
        w.OnUnload(() => { if (t != null) { t.rotation = rot; t.localScale = scale; } });
    }

    // A level thing moved in or out in the editor's draw order: every sprite of it shifts by the same amount.
    private static void OrderLevel(MapWorld w, JObject obj)
    {
        var delta = obj["delta"]?.Value<int>() ?? 0;
        foreach (var r in LevelObject(w, obj).GetComponentsInChildren<Renderer>(true))
        {
            var rend = r;
            rend.sortingOrder += delta;
            w.OnUnload(() => { if (rend != null) rend.sortingOrder -= delta; });
        }
    }

    // A level thing in one of the map's groups, for group triggers to show, hide or move.
    private static void GroupLevel(MapWorld w, JObject obj)
    {
        var go = LevelObject(w, obj).gameObject;
        var was = go.activeSelf;
        var at = go.transform.position;
        MapGroups.Add(w, obj, go);
        w.OnUnload(() => { if (go != null) { go.SetActive(was); go.transform.position = at; } });
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
