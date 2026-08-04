using System;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BizHawk.Common;
using BizHawk.Emulation.Common;
using BizHawk.Emulation.DiscSystem;
using BizHawkNetplay.Core.Emu;
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
            // The core's [Core] attribute name via BizHawk's own extension; fall back to the
            // emulator type name. The previous reflection here never matched anything: it looked
            // for a "Name" or "CoreName" PROPERTY (CoreName is a field), and its type-name test
            // missed PortedCoreAttribute, which every ported core uses — so every peer was
            // comparing CLR type names. Benign, since both peers degraded identically, but the
            // diagnostics named the wrong thing.
            try
            {
                var name = _emulator.Attributes()?.CoreName;
                if (!string.IsNullOrEmpty(name)) return name!;
            }
            catch { /* fall through to the type name */ }
            return _emulator.GetType().Name;
        }
    }

    public string CoreVersion =>
        _emulator.GetType().Assembly.GetName().Version?.ToString() ?? "0";

    /// <summary>
    /// Which BizHawk this is, from BizHawk's own <c>VersionInfo</c>: release, commit, branch, dev
    /// flag, architecture, custom-build string. See <see cref="BuildIdentity"/> for what
    /// <see cref="CoreVersion"/> was missing.
    ///
    /// The git fields are read by reflection rather than bound at compile time. They come from a
    /// generated partial class, so they are the parts most likely to be absent or renamed in an
    /// unusual build — and a tool that fails to load because a build had no commit hash would be a
    /// worse outcome than one that reports "?" and says so.
    /// </summary>
    public string BuildId
    {
        get
        {
            try
            {
                return BuildIdentity.Format(
                    VersionInfo.MainVersion,
                    VersionInfoField("GIT_HASH"),
                    VersionInfoField("GIT_BRANCH"),
                    VersionInfo.DeveloperBuild,
                    VersionInfo.CustomBuildString,
                    IntPtr.Size == 8);
            }
            catch { return ""; }   // "" means "not known", which the negotiator skips rather than refuses
        }
    }

    private static string? VersionInfoField(string name)
    {
        try
        {
            return typeof(VersionInfo)
                .GetField(name, BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null) as string;
        }
        catch { return null; }
    }

    /// <summary>
    /// The firmware BizHawk matched alongside the ROM — a PSX or Saturn BIOS, an NDS bootrom. Empty
    /// on the many systems that boot no firmware at all, which compares equal between two such peers.
    /// </summary>
    public string FirmwareHash => _apis.Emulation.GetGameInfo()?.FirmwareHash ?? "";

    /// <summary>BizHawk system identifier (for conservative per-system netplay defaults).</summary>
    public string SystemId => _emulator.SystemId;

    /// <summary>
    /// A hash per mounted disc, in the order the core holds them. Empty on the many systems that
    /// have none — which is not a failure, and compares equal between two such peers.
    ///
    /// <b>Both of BizHawk's own hashers, per disc.</b> <c>Calculate_PSX_BizIDHash</c> covers the TOC
    /// and the first 26 sectors, which is what catches a mangled rip and an audio-only disc that has
    /// no data track to read; <c>OldHash</c> is MD5 over up to 512 sectors of the first data track,
    /// which is a great deal more discriminating than the CRC32 the first one ends in. Together they
    /// read about 1.3 MB per disc, which is a file read and not worth optimising.
    ///
    /// The full redump hash reads every sector of every disc and is what BizHawk's own PSX menu
    /// warns "would take too long". Not at session start.
    ///
    /// Cached: the discs do not change under a running core, and re-reading them on every handshake
    /// would charge that cost to each joiner in turn.
    /// </summary>
    public IReadOnlyList<string> DiscHashes => _discHashes ??= ComputeDiscHashes();

    private IReadOnlyList<string>? _discHashes;

    private IReadOnlyList<string> ComputeDiscHashes()
    {
        var hashes = new List<string>();
        try
        {
            foreach (var disc in MountedDiscs())
            {
                string quick = "", deep = "";
                // Each guarded separately: a disc whose data track cannot be read should still
                // contribute its TOC rather than dropping out of the identity entirely.
                try { quick = new DiscHasher(disc).Calculate_PSX_BizIDHash() ?? ""; } catch { }
                try { deep = new DiscHasher(disc).OldHash() ?? ""; } catch { }
                hashes.Add(quick + "/" + deep);
            }
        }
        catch { return Array.Empty<string>(); }
        return hashes;
    }

    /// <summary>
    /// The discs a core is holding, found by reflection because no interface exposes them.
    ///
    /// Every disc-based core keeps them in a field of its own naming — Octoshock has a public
    /// <c>List&lt;Disc&gt; Discs</c>, Nymashock a private <c>IReadOnlyList&lt;Disc&gt; _discs</c>,
    /// NymaCore a private <c>Disc[] _disks</c> — so this matches on the element TYPE rather than on
    /// any name, and picks up a core nobody here has heard of for free.
    ///
    /// The first matching member wins, and members are taken in a fixed order, because the disc
    /// ORDER is part of the identity: a core exposing two different views of its discs must not
    /// yield a different order on two machines.
    /// </summary>
    private IEnumerable<Disc> MountedDiscs()
    {
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var type = _emulator.GetType();

        var fields = new List<FieldInfo>(type.GetFields(Any));
        fields.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        foreach (var f in fields)
            if (HoldsDiscs(f.FieldType) && Materialize(SafeGet(() => f.GetValue(_emulator))) is { } fromField)
                return fromField;

        var props = new List<PropertyInfo>(type.GetProperties(Any));
        props.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        foreach (var p in props)
            if (p.CanRead && p.GetIndexParameters().Length == 0 && HoldsDiscs(p.PropertyType)
                && Materialize(SafeGet(() => p.GetValue(_emulator))) is { } fromProp)
                return fromProp;

        return Array.Empty<Disc>();
    }

    private static bool HoldsDiscs(Type type)
    {
        if (type.IsArray) return type.GetElementType() == typeof(Disc);
        if (!type.IsGenericType) return false;
        var args = type.GetGenericArguments();
        return args.Length == 1 && args[0] == typeof(Disc) && typeof(IEnumerable<Disc>).IsAssignableFrom(type);
    }

    private static object? SafeGet(Func<object?> read)
    {
        try { return read(); } catch { return null; }
    }

    /// <summary>Null when the member held nothing usable, so the caller keeps looking rather than
    /// settling for an empty list from the first field that merely had the right type.</summary>
    private static List<Disc>? Materialize(object? value)
    {
        if (value is not IEnumerable<Disc> discs) return null;
        var list = new List<Disc>();
        try
        {
            foreach (var d in discs) if (d != null) list.Add(d);
        }
        catch { return null; }
        return list.Count > 0 ? list : null;
    }

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
    /// <summary>
    /// BizHawk's own shim for exactly this job, constructed read-only (every put callback refuses).
    /// Replaces a hand-rolled GetInterfaces walk that missed what the shim gets right: it resolves
    /// ISettable off the service provider, and it knows that <c>object</c> as a type argument is
    /// the "this half doesn't exist" placeholder — the old walk would happily have serialized a
    /// bare object as a core's settings.
    /// </summary>
    private SettingsAdapter ReadOnlySettings() =>
        new(_emulator, () => false, _ => { }, () => false, _ => { });

    private string SyncSettingsBlob() => TryGetSyncSettingsBlob(out string blob) ? blob : "";

    /// <summary>
    /// The core's sync settings as JSON, and whether reading them worked.
    ///
    /// The two answers used to be one. "This core has no sync settings" and "this core's settings
    /// threw" both returned an empty string, an empty string hashes to a constant, and so two peers
    /// that both failed to read produced matching digests — the handshake's most important
    /// comparison passing precisely because it had nothing to compare. False here now refuses the
    /// session (see <c>SessionNegotiator</c>); a core with genuinely no sync settings still returns
    /// true with an empty blob, because that is an answer rather than a failure.
    /// </summary>
    public bool TryGetSyncSettingsBlob(out string blob)
    {
        blob = "";
        try
        {
            var settings = ReadOnlySettings();
            if (!settings.HasSyncSettings) return true;
            blob = Newtonsoft.Json.JsonConvert.SerializeObject(settings.GetSyncSettings());
            return true;
        }
        catch { return false; }
    }

    /// <summary>True when this core's sync settings can be read at all — the value the handshake
    /// carries so a failure on either side refuses instead of matching.</summary>
    public bool SyncSettingsReadable => TryGetSyncSettingsBlob(out _);

    /// <summary>
    /// Whether this core qualifies as deterministic for netplay: its own flag, or a named exception
    /// to it. See <see cref="DeterminismPolicy"/> — the exception exists because Mupen64Plus reports
    /// false unconditionally and reads it back nowhere, while nearly every other core that reports
    /// false is telling you it seeded its clock from the wall.
    /// </summary>
    public bool QualifiesDeterministic =>
        DeterminismPolicy.Qualifies(VerifyDeterministicMode(), CoreName);

    /// <summary>The refusal to show the player when this core does not qualify, or null.</summary>
    public string? DeterminismGap()
    {
        try { return DeterminismPolicy.Refusal(VerifyDeterministicMode(), CoreName); }
        catch { return null; } // a reflection failure must not itself refuse the session
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
            var adapter = ReadOnlySettings();
            var settings = adapter.HasSettings ? adapter.GetSettings() : null;
            if (settings == null) return null;

            var x = MemberValue(settings, "VideoSizeX");
            var y = MemberValue(settings, "VideoSizeY");
            if (x == null || y == null) return null;

            var sync = adapter.HasSyncSettings ? adapter.GetSyncSettings() : null;
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
    /// Whether the core is rendering above its native resolution, or null when it exposes no
    /// resolution to read (every core but N64).
    ///
    /// This is the gate on the learned exclusion mask: at native the video write-back bytes
    /// already agree between machines, so masking there could only ever hide something real. The
    /// comparison is against the console's own framebuffer size rather than a fixed number,
    /// because "native" is what the VIDEO PROVIDER says it is.
    /// </summary>
    public bool? IsAboveNativeResolution()
    {
        try
        {
            var adapter = ReadOnlySettings();
            var settings = adapter.HasSettings ? adapter.GetSettings() : null;
            if (settings == null) return null;
            if (MemberValue(settings, "VideoSizeX") is not int x
                || MemberValue(settings, "VideoSizeY") is not int y) return null;

            var vp = _emulator.ServiceProvider.GetService<IVideoProvider>();
            // BufferWidth/Height is what the core actually presents at native; N64's own default
            // is 320x240. Falling back to that rather than to "unknown" keeps the gate closed
            // (above-native reads as true) instead of failing open into masking.
            int nativeX = vp?.BufferWidth ?? 320;
            int nativeY = vp?.BufferHeight ?? 240;
            if (nativeX <= 0 || nativeY <= 0) { nativeX = 320; nativeY = 240; }
            return x > nativeX || y > nativeY;
        }
        catch { return null; }
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
