using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BizHawkNetplay.Core.Session;

namespace BizHawkNetplay.Tool;

/// <summary>
/// What the core IS, as far as the handshake is concerned: name, version, ROM, and the sync
/// settings two peers must agree on before a session can be deterministic.
///
/// Everything here reads the emulator by reflection and must never throw — a settings read that
/// fails degrades the answer rather than preventing a session, because refusing to start over a
/// diagnostic is worse than starting without one.
/// </summary>
internal sealed partial class EmuHawkAdapter
{
    public string RomHash => _apis.Emulation.GetGameInfo()?.Hash ?? "unknown";

    public string CoreName
    {
        get
        {
            // Prefer the core's [Core] attribute name; fall back to the emulator type name.
            var attr = _emulator.GetType()
                .GetCustomAttributes(true)
                .FirstOrDefault(a => a.GetType().Name == "CoreAttribute");
            if (attr != null)
            {
                var nameProp = attr.GetType().GetProperty("Name") ?? attr.GetType().GetProperty("CoreName");
                if (nameProp?.GetValue(attr) is string s && s.Length > 0) return s;
            }
            return _emulator.GetType().Name;
        }
    }

    public string CoreVersion =>
        _emulator.GetType().Assembly.GetName().Version?.ToString() ?? "0";

    /// <summary>BizHawk system identifier (for conservative per-system netplay defaults).</summary>
    public string SystemId => _emulator.SystemId;

    // Identity = core + version + system + the core's REAL sync-settings blob. The blob is what
    // makes two peers on the same core but different per-core settings (e.g. an N64 video plugin, a
    // region, a CPU-core choice) fail the handshake up front instead of silently desyncing later.
    // Both peers run the identical core build (the handshake already requires matching CoreVersion),
    // so the same settings serialize to the same JSON and hash equal — while any real difference
    // diverges. Best-effort: if the settings can't be read, the blob is empty and this degrades to
    // the old coarse core+version+system digest (no false mismatch).
    public string SyncSettingsDigest
    {
        get
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(
                CoreName + "|" + CoreVersion + "|" + _emulator.SystemId + "|" + SyncSettingsBlob()));
            return BitConverter.ToString(bytes, 0, 8).Replace("-", string.Empty);
        }
    }

    /// <summary>
    /// The same settings <see cref="SyncSettingsDigest"/> hashes, flattened to sorted
    /// <c>name → value</c> pairs so a mismatch can be NAMED rather than merely detected.
    ///
    /// The digest stays the decision. This is lossy where the hash is not — nested objects become
    /// dotted paths, arrays become indexed ones, and both are truncated — so it can fail to explain
    /// a difference the digest sees, and the negotiator is written to say so rather than imply a
    /// match. Bounded because these fields are core-defined: nothing here chooses what a core puts
    /// in its settings, so it is capped instead of trusted.
    ///
    /// Sorted by name so both peers produce the same order regardless of how their core declared it.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>> SyncSettingsFields
    {
        get
        {
            var fields = new List<KeyValuePair<string, string>>();
            try
            {
                var blob = SyncSettingsBlob();
                if (blob.Length == 0) return fields;
                Flatten(Newtonsoft.Json.Linq.JToken.Parse(blob), "", fields);
                fields.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
                if (fields.Count > HandshakeCodec.MaxSyncFields)
                    fields.RemoveRange(HandshakeCodec.MaxSyncFields,
                        fields.Count - HandshakeCodec.MaxSyncFields);
            }
            catch { fields.Clear(); } // explanation is optional; the digest is not
            return fields;
        }
    }

    private static void Flatten(
        Newtonsoft.Json.Linq.JToken token, string path, List<KeyValuePair<string, string>> into)
    {
        // One over the cap, so the caller's trim is what enforces it and this cannot recurse for
        // ever into a pathological settings object.
        if (into.Count > HandshakeCodec.MaxSyncFields) return;
        switch (token)
        {
            case Newtonsoft.Json.Linq.JObject obj:
                foreach (var prop in obj.Properties())
                    Flatten(prop.Value, path.Length == 0 ? prop.Name : path + "." + prop.Name, into);
                break;
            case Newtonsoft.Json.Linq.JArray arr:
                for (int i = 0; i < arr.Count; i++) Flatten(arr[i], $"{path}[{i}]", into);
                break;
            default:
                var text = token.Type == Newtonsoft.Json.Linq.JTokenType.Null
                    ? ""
                    : token.ToString(Newtonsoft.Json.Formatting.None).Trim('"');
                if (text.Length > HandshakeCodec.MaxSyncFieldChars)
                    text = text.Substring(0, HandshakeCodec.MaxSyncFieldChars) + "…";
                into.Add(new KeyValuePair<string, string>(path.Length == 0 ? "value" : path, text));
                break;
        }
    }

    /// <summary>
    /// The core's live sync settings serialized to JSON, or "" if the core exposes none / it can't be
    /// read. Read straight from the core via <c>ISettable&lt;,&gt;.GetSyncSettings()</c> (the authoritative,
    /// both-peers-symmetric source) rather than the config dict, so a value the user just changed and a
    /// value loaded from disk can't serialize differently for the same logical settings.
    /// </summary>
    private string SyncSettingsBlob()
    {
        try
        {
            var settable = _emulator.GetType().GetInterfaces().FirstOrDefault(i =>
                i.IsGenericType && i.GetGenericTypeDefinition().FullName == "BizHawk.Emulation.Common.ISettable`2");
            var syncSettings = settable?.GetMethod("GetSyncSettings")?.Invoke(_emulator, null);
            if (syncSettings == null) return "";
            return Newtonsoft.Json.JsonConvert.SerializeObject(syncSettings);
        }
        catch { return ""; } // never let a settings read break the handshake — fall back to the coarse digest
    }

    /// <summary>
    /// The video settings that change what ends up in main RAM without being part of the sync
    /// settings — so the handshake never compares them and a desync is the first anyone hears.
    ///
    /// On N64 the render resolution lives in <c>N64Settings.VideoSizeX/Y</c>, which is the ordinary
    /// settings object, while the plugin choice and its framebuffer options live in
    /// <c>N64SyncSettings</c> and ARE compared. Above native resolution the plugin resolves its
    /// framebuffer back into RDRAM and those bytes come from the GPU, so two machines disagree even
    /// with byte-identical settings on both — measured as a desync at every single checksum at
    /// 800x600, and none at all at native, in lockstep and rollback alike.
    ///
    /// Reported rather than enforced: this reads whatever the loaded core happens to expose, and
    /// what counts as "too high" is a property of the game and plugin, not something worth guessing
    /// at from here. Returns a bare summary like "800x600, plugin Rice (InN64Resolution=False)" so
    /// callers can quote it in their own wording. Null when the core has no such setting.
    /// </summary>
    public string? VideoSettingsDiagnostic()
    {
        try
        {
            var settable = _emulator.GetType().GetInterfaces().FirstOrDefault(i =>
                i.IsGenericType && i.GetGenericTypeDefinition().FullName == "BizHawk.Emulation.Common.ISettable`2");
            var settings = settable?.GetMethod("GetSettings")?.Invoke(_emulator, null);
            if (settings == null) return null;

            var x = MemberValue(settings, "VideoSizeX");
            var y = MemberValue(settings, "VideoSizeY");
            if (x == null || y == null) return null;

            var sync = settable!.GetMethod("GetSyncSettings")?.Invoke(_emulator, null);
            var plugin = sync == null ? null : MemberValue(sync, "VideoPlugin");
            if (plugin == null) return $"{x}x{y}";

            // Whether the plugin renders at the console's own resolution is the setting that decides
            // whether those framebuffer bytes came off the GPU at all, so it belongs next to the size.
            // It lives on the selected plugin's own settings object, which each plugin names for
            // itself; the two that have one disagree even on what to call it. Absent for the rest.
            var pluginSettings = MemberValue(sync!, plugin + "Plugin");
            if (pluginSettings != null)
                foreach (var flag in new[] { "InN64Resolution", "UseNativeResolutionFactor" })
                {
                    var value = MemberValue(pluginSettings, flag);
                    if (value != null) return $"{x}x{y}, plugin {plugin} ({flag}={value})";
                }
            return $"{x}x{y}, plugin {plugin}";
        }
        catch { return null; } // a settings read must never disturb starting a session
    }

    /// <summary>
    /// Read a public instance member by name, whether it is a property or a field.
    ///
    /// BizHawk's settings objects mix the two freely — N64Settings exposes UseMupenStyleLag as a
    /// property and VideoSizeX/VideoSizeY as bare fields — so a property-only lookup returns null
    /// for exactly the values worth reporting, and does it silently.
    /// </summary>
    private static object? MemberValue(object target, string name)
    {
        var type = target.GetType();
        var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (property != null) return property.GetValue(target);
        return type.GetField(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(target);
    }

    public bool VerifyDeterministicMode() => _emulator.DeterministicEmulation;
}
