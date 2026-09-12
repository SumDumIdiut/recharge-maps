using System;
using UnityEngine;

// Author-configured completion reward, added alongside a cloned real endGate.
// Deliberately bypasses courseScript.stopTracking's built-in tier-multiplier
// reward calc (see project_igtap_map_maker_mod memory - user decision: reward
// currency + amount are author-configurable, not automatic).
internal class MapRewardTrigger : MonoBehaviour
{
    public globalStats.Currencies Currency = globalStats.Currencies.Cash;
    public double Amount;
    private bool _fired;

    private void OnTriggerEnter2D(Collider2D collision)
    {
        if (_fired || !collision.gameObject.CompareTag("Player")) return;
        if (Amount <= 0) return;
        _fired = true;
        globalStats.currencyLookup[Currency] += Amount;
        Debug.Log("[RechargeMaps] granted " + Amount + " " + Currency + " on map completion");
    }
}
