using System;
using System.IO;
using Recharge.ModApi;
using UnityEngine;

public class RechargeMapsMod : IRechargeMod
{
    public string Id => "recharge.maps";
    public string DisplayName => "Navigator";
    public Version Version => new Version(1, 0, 0);

    public void OnLoad(IRechargeHost host)
    {
        MapManager.GetOrCreate();
        MapMenuBuilder.Install(host.PauseMenu);

        // host.PauseMenu is a one-time snapshot of whichever pauseMenuScript
        // instance existed when the loader first initialized (RechargeLoaderBootstrap
        // only ever runs its load sequence once). Every later scene load - e.g.
        // returning to the main menu after playing - creates a brand new
        // pauseMenuScript with an undecorated mainBitPublic, so re-install
        // against whatever instance is actually live each time a scene loads.
        PauseMenuHelper.OnMenuReady(host, MapMenuBuilder.Install);

        host.Events.On("recharge.maps.load_requested", payload =>
        {
            var mapId = payload as string;
            if (string.IsNullOrEmpty(mapId)) return;
            var menu = PauseMenuHelper.FindMenu();
            if (menu != null) MapManager.Instance.PlayMap(mapId, menu);
        });

        PlayRequestedTestMap();
    }

    // The Recharge map maker's "Test in game" writes the map, then this file
    // naming it, then launches the game - so the first load (the title screen)
    // goes straight into that map, from a fresh save each time.
    private static void PlayRequestedTestMap()
    {
        var request = Path.Combine(MapPaths.ModsRoot, "recharge.maps", "autoplay.txt");
        if (!File.Exists(request)) return;
        string mapId;
        try
        {
            mapId = File.ReadAllText(request).Trim();
            File.Delete(request);
        }
        catch (Exception e)
        {
            Debug.LogWarning("[RechargeMaps] couldn't read the map maker's test request: " + e.Message);
            return;
        }
        if (mapId.Length == 0) return;

        MapManager.DeleteMapSave(mapId);
        var menu = PauseMenuHelper.FindMenu();
        // The title screen already has a Player, but starting the game reloads
        // the scene and would wipe a map spawned now - so always start the
        // game first and load the map once gameplay's Player exists.
        if (menu != null) MapManager.Instance.PlayMapAfterSceneChange(mapId, menu);
        else Debug.LogWarning("[RechargeMaps] no menu to start test map '" + mapId + "' from");
    }

    public void OnUnload() { }
}
