using System.Collections;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering.Universal;

// A map's copy of the level's fake-credits screen, played the way the game does it
// minus what reaches outside the map: no achievements or saved settings, and the
// V-man ending gives the player back instead of crashing the game. Mode: "game"
// (fake credits, V-man's ending for V-man), "fake" (always the fake credits) or
// "vman" (V-man's ending, only for V-man).
internal class MapFakeCredits : MonoBehaviour
{
    public TMPro.TMP_Text[] EndingMessage;
    public TMPro.TMP_Text[] VmanEndingMessage;
    public Light2D Light;
    public GameObject RealCredits;
    public string Mode = "game";
    private bool _active;

    // Takes over from the game's own script on a copy (before the copy wakes up).
    public static void Replace(GameObject clone, string mode)
    {
        var game = clone.GetComponentInChildren<FakeCreditsControlScript>(true);
        if (game == null) return;
        const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        T Get<T>(string field) where T : class => typeof(FakeCreditsControlScript).GetField(field, any)?.GetValue(game) as T;
        var host = game.gameObject;
        var credits = host.AddComponent<MapFakeCredits>();
        credits.EndingMessage = Get<TMPro.TMP_Text[]>("EndingMessage") ?? new TMPro.TMP_Text[0];
        credits.VmanEndingMessage = Get<TMPro.TMP_Text[]>("VmanEndingMessage") ?? new TMPro.TMP_Text[0];
        credits.Light = Get<Light2D>("Light");
        credits.RealCredits = Get<GameObject>("realCredits");
        credits.Mode = mode ?? "game";
        DestroyImmediate(game);
    }

    private void Start()
    {
        foreach (var m in EndingMessage) Hide(m);
        foreach (var m in VmanEndingMessage) Hide(m);
    }

    private static void Hide(TMPro.TMP_Text m)
    {
        if (m == null) return;
        m.gameObject.SetActive(false);
        m.alpha = 0f;
    }

    private void OnTriggerEnter2D(Collider2D collision)
    {
        var mv = collision.GetComponent<Movement>();
        if (_active || mv == null) return;
        var vman = VmanScript.isCurrentlyVman;
        if (Mode == "vman" ? vman : Mode == "game" && vman) { _active = true; StartCoroutine(Vman(mv)); }
        else if (Mode != "vman") { _active = true; StartCoroutine(Fake()); }
    }

    private IEnumerator FadeIn(TMPro.TMP_Text[] messages)
    {
        foreach (var m in messages)
        {
            if (m == null) continue;
            m.gameObject.SetActive(true);
            m.alpha = 0f;
            while (m.alpha < 1f) { m.alpha += 0.018f; yield return new WaitForFixedUpdate(); }
            m.alpha = 1f;
            yield return new WaitForSeconds(1.2f);
        }
    }

    private IEnumerator FadeOut(TMPro.TMP_Text[] messages)
    {
        for (float a = 1f; a > 0f; a -= 0.02f)
        {
            foreach (var m in messages) if (m != null) m.alpha = a;
            yield return new WaitForFixedUpdate();
        }
        foreach (var m in messages) Hide(m);
    }

    private IEnumerator Fake()
    {
        yield return FadeIn(EndingMessage);
        yield return new WaitForSeconds(1.2f);
        yield return FadeOut(EndingMessage);
        if (RealCredits != null) RealCredits.SetActive(true);
    }

    private IEnumerator Vman(Movement mv)
    {
        Singleton<MusicPlayer>.Instance?.playNewMusic(MusicPlayer.AUDIO_ID.none, 2f);
        mv.cutsceneMode = Movement.cutsceneModes.longfall;
        yield return FadeIn(VmanEndingMessage);
        var flicker = Light != null ? Light.GetComponent<LightFlicker>() : null;
        float minIntensity = 0f, minRadius = 0f;
        if (flicker != null)
        {
            minIntensity = flicker.minIntensity;
            minRadius = flicker.minRadius;
            for (int i = 0; i < 400 && Light.intensity < 1300f; i++)
            {
                flicker.minIntensity *= 1.04f;
                flicker.minRadius *= 1.04f;
                yield return new WaitForFixedUpdate();
            }
        }
        yield return new WaitForSeconds(0.8f);
        // Where the game crashes itself: the light settles and the player goes on.
        if (flicker != null) { flicker.minIntensity = minIntensity; flicker.minRadius = minRadius; }
        yield return FadeOut(VmanEndingMessage);
        if (mv != null) mv.cutsceneMode = Movement.cutsceneModes.none;
        _active = false;
    }
}
