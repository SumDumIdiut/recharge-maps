using System;
using System.IO;
using System.Linq;
using Recharge.ModApi;
using UnityEngine;

// Each map keeps its own save folder (SavedataMaps/<map id>): while a map is
// loaded, everything the game would save - player, courses, breaker, tree,
// timer - goes there instead of the real save, so playing (or editing) a map
// never changes Base Game progress, and each map picks up where it was left.
//
// The game saves only through Saveloader (a 10 s autosave, and manualSave
// from menus and course ends), always to the real folder. While a map's save
// is active that's switched off and this saves to the map's folder instead;
// the next scene load brings a fresh Saveloader that loads the real save again.
public static class MapSaves
{
    private const float AutosaveSeconds = 10f;

    public static string ActiveMapId { get; private set; }

    public static string RealFolder => "/Savedata" + (globalStats.difficultyLevel == 1 ? "hard" : "");

    // Where saves go right now: the loaded map's folder, or the real one.
    public static string ActiveFolder => ActiveMapId != null ? FolderFor(ActiveMapId) : RealFolder;

    // Beside the real save, not in it: the game's Delete Save wipes that whole folder.
    public static string FolderFor(string mapId) => "/SavedataMaps" + (globalStats.difficultyLevel == 1 ? "hard" : "") + "/" + Safe(mapId);

    private static string Safe(string mapId)
    {
        var bad = Path.GetInvalidFileNameChars();
        return new string(mapId.Select(c => bad.Contains(c) || c == '/' || c == '\\' ? '_' : c).ToArray());
    }

    private static string Full(string folder) => Application.persistentDataPath + folder;

    // Switches saving to mapId's folder and loads it into the scene - the first
    // time, the folder starts as a copy of the current (real) progress.
    public static void Enter(string mapId, MonoBehaviour host)
    {
        if (ActiveMapId == mapId) return;
        if (ActiveMapId != null) SaveAll(FolderFor(ActiveMapId));
        else if (!SceneIsFresh) SaveAll(RealFolder); // keep Base Game progress up to the moment the map starts

        var folder = FolderFor(mapId);
        ActiveMapId = mapId;
        SetGameSaving(false);
        try
        {
            if (File.Exists(Full(folder) + "/playerdata.txt")) LoadAll(folder);
            else
            {
                Directory.CreateDirectory(Full(folder));
                SaveAll(folder);
            }
        }
        catch (Exception e) { Debug.LogWarning("[RechargeMaps] map save load failed: " + e.Message); }
        MapUpgrades.Load(folder);

        host.CancelInvoke(nameof(MapManager.MapAutosave));
        host.InvokeRepeating(nameof(MapManager.MapAutosave), AutosaveSeconds, AutosaveSeconds);
    }

    // A map with its own starting settings begins as a brand-new game: the
    // real save's playerdata.txt is set aside while the gameplay scene starts,
    // so the game's Saveloader finds no save and builds a new-game state, then
    // it's put straight back. Nothing is saved to the real folder meanwhile.
    private const string HoldSuffix = ".navigator-hold";
    public static bool SceneIsFresh { get; private set; }
    private static bool _holding;

    public static bool NeedsFreshStart(string mapId, Newtonsoft.Json.Linq.JObject player)
    {
        if (player == null) return false;
        var folder = Full(FolderFor(mapId));
        if (!File.Exists(folder + "/playerdata.txt")) return true;
        try { return !File.Exists(folder + "/navigator-start.json") || File.ReadAllText(folder + "/navigator-start.json") != player.ToString(Newtonsoft.Json.Formatting.None); }
        catch { return true; }
    }

    public static void PrepareFreshStart(string mapId)
    {
        DeleteFolder(mapId);
        var real = Full(RealFolder) + "/playerdata.txt";
        try
        {
            if (File.Exists(real))
            {
                if (File.Exists(real + HoldSuffix)) File.Delete(real + HoldSuffix);
                File.Move(real, real + HoldSuffix);
            }
            _holding = true;
        }
        catch (Exception e) { Debug.LogWarning("[RechargeMaps] couldn't set the real save aside: " + e.Message); }
    }

    // Called on every scene load, at startup and at quit: the real save is always put back.
    public static void RestoreRealSave(bool sceneJustLoaded)
    {
        SceneIsFresh = sceneJustLoaded && _holding;
        _holding = false;
        foreach (var suffix in new[] { "", "hard" })
        {
            var real = Full("/Savedata" + suffix) + "/playerdata.txt";
            try
            {
                if (!File.Exists(real + HoldSuffix)) continue;
                File.Copy(real + HoldSuffix, real, true);
                File.Delete(real + HoldSuffix);
            }
            catch (Exception e) { Debug.LogError("[RechargeMaps] couldn't put the real save back (" + real + HoldSuffix + "): " + e.Message); }
        }
        if (SceneIsFresh) SetGameSaving(false);
    }

    public static void DeleteFolder(string mapId)
    {
        var path = Full(FolderFor(mapId));
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
            if (Directory.Exists(path + "backup")) Directory.Delete(path + "backup", true);
        }
        catch (Exception e) { Debug.LogWarning("[RechargeMaps] clearing map save failed: " + e.Message); }
    }

    // The scene is being replaced (a new Saveloader loads the real save): stop.
    public static void Forget(MonoBehaviour host)
    {
        ActiveMapId = null;
        host.CancelInvoke(nameof(MapManager.MapAutosave));
    }

    // The game's autosave rule: not while a course run is in progress.
    public static void Autosave()
    {
        if (ActiveMapId == null) return;
        var player = UnityEngine.Object.FindFirstObjectByType<Movement>();
        if (player != null && player.courseResetPoint != Vector2.zero) return;
        SetGameSaving(false); // a Saveloader that woke up since would save to the real folder
        SaveAll(FolderFor(ActiveMapId));
    }

    public static void SaveNow()
    {
        if (ActiveMapId != null) SaveAll(FolderFor(ActiveMapId));
    }

    // Deletes a map's whole save: its progress starts again from Base Game's.
    // If it's the map being played, the scene goes back to the real progress
    // now and the map's folder starts over from that.
    public static void Delete(string mapId)
    {
        var path = Full(FolderFor(mapId));
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch (Exception e) { Debug.LogWarning("[RechargeMaps] delete map save failed: " + e.Message); }
        if (ActiveMapId != mapId) return;
        MapUpgrades.Reset();
        LoadAll(RealFolder);
        SaveAll(FolderFor(mapId));
    }

    // The game's Delete Save wipes a slot's real folder (Savedata or
    // Savedatahard) and reloads the scene; on that reload, a slot with no real
    // save left loses its map saves too, so deleting a save deletes all of it.
    public static void DropWithDeletedSaves()
    {
        foreach (var suffix in new[] { "", "hard" })
        {
            if (File.Exists(Full("/Savedata" + suffix) + "/playerdata.txt")) continue;
            var maps = Full("/SavedataMaps" + suffix);
            try { if (Directory.Exists(maps)) Directory.Delete(maps, true); }
            catch (Exception e) { Debug.LogWarning("[RechargeMaps] clearing map saves failed: " + e.Message); }
        }
    }

    private static void SaveAll(string folder)
    {
        Directory.CreateDirectory(Full(folder));
        Directory.CreateDirectory(Full(folder) + "backup");
        if (folder != RealFolder) MapUpgrades.Save(folder);
        foreach (var obj in UnityEngine.Object.FindObjectsByType<SaveableObject>(FindObjectsSortMode.None))
        {
            try { obj.save(folder); }
            catch (Exception e) { Debug.LogWarning("[RechargeMaps] saving " + obj.GetType().Name + " failed: " + e.Message); }
        }
    }

    private static void LoadAll(string folder)
    {
        foreach (var obj in UnityEngine.Object.FindObjectsByType<SaveableObject>(FindObjectsSortMode.None))
        {
            try { obj.load(folder); }
            catch (Exception e) { Debug.LogWarning("[RechargeMaps] loading " + obj.GetType().Name + " failed: " + e.Message); }
        }
    }

    // Saveloader's own saving: IsSaving gates manualSave, and autosave runs on an Invoke.
    private static void SetGameSaving(bool on)
    {
        var loader = UnityEngine.Object.FindFirstObjectByType<Saveloader>();
        if (loader == null) return;
        Reflect.TrySetField(loader, "IsSaving", on);
        loader.CancelInvoke("autosave");
        if (on) loader.InvokeRepeating("autosave", AutosaveSeconds, AutosaveSeconds);
    }
}
