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
    // The player chose "Base Game" / "B-side" from the map menu: the scene change
    // that follows carries no map, and that is the only thing that should end the
    // map. Entering a map reloads the gameplay scene twice, and that second load
    // carries no pending map either - so "no pending map" alone can't be read as
    // "left the map". Acting on it dropped the map mid-play, and the game carried
    // on with Base Game progress and no save of its own.
    public static void LeaveMap() { _leavingForBaseGame = true; ReleaseSave(); }
    private static bool _realSaveSeen;
    private static bool _savedForSceneChange;
    private static bool _leavingForBaseGame;

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
        _menus = null;
        // Which folder this load's Saveloader (it runs in Awake, before this event) read.
        Debug.Log("[RechargeMaps] scene '" + scene.name + "' loaded; the game read " + (SaveSwap.IsSwapped
            ? "the swapped-in copy of " + (ActiveMapId != null ? FolderFor(ActiveMapId) : _pendingMapId != null ? FolderFor(_pendingMapId) : "a map save")
            : "the REAL Base Game save") + " (" + SaveSwap.Describe(SaveSwap.Game) + ")");
        // The title scene reloads itself once at startup: the swap waits for the gameplay scene.
        if (_pendingMapId != null && scene.name == "MainMenu")
        {
            StopGameSaving();
            return;
        }
        // A scene load with nothing pending is the gameplay scene loading a second
        // time on the way into a map. Leave the map alone - it is still the one
        // being set up, and dropping it here is what left the game running on Base
        // Game progress with nowhere to save.
        if (_pendingMapId == null && !_leavingForBaseGame)
        {
            if (ActiveMapId != null)
            {
                StopGameSaving();
                // Still swapped in (the map isn't built yet): this load's Saveloader read the
                // map's own save, nothing to fix. Otherwise it read the real Base Game save.
                if (!SaveSwap.IsSwapped) ReloadActiveSave();
            }
            return;
        }
        ActiveMapId = _pendingMapId;
        StartedFresh = _pendingMapId != null && _pendingFresh;
        _pendingMapId = null;
        _leavingForBaseGame = false;
        if (ActiveMapId != null)
        {
            // Entering a map: the map's save stays swapped in for every gameplay-scene load
            // until the map is built (ReleaseSave), so the game's own Saveloader reads the
            // map's save - or an empty one for a new game - on each of them.
            StopGameSaving();
            host.InvokeRepeating(nameof(MapManager.MapAutosave), AutosaveSeconds, AutosaveSeconds);
        }
        else
        {
            SaveSwap.SwapOut();
            DropWithDeletedSave();
        }
    }

    // The map is built and running: the real Base Game save goes back. Until now the map's
    // save has been the one the game loads, whichever scene load that was.
    public static void ReleaseSave()
    {
        if (SaveSwap.IsSwapped) SaveSwap.SwapOut();
    }

    // Leaving a map by the door transition: the map is saved now, while its player is still
    // active (the scene-change check in Tick would find the player switched off).
    public static void SaveForSceneChange()
    {
        if (ActiveMapId == null || _savedForSceneChange) return;
        _savedForSceneChange = true;
        SaveAll(FolderFor(ActiveMapId));
    }

    // The scene is going: no game autosave of a player who has been switched off.
    public static void HoldGameSaving() => StopGameSaving();

    // Safety net: a gameplay scene loaded with the real save in place while a map is active
    // (a stray load after the map was released). Everything the game's Saveloader restores per
    // save and that is safe to load twice - the player and the level's courses - comes from the
    // map's folder again; a course the map has no file for starts fresh.
    private static void ReloadActiveSave()
    {
        var folder = FolderFor(ActiveMapId);
        Debug.LogWarning("[RechargeMaps] '" + ActiveMapId + "': a scene loaded with the real save in place; reloading the player and level courses from '" + folder + "'");
        var movement = MapUpgrades.GamePlayer();
        if (movement == null) return;
        if (SaveSwap.HasSave(folder))
        {
            try { movement.load(folder); }
            catch (Exception e) { Debug.LogWarning("[RechargeMaps] reloading '" + ActiveMapId + "' player: " + e.Message); }
        }
        else
        {
            try { MapUpgrades.ApplyStart(ActiveMapId, MapDefinition.Read(ActiveMapId)?.Player); }
            catch (Exception e) { Debug.LogWarning("[RechargeMaps] re-applying '" + ActiveMapId + "' start: " + e.Message); }
        }
        foreach (var course in LevelCourses())
        {
            var file = SaveSwap.Full(folder) + "/course" + course.courseNumber + "data.txt";
            try
            {
                if (!File.Exists(file)) throw new FileNotFoundException("no save for the course");
                course.load(folder);
            }
            catch { MapCourses.StartFresh(course); }
        }
        LogLevelCourses(folder);
    }

    // The level's own courses (not the map's): they are live in an overlay map.
    private static System.Collections.Generic.List<courseScript> LevelCourses()
    {
        var list = new System.Collections.Generic.List<courseScript>();
        foreach (var c in UnityEngine.Object.FindObjectsByType<courseScript>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (c != null && c.gameObject.scene.IsValid() && c.gameObject.scene.name != "MainMenu" && !MapWorld.IsMapObject(c.transform))
                list.Add(c);
        return list;
    }

    // One line per level course: did it come from the map's save or start fresh, and its clone count.
    public static void LogLevelCourses(string folder)
    {
        foreach (var c in LevelCourses())
        {
            var has = File.Exists(SaveSwap.Full(folder) + "/course" + c.courseNumber + "data.txt");
            var clones = Reflect.TryGetField<clonesScript>(c, "clones") ?? c.GetComponentInChildren<clonesScript>(true);
            Debug.Log("[RechargeMaps] level course " + c.courseNumber + ": " + (has ? "from the map save" : "fresh (no file in the map save)") +
                      ", clones=" + (clones != null ? clones.cloneCount : -1) + ", reward=" + c.reward);
        }
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
        LogLevelCourses(FolderFor(mapId));
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
        if (ActiveMapId == null || SaveSwap.IsSwapped) return;
        var player = MapUpgrades.GamePlayer();
        if (player != null && player.courseResetPoint != Vector2.zero) return;
        StopGameSaving();
        SaveAll(FolderFor(ActiveMapId));
    }

    // Every frame: a scene change has started (the pause menu's own save is
    // off while a map is active), so save the map before the scene goes.
    // The pause menus are found once a scene; the change-scene animation runs
    // for a good second, so checking a few times a second is plenty.
    private static pauseMenuScript[] _menus;
    private static float _nextCheck;

    public static void Tick()
    {
        if (ActiveMapId == null || _savedForSceneChange || Time.unscaledTime < _nextCheck) return;
        _nextCheck = Time.unscaledTime + 0.2f;
        if (_menus == null || Array.Exists(_menus, m => m == null)) _menus = UnityEngine.Object.FindObjectsByType<pauseMenuScript>(FindObjectsSortMode.None);
        foreach (var menu in _menus)
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
        // The map isn't built yet (its save is still swapped in): the scene holds the load, not the map.
        if (SaveSwap.IsSwapped) return;
        try
        {
            Directory.CreateDirectory(SaveSwap.Full(folder));
            Directory.CreateDirectory(SaveSwap.Full(folder) + "backup");
        }
        catch (Exception e) { Debug.LogWarning("[RechargeMaps] map save folder: " + e.Message); return; }
        MapUpgrades.Save(folder);
        var saved = new System.Collections.Generic.List<string>();
        foreach (var obj in UnityEngine.Object.FindObjectsByType<SaveableObject>(FindObjectsSortMode.None))
        {
            // The title scene can stay loaded beside the world with its own copies.
            if (obj.gameObject.scene.name == "MainMenu") continue;
            try { obj.save(folder); saved.Add(obj.GetType().Name); }
            catch (Exception e) { Debug.LogWarning("[RechargeMaps] saving " + obj.GetType().Name + " failed: " + e.Message); }
        }
        // Movement writes playerdata.txt, and HasSave() looks for exactly that file: without it
        // the map reads as never saved, so every play starts over and the folder is deleted.
        // Nothing logged when it went missing, so say so loudly instead of losing a save quietly.
        bool wrotePlayer = File.Exists(SaveSwap.Full(folder) + "/playerdata.txt");
        if (!wrotePlayer)
            Debug.LogError("[RechargeMaps] NO playerdata.txt in '" + folder + "' after saving [" + string.Join(", ", saved) +
                           "] - this map's save will be treated as new next time");
    }

    // Saveloader's own saving: IsSaving gates manualSave, but its autosave() never checks
    // IsSaving - it only tests courseResetPoint - so the repeating invoke is the only thing
    // keeping it out of a map. Cancelling by name wasn't stopping it, and one stray
    // "Autosaving" writes the whole map's state over the Base Game save. Cancel everything
    // on the component instead, and keep IsSaving false so manualSave stays a no-op too.
    private static void StopGameSaving()
    {
        foreach (var loader in UnityEngine.Object.FindObjectsByType<Saveloader>(FindObjectsSortMode.None))
        {
            Reflect.TrySetField(loader, "IsSaving", false);
            loader.CancelInvoke();
        }
    }
}
