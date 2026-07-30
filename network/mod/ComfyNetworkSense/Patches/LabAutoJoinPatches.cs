namespace ComfyNetworkSense;

using System;
using System.Collections;
using System.Globalization;
using System.Reflection;

using HarmonyLib;
using UnityEngine;

/// <summary>
/// Bounded, opt-in character selection for disposable Compose clients and short-lived native
/// Windows autotest requests. This restores only the smallest unattended seam: select an existing
/// profile and invoke Valheim's normal OnCharacterStart path. It does not create characters/worlds,
/// teleport, or run arbitrary scripts. Persistent COMFY_AUTOJOIN/config remains off by default;
/// physical clients arm through an expiring request file that is consumed after a real peer join.
/// </summary>
public static class LabAutoJoinPatches
{
    private static bool _armed;
    private static bool _completed;
    private static bool _joined;
    private static NativeAutotestRequest _nativeRequest;

    private static FieldInfo _profilesField;
    private static FieldInfo _profileIndexField;
    private static MethodInfo _showCharacterSelection;
    private static MethodInfo _updateCharacterList;
    private static MethodInfo _onCharacterStart;
    private static MethodInfo _setSelectedProfile;

    public static void Apply(Harmony harmony)
    {
        _profilesField = AccessTools.Field(typeof(FejdStartup), "m_profiles");
        _profileIndexField = AccessTools.Field(typeof(FejdStartup), "m_profileIndex");
        _showCharacterSelection = AccessTools.Method(typeof(FejdStartup), "ShowCharacterSelection");
        _updateCharacterList = AccessTools.Method(typeof(FejdStartup), "UpdateCharacterList");
        _onCharacterStart = AccessTools.Method(typeof(FejdStartup), "OnCharacterStart");
        _setSelectedProfile = AccessTools.Method(typeof(FejdStartup), "SetSelectedProfile", new[] { typeof(string) });

        MethodBase target = AccessTools.Method(typeof(FejdStartup), "Start")
            ?? AccessTools.Method(typeof(FejdStartup), "Awake");
        if (target == null)
        {
            ComfyNetworkSense.LogWarning("Lab auto-join skipped: FejdStartup.Start/Awake was not found.");
            return;
        }

        try
        {
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(LabAutoJoinPatches), nameof(FejdStartupReadyPostfix)));
            ComfyNetworkSense.LogInfo($"Lab auto-join patch applied: FejdStartup.{target.Name}");
        }
        catch (Exception exception)
        {
            ComfyNetworkSense.LogWarning("Lab auto-join patch failed: " + exception.Message);
        }
    }

    private static void FejdStartupReadyPostfix(FejdStartup __instance)
    {
        if (!IsEnabled(out NativeAutotestRequest nativeRequest) || _armed || _completed || __instance == null) return;
        _nativeRequest = nativeRequest;
        _armed = true;
        _nativeRequest?.Record("ready",
            "renderer=" + SafeMarker(SystemInfo.graphicsDeviceName)
            + " renderer_type=" + SafeMarker(SystemInfo.graphicsDeviceType.ToString()));
        __instance.StartCoroutine(Drive(__instance));
    }

    private static IEnumerator Drive(FejdStartup fejd)
    {
        float delay = Mathf.Clamp(PluginConfig.AutoJoinInitialDelaySeconds.Value, 0.0f, 120.0f);
        yield return new WaitForSeconds(delay);

        float poll = Mathf.Clamp(PluginConfig.AutoJoinPollIntervalSeconds.Value, 0.25f, 10.0f);
        float deadline = Time.realtimeSinceStartup + Mathf.Clamp(PluginConfig.AutoJoinTimeoutSeconds.Value, 5.0f, 600.0f);
        while (!PlayFabManager.IsLoggedIn && Time.realtimeSinceStartup < deadline)
            yield return new WaitForSeconds(poll);

        if (!PlayFabManager.IsLoggedIn)
        {
            ComfyNetworkSense.LogWarning("Lab auto-join stopped: PlayFab authentication did not become ready before timeout.");
            yield break;
        }

        IList profiles = Profiles(fejd);
        bool launchArgsOwnCharacterSelection = HasConnectLaunchArg();
        if ((profiles == null || profiles.Count == 0) && !launchArgsOwnCharacterSelection)
        {
            Invoke(_showCharacterSelection, fejd, "ShowCharacterSelection");
            yield return new WaitForSeconds(poll);
            profiles = Profiles(fejd);
        }

        while ((profiles == null || profiles.Count == 0) && Time.realtimeSinceStartup < deadline)
        {
            if (!launchArgsOwnCharacterSelection)
                Invoke(_updateCharacterList, fejd, "UpdateCharacterList");
            yield return new WaitForSeconds(poll);
            profiles = Profiles(fejd);
        }

        if (profiles == null || profiles.Count == 0)
        {
            ComfyNetworkSense.LogWarning("Lab auto-join stopped: no existing character profile was available.");
            yield break;
        }

        int index = ResolveProfileIndex(profiles);
        if (index < 0)
        {
            _nativeRequest?.Record("failed", "reason=requested_character_not_found");
            yield break;
        }
        PlayerProfile profile = profiles[index] as PlayerProfile;
        string name = SafeName(profile);
        _profileIndexField?.SetValue(fejd, index);
        if (profile != null && _setSelectedProfile != null)
            Invoke(_setSelectedProfile, fejd, "SetSelectedProfile", profile.GetFilename());
        Invoke(_updateCharacterList, fejd, "UpdateCharacterList");
        yield return new WaitForSeconds(0.5f);
        Invoke(_onCharacterStart, fejd, "OnCharacterStart");
        _completed = true;
        ComfyNetworkSense.LogInfo($"Lab auto-join started existing character '{name}' at profile index {index}.");
        _nativeRequest?.Record("character_selected", "profile_index=" + index);
    }

    private static IList Profiles(FejdStartup fejd) => _profilesField?.GetValue(fejd) as IList;

    private static int ResolveProfileIndex(IList profiles)
    {
        string requestedName = FirstNonEmpty(
            _nativeRequest?.Character,
            Environment.GetEnvironmentVariable("COMFY_AUTOJOIN_CHARACTER"),
            PluginConfig.AutoJoinCharacterName.Value);
        if (!string.IsNullOrWhiteSpace(requestedName))
        {
            for (int i = 0; i < profiles.Count; i++)
                if (profiles[i] is PlayerProfile candidate && string.Equals(SafeName(candidate), requestedName.Trim(), StringComparison.OrdinalIgnoreCase)) return i;
            if (_nativeRequest != null)
            {
                ComfyNetworkSense.LogWarning($"Native autotest character '{requestedName}' was not found; refusing fallback selection.");
                return -1;
            }
            ComfyNetworkSense.LogWarning($"Lab auto-join character '{requestedName}' was not found; falling back to index.");
        }

        if (TryReadEnvInt("COMFY_AUTOJOIN_INDEX", out int envIndex)) return WrapIndex(envIndex, profiles.Count);
        if (PluginConfig.AutoJoinDeriveFromHostname.Value && TryDeriveHostIndex(out int hostIndex)) return WrapIndex(hostIndex, profiles.Count);
        return WrapIndex(PluginConfig.AutoJoinCharacterIndex.Value, profiles.Count);
    }

    private static bool IsEnabled(out NativeAutotestRequest nativeRequest)
    {
        nativeRequest = null;
        if (NativeAutotestRequest.TryLoad(out NativeAutotestRequest request, out string detail))
        {
            nativeRequest = request;
            return true;
        }
        if (!string.IsNullOrWhiteSpace(detail))
            ComfyNetworkSense.LogWarning("Native autotest request ignored: " + detail);

        string env = Environment.GetEnvironmentVariable("COMFY_AUTOJOIN");
        if (!string.IsNullOrWhiteSpace(env) && TryParseBool(env, out bool enabled)) return enabled;
        return PluginConfig.AutoJoinEnabled.Value;
    }

    public static void PollJoined()
    {
        if (_nativeRequest == null || !_completed || _joined) return;
        ZNet znet = ZNet.instance;
        Player player = Player.m_localPlayer;
        if (znet == null || znet.IsServer() || player == null || (znet.GetPeers()?.Count ?? 0) == 0)
            return;

        _joined = true;
        _nativeRequest.Record(
            "joined",
            "peer_count=" + (znet.GetPeers()?.Count ?? 0)
            + " player=" + SafeMarker(SafePlayerName(player))
            + " renderer=" + SafeMarker(SystemInfo.graphicsDeviceName)
            + " renderer_type=" + SafeMarker(SystemInfo.graphicsDeviceType.ToString())
            + " graphics_memory_mb=" + SystemInfo.graphicsMemorySize);
        _nativeRequest.Consume();
    }

    private static bool TryDeriveHostIndex(out int index)
    {
        index = 0;
        string host = Environment.MachineName;
        if (string.IsNullOrWhiteSpace(host)) host = Environment.GetEnvironmentVariable("HOSTNAME");
        if (string.IsNullOrWhiteSpace(host)) return false;
        int end = host.Length;
        while (end > 0 && char.IsDigit(host[end - 1])) end--;
        if (end == host.Length) return false;
        int start = end;
        while (start > 0 && char.IsDigit(host[start - 1])) start--;
        return int.TryParse(host.Substring(start, end - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)
            && (index = Math.Max(0, number - 1)) >= 0;
    }

    private static bool TryReadEnvInt(string name, out int value) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static bool HasConnectLaunchArg()
    {
        try
        {
            foreach (string argument in Environment.GetCommandLineArgs())
                if (string.Equals(argument, "+connect", StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch { }
        return false;
    }

    private static int WrapIndex(int value, int count)
    {
        if (count <= 0) return 0;
        int wrapped = value % count;
        return wrapped < 0 ? wrapped + count : wrapped;
    }

    private static string SafeName(PlayerProfile profile)
    {
        try { return profile?.GetName() ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static string SafePlayerName(Player player)
    {
        try { return player?.GetPlayerName() ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static string SafeMarker(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "unknown";
        return value.Trim().Replace(' ', '_').Replace('\t', '_').Replace('\r', '_').Replace('\n', '_');
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (string value in values) if (!string.IsNullOrWhiteSpace(value)) return value;
        return string.Empty;
    }

    private static bool TryParseBool(string value, out bool result)
    {
        switch ((value ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "1": case "true": case "yes": case "on": result = true; return true;
            case "0": case "false": case "no": case "off": result = false; return true;
            default: result = false; return false;
        }
    }

    private static void Invoke(MethodInfo method, object instance, string label, params object[] args)
    {
        if (method == null)
        {
            ComfyNetworkSense.LogWarning("Lab auto-join step skipped: " + label + " was not found.");
            return;
        }
        try { method.Invoke(instance, args); }
        catch (Exception exception) { ComfyNetworkSense.LogWarning($"Lab auto-join step failed ({label}): {(exception.InnerException ?? exception).Message}"); }
    }
}
