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
    // Custom maps: the editor's level-space point their coordinates are measured from.
    [JsonProperty("levelOrigin")] public float[] LevelOrigin;

    public static MapDefinition Read(string mapId)
    {
        var path = Path.Combine(MapPaths.MapsDir, mapId, "map.json");
        try { return JsonConvert.DeserializeObject<MapDefinition>(File.ReadAllText(path)); }
        catch (Exception e) { Debug.LogError("[RechargeMaps] couldn't read " + path + ": " + e.Message); return null; }
    }
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
