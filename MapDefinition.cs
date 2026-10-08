using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

internal class MapDefinition
{
    [JsonProperty("formatVersion")] public int FormatVersion = 1;
    [JsonProperty("name")] public string Name = "";
    [JsonProperty("description")] public string Description = "";
    [JsonProperty("images")] public List<string> Images = new List<string>();
    [JsonProperty("groups")] public List<MapGroup> Groups = new List<MapGroup>();
    [JsonProperty("customImages")] public List<MapCustomImage> CustomImages = new List<MapCustomImage>();
    // Overlay maps are edits to the real base-game world at its own coordinates
    // (made on top of the imported base map); others are self-contained and
    // spawn in the far-away pocket. baseState picks area 1's look for overlays:
    // "start" (start of the game) or "overgrown" (after the breaker trips).
    [JsonProperty("overlay")] public bool Overlay;
    [JsonProperty("baseState")] public string BaseState;
    [JsonProperty("player")] public Newtonsoft.Json.Linq.JObject Player;
    [JsonProperty("cameraSize")] public float? CameraSize;
    // The map's music ("level", "none", "game:<track>", "asset:<file>") and
    // background (null / "level", or { image, parallax, scale }); see MapMedia.
    [JsonProperty("music")] public Newtonsoft.Json.Linq.JToken Music;
    [JsonProperty("background")] public Newtonsoft.Json.Linq.JToken Background;
    // Edits in the overgrown stages are kept per area-1 state: the game's own state picks them.
    [JsonProperty("stages")] public bool Stages;
    // Custom maps: the editor's level-space point their coordinates are measured from.
    [JsonProperty("levelOrigin")] public float[] LevelOrigin;
    // Groups (see MapGroups) that start switched off until a trigger shows them.
    [JsonProperty("hiddenGroups")] public List<string> HiddenGroups;
    // Rebuilt maps: every tile, per tilemap as runs of cells on its own grid, and the matrices they use.
    [JsonProperty("tileLayers")] public List<MapTileLayer> TileLayers;
    [JsonProperty("mats")] public List<float[]> Mats;
    // Rebuilt maps: the level area loaded when the map starts (its background, lights,
    // courses' screens) and area 1's state ("start" / "overgrown").
    [JsonProperty("startZone")] public int StartZone;
    [JsonProperty("areaState")] public string AreaState;

    public static MapDefinition Read(string mapId)
    {
        var path = Path.Combine(MapPaths.MapsDir, mapId, "map.json");
        try { return JsonConvert.DeserializeObject<MapDefinition>(File.ReadAllText(path)); }
        catch (Exception e) { Debug.LogError("[RechargeMaps] couldn't read " + path + ": " + e.Message); return null; }
    }

    // Just the map's name, read up to its top-level "name" (a whole level is megabytes).
    // A map from the Hub goes by its Hub name (hub.json, written by the app beside it): two uploads
    // of one map can share the name in their files. Others by the name in map.json.
    public static string ReadName(string mapId)
    {
        var hub = Path.Combine(MapPaths.MapsDir, mapId, "hub.json");
        try
        {
            if (File.Exists(hub))
            {
                var hubName = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(hub))["name"]?.ToString();
                if (!string.IsNullOrWhiteSpace(hubName)) return hubName;
            }
        }
        catch (Exception e) { Debug.LogWarning("[RechargeMaps] couldn't read " + hub + ": " + e.Message); }
        var path = Path.Combine(MapPaths.MapsDir, mapId, "map.json");
        try
        {
            using (var reader = new JsonTextReader(new StreamReader(path)))
                while (reader.Read())
                {
                    if (reader.Depth == 1 && reader.TokenType == JsonToken.PropertyName && (string)reader.Value == "name")
                        return reader.ReadAsString();
                    if (reader.Depth == 1 && reader.TokenType == JsonToken.PropertyName) { reader.Read(); reader.Skip(); }
                }
        }
        catch (Exception e) { Debug.LogWarning("[RechargeMaps] couldn't read the name in " + path + ": " + e.Message); }
        return null;
    }
}

// Runs [y, x, n, name index, matrix index], five numbers each, left to right along a row.
internal class MapTileLayer
{
    [JsonProperty("tilemap")] public string Tilemap;
    [JsonProperty("names")] public List<string> Names;
    [JsonProperty("runs")] public int[] Runs;
}

internal class MapCustomImage
{
    [JsonProperty("assetId")] public string AssetId;
    [JsonProperty("path")] public string Path;
}

internal class MapGroup
{
    [JsonProperty("startX")] public float StartX;
    [JsonProperty("startY")] public float StartY;
    [JsonProperty("endX")] public float EndX;
    [JsonProperty("endY")] public float EndY;
    // Optional: where the player spawns (defaults to the start gate), and
    // whether the map has start/end gates at all - a free-play map has none.
    [JsonProperty("spawnX")] public float? SpawnX;
    [JsonProperty("spawnY")] public float? SpawnY;
    [JsonProperty("gates")] public bool Gates = true;
    [JsonProperty("keepSpawn")] public bool KeepSpawn;
    [JsonProperty("reward")] public MapReward Reward;
    [JsonProperty("objects")] public List<Newtonsoft.Json.Linq.JObject> Objects = new List<Newtonsoft.Json.Linq.JObject>();
    [JsonProperty("courses")] public List<MapCourse> Courses;
    // More spawns, cycled with Q / E in game (after the main one).
    [JsonProperty("spawns")] public List<MapPoint> Spawns;
    // What arriving at the main spawn does to the area: { zone, music, background } (see MapMedia.ApplyArea).
    [JsonProperty("spawnArea")] public Newtonsoft.Json.Linq.JObject SpawnArea;
}

internal class MapPoint
{
    [JsonProperty("x")] public float X;
    [JsonProperty("y")] public float Y;
    // A spawn's area: loaded, with its music and background, when the player goes there.
    [JsonProperty("area")] public Newtonsoft.Json.Linq.JObject Area;
}

internal class MapCourse
{
    [JsonProperty("id")] public string Id;
    [JsonProperty("startX")] public float StartX;
    [JsonProperty("startY")] public float StartY;
    [JsonProperty("endX")] public float EndX;
    [JsonProperty("endY")] public float EndY;
    // Where the course's screen (reward, best time, clones) sits: the centre of its board.
    [JsonProperty("screenX")] public float? ScreenX;
    [JsonProperty("screenY")] public float? ScreenY;
    [JsonProperty("reward")] public MapReward Reward;
    // Timer resets: gates that end a run without finishing it. For a level copy,
    // null keeps the course's own.
    [JsonProperty("resets")] public List<MapResetGate> Resets;
    // The screen's place in the editor's stack (and behind the level / the walls too).
    [JsonProperty("orderDelta")] public int OrderDelta;
    [JsonProperty("order")] public int? Order;
    [JsonProperty("behind")] public bool Behind;
    [JsonProperty("depth")] public int Depth;
    // One of the level's own courses, copied whole: { path, srcX, srcY, x, y, cut, toggles,
    // startX, startY, endX, endY, screenX, screenY } - where its gates and screen were.
    [JsonProperty("level")] public Newtonsoft.Json.Linq.JObject Level;
}

internal class MapResetGate
{
    [JsonProperty("x")] public float X;
    [JsonProperty("y")] public float Y;
    // Its trigger, centred (dx, dy) from the gate.
    [JsonProperty("w")] public float W = 20f;
    [JsonProperty("h")] public float H = 400f;
    [JsonProperty("dx")] public float Dx;
    [JsonProperty("dy")] public float Dy = 150f;
}

internal class MapReward
{
    [JsonProperty("currency")] public string Currency = "Cash";
    [JsonProperty("amount")] public double Amount;
}

internal class PlatformPosition
{
    [JsonProperty("x")] public float X;
    [JsonProperty("y")] public float Y;
    [JsonProperty("timeToReachFromPrevious")] public float TimeToReachFromPrevious;
    [JsonProperty("tween")] public string Tween = "linear";
    [JsonProperty("autoStartNextPhase")] public bool AutoStartNextPhase;
    [JsonProperty("nextPhaseOnEnter")] public bool NextPhaseOnEnter;
    [JsonProperty("nextPhaseOnExit")] public bool NextPhaseOnExit;
    [JsonProperty("waitOnPhaseEnd")] public float WaitOnPhaseEnd;
}
