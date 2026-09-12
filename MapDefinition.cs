using System.Collections.Generic;
using Newtonsoft.Json;

internal class MapDefinition
{
    [JsonProperty("formatVersion")] public int FormatVersion = 1;
    [JsonProperty("name")] public string Name = "";
    [JsonProperty("description")] public string Description = "";
    [JsonProperty("images")] public List<string> Images = new List<string>();
    [JsonProperty("groups")] public List<MapGroup> Groups = new List<MapGroup>();
    [JsonProperty("customImages")] public List<MapCustomImage> CustomImages = new List<MapCustomImage>();
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
    [JsonProperty("reward")] public MapReward Reward;
    [JsonProperty("objects")] public List<Newtonsoft.Json.Linq.JObject> Objects = new List<Newtonsoft.Json.Linq.JObject>();
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
