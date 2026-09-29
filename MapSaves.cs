using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Recharge.ModApi;
using UnityEngine;
using UnityEngine.SceneManagement;

// Each map keeps its own save (SavedataMaps/<map id>), so playing a map never
// changes Base Game progress and each map picks up where it was left.
//
// Loading: only ever the game's own way, as a scene starts (see SaveSwap) -
// playing a map always goes through a scene change with its save swapped in.
// Saving: while a map is active the game's Saveloader is switched off (it
// only writes /Savedata) and this saves the same objects to the map's folder,
// on the game's own rule of never mid-run, plus once as a scene change begins.
public static class MapSaves
{
    private const float AutosaveSeconds = 10f;
    private const string MapsRoot = "/SavedataMaps/";
    private const string StartFile = "/navigator-start.json";

    // The map whose save is live in this scene (null: Base Game).
    public static string ActiveMapId { get; private set; }
    // This scene started the active map as a brand-new game.
    public static bool StartedFresh { get; private set; }

    public static string FolderFor(string mapId) => MapsRoot + Safe(mapId);
    public static string ActiveFolder => ActiveMapId != null ? FolderFor(ActiveMapId) : SaveSwap.Game;

    private static string _pendingMapId;
    private static bool _pendingFresh;
    private static bool _realSaveSeen;
    private static bool _savedForSceneChange;

    private static string Safe(string mapId)
    {
        var bad = Path.GetInvalidFileNameChars();
        return new string(mapId.Select(c => bad.Contains(c) || c == '/' || c == '\\' ? '_' : c).ToArray());
    }

    // Mod start: whatever an earlier session left swapped goes back.
    public static void Recover() => SaveSwap.SwapOut();

    // Before a scene change into mapId: saves where we are now, then swaps the
    // map's save (or a new game) in for the next scene to load.
    public static void BeginMapScene(string mapId, JObject player, bool asNewGame = false)
    {
        SaveCurrent();
        StopGameSaving();
        var folder = FolderFor(mapId);
        if (asNewGame) Delete(mapId);
        var fresh = player != null && !MatchesStart(folder, player);
        if (fresh)
        {
            SaveSwap.DeleteFolder(folder);
            SaveSwap.DeleteFolder(folder + "backup");
        }
        else if (player == null && !SaveSwap.HasSave(folder))
        {
            // "Use the player's own save": starts from Base Game progress.
            SaveSwap.CopyFolder(SaveSwap.Game, folder);
            SaveSwap.CopyFolder(SaveSwap.GameBackup, folder + "backup");
        }
        try { SaveSwap.SwapIn(fresh ? null : folder); }
        catch (Exception e)
        {
            Debug.LogError("[RechargeMaps] couldn't swap in the map's save: " + e.Message);
            SaveSwap.SwapOut();
        }
        _pendingMapId = mapId;
        _pendingFresh = fresh;
        Debug.Log("[RechargeMaps] starting '" + mapId + "' " + (fresh ? "as a new game" : "from its save"));
    }

    public static void OnSceneLoaded(Scene scene, MonoBehaviour host)
    {
        host.CancelInvoke(nameof(MapManager.MapAutosave));
        _savedForSceneChange = false;
        // The title scene reloads itself once at startup: the swap waits for the gameplay scene.
        if (_pendingMapId != null && scene.name == "MainMenu")
        {
            StopGameSaving();
            return;
        }
        SaveSwap.SwapOut();
        ActiveMapId = _pendingMapId;
        StartedFresh = _pendingMapId != null && _pendingFresh;
        _pendingMapId = null;
        if (ActiveMapId != null)
        {
            StopGameSaving();
            host.InvokeRepeating(nameof(MapManager.MapAutosave), AutosaveSeconds, AutosaveSeconds);
        }
        else DropWithDeletedSave();
    }

    // LoadMap: the map's own extras on top of the save the scene loaded.
    public static void Enter(string mapId)
    {
        if (ActiveMapId != mapId)
        {
            Debug.LogWarning("[RechargeMaps] '" + mapId + "' loaded without its save scene; progress won't be saved");
            return;
        }
        MapUpgrades.Load(FolderFor(mapId));
    }

    // The map's start settings: a new game's marker, checked on the next play.
    public static void MarkStart(string mapId, JObject player)
    {
        var folder = SaveSwap.Full(FolderFor(mapId));
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(folder + StartFile, MapUpgrades.StartMarker(player));
        }
        catch (Exception e) { Debug.LogWarning("[RechargeMaps] saving the map's start failed: " + e.Message); }
    }

    private static bool MatchesStart(string folder, JObject player)
    {
        if (!SaveSwap.HasSave(folder)) return false;
        try { return File.ReadAllText(SaveSwap.Full(folder) + StartFile) == MapUpgrades.StartMarker(player); }
        catch { return false; }
    }

    public static void Autosave()
    {
        if (ActiveMapId == null) return;
        var player = MapUpgrades.GamePlayer();
        if (player != null && player.courseResetPoint != Vector2.zero) return;
        StopGameSaving();
        SaveAll(FolderFor(ActiveMapId));
    }

    // Every frame: a scene change has started (the pause menu's own save is
    // off while a map is active), so save the map before the scene goes.
    public static void Tick()
    {
        if (ActiveMapId == null || _savedForSceneChange) return;
        foreach (var menu in UnityEngine.Object.FindObjectsByType<pauseMenuScript>(FindObjectsSortMode.None))
        {
            if (!Reflect.GetField<bool>(menu, "changingSceneNow")) continue;
            _savedForSceneChange = true;
            SaveAll(FolderFor(ActiveMapId));
            return;
        }
    }

    public static void Shutdown()
    {
        if (ActiveMapId != null) SaveAll(FolderFor(ActiveMapId));
        SaveSwap.SwapOut();
    }

    // Deletes a map's whole save; the next play starts it over.
    public static void Delete(string mapId)
    {
        SaveSwap.DeleteFolder(FolderFor(mapId));
        SaveSwap.DeleteFolder(FolderFor(mapId) + "backup");
        if (ActiveMapId == mapId) ActiveMapId = null;
    }

    // Where we are now, saved before leaving: the map, or Base Game (its own save).
    private static void SaveCurrent()
    {
        if (SaveSwap.IsSwapped) return;
        if (ActiveMapId != null) { SaveAll(FolderFor(ActiveMapId)); return; }
        if (SceneManager.GetActiveScene().name == "MainMenu") return;
        var loader = UnityEngine.Object.FindFirstObjectByType<Saveloader>();
        if (loader != null) loader.manualSave();
    }

    // The game's Delete Save wipes /Savedata and reloads the scene; a real save
    // that was there and now isn't takes the map saves with it.
    private static void DropWithDeletedSave()
    {
        if (SaveSwap.HasSave(SaveSwap.Game)) { _realSaveSeen = true; return; }
        if (!_realSaveSeen) return;
        _realSaveSeen = false;
        SaveSwap.DeleteFolder(MapsRoot.TrimEnd('/'));
        Debug.Log("[RechargeMaps] Base Game save deleted: map saves cleared too");
    }

    private static void SaveAll(string folder)
    {
        try
        {
            Directory.CreateDirectory(SaveSwap.Full(folder));
            Directory.CreateDirectory(SaveSwap.Full(folder) + "backup");
        }
        catch (Exception e) { Debug.LogWarning("[RechargeMaps] map save folder: " + e.Message); return; }
        MapUpgrades.Save(folder);
        foreach (var obj in UnityEngine.Object.FindObjectsByType<SaveableObject>(FindObjectsSortMode.None))
        {
            // The title scene can stay loaded beside the world with its own copies.
            if (obj.gameObject.scene.name == "MainMenu") continue;
            try { obj.save(folder); }
            catch (Exception e) { Debug.LogWarning("[RechargeMaps] saving " + obj.GetType().Name + " failed: " + e.Message); }
        }
    }

    // Saveloader's own saving: IsSaving gates manualSave, and autosave runs on an Invoke.
    private static void StopGameSaving()
    {
        foreach (var loader in UnityEngine.Object.FindObjectsByType<Saveloader>(FindObjectsSortMode.None))
        {
            Reflect.TrySetField(loader, "IsSaving", false);
            loader.CancelInvoke("autosave");
        }
    }
}
