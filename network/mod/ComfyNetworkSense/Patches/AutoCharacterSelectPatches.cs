namespace ComfyNetworkSense;

using System;
using System.Collections;
using System.Globalization;
using System.Reflection;

using HarmonyLib;

using UnityEngine;

// Drives the Valheim FejdStartup character-select screen without a human: it selects a
// character (by configured name, env/config index, or a hostname-derived index so a swarm
// sharing one config still spreads across distinct characters) and calls OnCharacterStart,
// which connects to the server that the launch args (+connect) already point at.
//
// This is the last piece that turns a spawned lab client from "sitting at the menu with
// rtt/jitter/bytes = 0" into a connected multiplayer sample source. It is gated off by
// default and only fires for lab/swarm clients that opt in via config or COMFY_AUTOJOIN.
public static class AutoCharacterSelectPatches {
  static bool _armed;
  static bool _completed;

  static FieldInfo _profilesField;
  static FieldInfo _profileIndexField;
  static FieldInfo _newCharacterNameField;
  static MethodInfo _showCharacterSelection;
  static MethodInfo _updateCharacterList;
  static MethodInfo _onCharacterStart;
  static MethodInfo _setSelectedProfile;
  static MethodInfo _onCharacterNew;
  static MethodInfo _onNewCharacterDone;

  public static void Apply(Harmony harmony) {
    _profilesField = AccessTools.Field(typeof(FejdStartup), "m_profiles");
    _profileIndexField = AccessTools.Field(typeof(FejdStartup), "m_profileIndex");
    _newCharacterNameField = AccessTools.Field(typeof(FejdStartup), "m_csNewCharacterName");
    _showCharacterSelection = AccessTools.Method(typeof(FejdStartup), "ShowCharacterSelection");
    _updateCharacterList = AccessTools.Method(typeof(FejdStartup), "UpdateCharacterList");
    _onCharacterStart = AccessTools.Method(typeof(FejdStartup), "OnCharacterStart");
    _setSelectedProfile = AccessTools.Method(typeof(FejdStartup), "SetSelectedProfile", new[] { typeof(string) });
    _onCharacterNew = AccessTools.Method(typeof(FejdStartup), "OnCharacterNew");
    _onNewCharacterDone = AccessTools.Method(typeof(FejdStartup), "OnNewCharacterDone");

    MethodBase target =
        AccessTools.Method(typeof(FejdStartup), "Start")
        ?? AccessTools.Method(typeof(FejdStartup), "Awake");
    if (target == null) {
      ComfyNetworkSense.LogWarning("Auto-join patch skipped: FejdStartup.Start/Awake not found.");
      return;
    }

    try {
      harmony.Patch(
          target,
          postfix: new HarmonyMethod(typeof(AutoCharacterSelectPatches), nameof(FejdStartupReadyPostfix)));
      ComfyNetworkSense.LogInfo($"Auto-join patch applied: FejdStartup.{target.Name}");
    } catch (Exception exception) {
      ComfyNetworkSense.LogWarning($"Auto-join patch failed: {exception.Message}");
    }
  }

  static void FejdStartupReadyPostfix(FejdStartup __instance) {
    if (!IsEnabled() || _armed || _completed || __instance == null) {
      return;
    }

    _armed = true;
    __instance.StartCoroutine(DriveAutoJoin(__instance));
  }

  static IEnumerator DriveAutoJoin(FejdStartup fejd) {
    float delay = Mathf.Max(0.0f, PluginConfig.AutoJoinInitialDelaySeconds.Value);
    ComfyNetworkSense.LogInfo($"Auto-join armed; waiting {delay:0.##}s before driving character select.");
    yield return new WaitForSeconds(delay);

    // Jump straight to the character-select screen regardless of where the menu currently sits.
    Invoke(_showCharacterSelection, fejd, "ShowCharacterSelection");

    float poll = Mathf.Max(0.25f, PluginConfig.AutoJoinPollIntervalSeconds.Value);
    float deadline = Time.realtimeSinceStartup + Mathf.Max(5.0f, PluginConfig.AutoJoinTimeoutSeconds.Value);

    IList profiles = GetProfiles(fejd);
    while ((profiles == null || profiles.Count == 0) && Time.realtimeSinceStartup < deadline) {
      Invoke(_updateCharacterList, fejd, "UpdateCharacterList");
      yield return new WaitForSeconds(poll);
      profiles = GetProfiles(fejd);
    }

    if ((profiles == null || profiles.Count == 0) && PluginConfig.AutoJoinCreateIfMissing.Value) {
      yield return CreateCharacter(fejd);
      profiles = GetProfiles(fejd);
    }

    if (profiles == null || profiles.Count == 0) {
      ComfyNetworkSense.LogWarning("Auto-join aborted: no character profiles available and none created.");
      yield break;
    }

    int index = ResolveProfileIndex(profiles);
    PlayerProfile profile = profiles[index] as PlayerProfile;
    string name = SafeName(profile);
    ComfyNetworkSense.LogInfo(
        $"Auto-join selecting character index {index} ('{name}') of {profiles.Count} available.");

    _profileIndexField?.SetValue(fejd, index);
    if (profile != null && _setSelectedProfile != null) {
      TryInvoke(_setSelectedProfile, fejd, new object[] { profile.GetFilename() }, "SetSelectedProfile");
    }
    Invoke(_updateCharacterList, fejd, "UpdateCharacterList");

    yield return new WaitForSeconds(0.5f);

    Invoke(_onCharacterStart, fejd, "OnCharacterStart");
    _completed = true;
    ComfyNetworkSense.LogInfo(
        $"Auto-join started game as '{name}'; server connection is handled by launch args (+connect).");
  }

  static IEnumerator CreateCharacter(FejdStartup fejd) {
    string newName = ResolveNewCharacterName();
    ComfyNetworkSense.LogInfo($"Auto-join creating character '{newName}'.");

    Invoke(_onCharacterNew, fejd, "OnCharacterNew");
    yield return new WaitForSeconds(0.5f);

    object field = _newCharacterNameField?.GetValue(fejd);
    if (field == null) {
      ComfyNetworkSense.LogWarning("Auto-join could not find the new-character name field; using default name.");
    } else {
      // Set .text via reflection so we don't need a UnityEngine.UI / TMP assembly reference.
      PropertyInfo textProperty = field.GetType().GetProperty("text");
      if (textProperty != null && textProperty.CanWrite) {
        try {
          textProperty.SetValue(field, newName, null);
        } catch (Exception exception) {
          ComfyNetworkSense.LogWarning($"Auto-join could not set new-character name: {exception.Message}");
        }
      }
    }
    yield return new WaitForSeconds(0.25f);

    Invoke(_onNewCharacterDone, fejd, "OnNewCharacterDone");
    yield return new WaitForSeconds(0.75f);
  }

  static IList GetProfiles(FejdStartup fejd) {
    return _profilesField?.GetValue(fejd) as IList;
  }

  static int ResolveProfileIndex(IList profiles) {
    string targetName = FirstNonEmpty(
        Environment.GetEnvironmentVariable("COMFY_AUTOJOIN_CHARACTER"),
        PluginConfig.AutoJoinCharacterName.Value);
    if (!string.IsNullOrWhiteSpace(targetName)) {
      targetName = targetName.Trim();
      for (int i = 0; i < profiles.Count; i++) {
        if (profiles[i] is PlayerProfile candidate
            && string.Equals(SafeName(candidate), targetName, StringComparison.OrdinalIgnoreCase)) {
          return i;
        }
      }
      ComfyNetworkSense.LogWarning($"Auto-join character '{targetName}' not found; falling back to index.");
    }

    if (TryReadEnvInt("COMFY_AUTOJOIN_INDEX", out int envIndex)) {
      return WrapIndex(envIndex, profiles.Count);
    }

    if (PluginConfig.AutoJoinDeriveFromHostname.Value && TryDeriveHostIndex(out int hostIndex)) {
      ComfyNetworkSense.LogInfo($"Auto-join derived character index {hostIndex} from hostname.");
      return WrapIndex(hostIndex, profiles.Count);
    }

    return WrapIndex(PluginConfig.AutoJoinCharacterIndex.Value, profiles.Count);
  }

  static string ResolveNewCharacterName() {
    string baseName = PluginConfig.AutoJoinNewCharacterName.Value;
    if (string.IsNullOrWhiteSpace(baseName)) {
      baseName = "comfyplayer";
    }
    baseName = baseName.Trim();

    return TryReadHostNumber(out int hostNumber)
        ? $"{baseName}{hostNumber:00}"
        : baseName;
  }

  // Enabled if the COMFY_AUTOJOIN environment variable is set (wins), else the config flag.
  static bool IsEnabled() {
    string env = Environment.GetEnvironmentVariable("COMFY_AUTOJOIN");
    if (!string.IsNullOrWhiteSpace(env) && TryParseBool(env, out bool enabled)) {
      return enabled;
    }
    return PluginConfig.AutoJoinEnabled.Value;
  }

  static bool TryDeriveHostIndex(out int index) {
    // Hostnames are 1-based (valheim-client-01); character indices are 0-based.
    if (TryReadHostNumber(out int hostNumber)) {
      index = Math.Max(0, hostNumber - 1);
      return true;
    }
    index = 0;
    return false;
  }

  static bool TryReadHostNumber(out int number) {
    number = 0;
    string host;
    try {
      host = Environment.MachineName;
    } catch {
      host = null;
    }
    if (string.IsNullOrEmpty(host)) {
      host = Environment.GetEnvironmentVariable("HOSTNAME");
    }
    if (string.IsNullOrEmpty(host)) {
      return false;
    }

    int end = host.Length;
    while (end > 0 && !char.IsDigit(host[end - 1])) {
      end--;
    }
    int start = end;
    while (start > 0 && char.IsDigit(host[start - 1])) {
      start--;
    }
    if (start >= end) {
      return false;
    }

    return int.TryParse(
        host.Substring(start, end - start),
        NumberStyles.Integer,
        CultureInfo.InvariantCulture,
        out number);
  }

  static bool TryReadEnvInt(string name, out int value) {
    value = 0;
    string raw = Environment.GetEnvironmentVariable(name);
    return !string.IsNullOrWhiteSpace(raw)
        && int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
  }

  static int WrapIndex(int index, int count) {
    if (count <= 0) {
      return 0;
    }
    int wrapped = index % count;
    return wrapped < 0 ? wrapped + count : wrapped;
  }

  static string FirstNonEmpty(params string[] values) {
    foreach (string value in values) {
      if (!string.IsNullOrWhiteSpace(value)) {
        return value;
      }
    }
    return string.Empty;
  }

  static bool TryParseBool(string value, out bool result) {
    switch ((value ?? string.Empty).Trim().ToLowerInvariant()) {
      case "1":
      case "true":
      case "yes":
      case "on":
        result = true;
        return true;
      case "0":
      case "false":
      case "no":
      case "off":
        result = false;
        return true;
      default:
        result = false;
        return false;
    }
  }

  static void Invoke(MethodInfo method, object instance, string label) {
    TryInvoke(method, instance, Array.Empty<object>(), label);
  }

  static void TryInvoke(MethodInfo method, object instance, object[] args, string label) {
    if (method == null) {
      ComfyNetworkSense.LogWarning($"Auto-join step skipped: {label} not found.");
      return;
    }

    try {
      method.Invoke(instance, args);
    } catch (Exception exception) {
      Exception inner = exception.InnerException ?? exception;
      ComfyNetworkSense.LogWarning($"Auto-join step failed ({label}): {inner.Message}");
    }
  }

  static string SafeName(PlayerProfile profile) {
    try {
      return profile?.GetName() ?? string.Empty;
    } catch {
      return string.Empty;
    }
  }
}
