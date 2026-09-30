using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json.Linq;
using Recharge.ModApi;
using UnityEngine;
using UnityEngine.Networking;

// A map's music and background: its own choice when it loads, and trigger
// zones that switch either when the player walks in.
//
// Music choices: "level" (the level's own), "none", "game:<track>" (one of
// the game's MusicPlayer tracks) or "asset:<file>" (a file in the map's
// assets folder), crossfaded through the game's own music players.
// Background: null / "level" (the level's own) or { image, parallax, scale }.
internal static class MapMedia
{
    private static MapWorld _world;
    private static MonoBehaviour _host;
    private static readonly Dictionary<string, AudioClip> Clips = new Dictionary<string, AudioClip>();
    private static readonly Dictionary<string, Sprite> Images = new Dictionary<string, Sprite>();
    private static GameObject _background;
    private static string _backgroundKey;

    public static void Start(MapWorld w, MonoBehaviour host)
    {
        _world = w;
        _host = host;
        _background = null;
        _backgroundKey = null;
        var music = w.Def.Music;
        if (music != null && music.Type == JTokenType.String && music.Value<string>() != "level")
        {
            SilenceLevelMusicZones(w);
            PlayMusic(music);
        }
        ShowBackground(w.Def.Background);
    }

    // The level's own music zones would switch the map's music back.
    private static void SilenceLevelMusicZones(MapWorld w)
    {
        foreach (var zone in Resources.FindObjectsOfTypeAll<MusicController>())
        {
            if (zone == null || !zone.gameObject.scene.IsValid()) continue;
            foreach (var col in zone.GetComponents<Collider2D>())
            {
                if (!col.enabled) continue;
                col.enabled = false;
                var c = col;
                w.OnUnload(() => { if (c != null) c.enabled = true; });
            }
        }
    }

    public static void PlayMusic(JToken choice)
    {
        var player = Singleton<MusicPlayer>.Instance;
        var text = choice?.Type == JTokenType.String ? choice.Value<string>() : null;
        if (player == null || string.IsNullOrEmpty(text) || text == "level") return;
        if (text == "none") { FadeTo(player, null); return; }
        if (text.StartsWith("game:"))
        {
            if (!Enum.TryParse(text.Substring(5), out MusicPlayer.AUDIO_ID id)) return;
            player.currentTrackId = MusicPlayer.AUDIO_ID.none;
            player.JumpToNewMusic(id);
            return;
        }
        if (text.StartsWith("asset:") && _host != null) _host.StartCoroutine(PlayAsset(player, text.Substring(6)));
    }

    private static IEnumerator PlayAsset(MusicPlayer player, string file)
    {
        if (!Clips.TryGetValue(AssetPath(file), out var clip) || clip == null)
        {
            var path = AssetPath(file);
            if (!File.Exists(path)) { Debug.LogWarning("[RechargeMaps] music file missing: " + path); yield break; }
            var type = Path.GetExtension(path).ToLowerInvariant() switch { ".ogg" => AudioType.OGGVORBIS, ".wav" => AudioType.WAV, ".mp3" => AudioType.MPEG, _ => AudioType.UNKNOWN };
            using (var req = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, type))
            {
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success) { Debug.LogWarning("[RechargeMaps] couldn't load music " + file + ": " + req.error); yield break; }
                clip = DownloadHandlerAudioClip.GetContent(req);
            }
            clip.name = file;
            Clips[path] = clip;
        }
        if (player != null) FadeTo(player, clip);
    }

    // The game's two music players, crossfaded the way MusicPlayer does it.
    private static void FadeTo(MusicPlayer player, AudioClip clip)
    {
        const BindingFlags any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var curField = typeof(MusicPlayer).GetField("currentAudioPlayer", any);
        var oldField = typeof(MusicPlayer).GetField("oldAudioPlayer", any);
        var current = curField?.GetValue(player) as AudioSource;
        var old = oldField?.GetValue(player) as AudioSource;
        if (current == null || old == null) return;
        player.StopAllCoroutines();
        curField.SetValue(player, old);
        oldField.SetValue(player, current);
        player.currentTrackId = MusicPlayer.AUDIO_ID.none;
        old.outputAudioMixerGroup = current.outputAudioMixerGroup;
        old.clip = clip;
        old.loop = true;
        if (clip != null) old.Play(); else old.Stop();
        player.StartCoroutine(Crossfade(old, current));
    }

    private static IEnumerator Crossfade(AudioSource incoming, AudioSource outgoing)
    {
        var from = outgoing.volume;
        for (float t = 0f; t < 1f; t += Time.unscaledDeltaTime)
        {
            if (incoming == null || outgoing == null) yield break;
            incoming.volume = incoming.clip != null ? 0.5f * t : 0f;
            outgoing.volume = from * (1f - t);
            yield return null;
        }
        if (incoming != null) incoming.volume = incoming.clip != null ? 0.5f : 0f;
        if (outgoing != null) { outgoing.volume = 0f; outgoing.Stop(); }
    }

    public static void ShowBackground(JToken choice)
    {
        var bg = choice as JObject;
        var file = bg?["image"]?.Value<string>();
        var key = file == null ? null : file + "|" + bg["parallax"] + "|" + bg["scale"];
        if (key == _backgroundKey && (_background != null || key == null)) return;
        _backgroundKey = key;
        if (_background != null) UnityEngine.Object.Destroy(_background);
        _background = null;
        if (file == null || _world == null) return;

        var sprite = LoadImage(file, true);
        if (sprite == null) return;
        var go = new GameObject("MapBackground");
        go.transform.SetParent(_world.Root, false);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.drawMode = SpriteDrawMode.Tiled;
        // Just in front of the game's own backgrounds (about -200), behind the level.
        var game = UnityEngine.Object.FindFirstObjectByType<backgroundScroller>()?.GetComponent<SpriteRenderer>();
        if (game != null) sr.sortingLayerID = game.sortingLayerID;
        sr.sortingOrder = (game != null ? game.sortingOrder : -200) + 10;
        var follow = go.AddComponent<MapBackgroundFollow>();
        follow.Parallax = Mathf.Clamp01(bg["parallax"]?.Value<float>() ?? 0.8f);
        var scale = Mathf.Max(0.05f, bg["scale"]?.Value<float>() ?? 1f);
        go.transform.localScale = new Vector3(scale, scale, 1f);
        follow.Tile = new Vector2(sprite.rect.width * scale, sprite.rect.height * scale);
        _background = go;
    }

    // One unit per pixel, as the editor measures them.
    public static Sprite LoadImage(string file, bool repeat)
    {
        var path = AssetPath(file);
        if (Images.TryGetValue(path + repeat, out var cached) && cached != null) return cached;
        try
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { wrapMode = repeat ? TextureWrapMode.Repeat : TextureWrapMode.Clamp };
            if (!ImageConversion.LoadImage(tex, File.ReadAllBytes(path))) throw new Exception("not an image");
            var sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 1f, 0, SpriteMeshType.FullRect);
            Images[path + repeat] = sprite;
            return sprite;
        }
        catch (Exception e)
        {
            Debug.LogWarning("[RechargeMaps] image " + file + ": " + e.Message);
            return null;
        }
    }

    private static string AssetPath(string file) => Path.Combine(MapPaths.MapsDir, _world?.MapId ?? "", "assets", file);
}

// Keeps a map background filling the view: it drifts with the camera by its
// parallax (0 moves with the level, 1 stays put on screen) and wraps its tiles.
internal class MapBackgroundFollow : MonoBehaviour
{
    public float Parallax = 0.8f;
    public Vector2 Tile = new Vector2(1920f, 1080f);
    private SpriteRenderer _renderer;

    private void LateUpdate()
    {
        var cam = Camera.main;
        if (cam == null || Tile.x <= 0f || Tile.y <= 0f) return;
        var c = cam.transform.position;
        var drift = new Vector2(c.x * (1f - Parallax), c.y * (1f - Parallax));
        var x = c.x - Mathf.Repeat(drift.x, Tile.x);
        var y = c.y - Mathf.Repeat(drift.y, Tile.y);
        transform.position = new Vector3(x, y, transform.position.z);
        var viewH = cam.orthographicSize * 2f;
        var viewW = viewH * cam.aspect;
        var sr = _renderer ??= GetComponent<SpriteRenderer>();
        var scale = transform.lossyScale;
        if (sr != null && scale.x > 0f && scale.y > 0f)
            sr.size = new Vector2((Mathf.Ceil(viewW / Tile.x) + 2f) * Tile.x / scale.x, (Mathf.Ceil(viewH / Tile.y) + 2f) * Tile.y / scale.y);
    }
}

// A zone that switches the map's music and / or background when the player walks in.
internal class MapMediaTrigger : MonoBehaviour
{
    public JToken Music;
    public JToken Background;

    private void OnTriggerEnter2D(Collider2D collision)
    {
        if (!collision.gameObject.CompareTag("Player")) return;
        if (Music != null) MapMedia.PlayMusic(Music);
        if (Background != null) MapMedia.ShowBackground(Background);
    }
}
