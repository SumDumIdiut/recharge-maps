using System;
using Recharge.ModApi;

public class RechargeMapsMod : IRechargeMod
{
    public string Id => "recharge.maps";
    public string DisplayName => "Maps";
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
        host.Events.On(RechargeEvents.SceneLoaded, _ =>
        {
            var menu = UnityEngine.Object.FindFirstObjectByType<pauseMenuScript>();
            if (menu != null) MapMenuBuilder.Install(menu);
        });

        host.Events.On("recharge.maps.load_requested", payload =>
        {
            var mapId = payload as string;
            if (string.IsNullOrEmpty(mapId)) return;
            var menu = UnityEngine.Object.FindFirstObjectByType<pauseMenuScript>();
            if (menu != null) MapManager.Instance.PlayMap(mapId, menu);
        });
    }

    public void OnUnload() { }
}
