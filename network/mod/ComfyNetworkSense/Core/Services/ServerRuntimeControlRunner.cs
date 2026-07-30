namespace ComfyNetworkSense;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

using BepInEx;

using UnityEngine;

// Dedicated-server control seam for the small set of rollback values that must be changeable
// without reloading a 9.15M-ZDO world. Authentication is deliberately outside the game process:
// an operator must use authenticated host access to atomically place one command in the mounted
// config directory. There is no listener, no general console bridge, and no arbitrary config key.
public sealed class ServerRuntimeControlRunner : IDisposable {
  const int MaxRememberedRequests = 256;

  readonly Func<string, string, RuntimeControlApplyResult> _apply;
  readonly string _commandPath =
      Path.Combine(Paths.ConfigPath, "comfy-network-sense", "runtime-control.json");
  readonly HashSet<string> _processed = new(StringComparer.Ordinal);
  readonly Queue<string> _processedOrder = new();

  float _nextPollAt;
  bool _loggedDisabledKey;

  public ServerRuntimeControlRunner(
      Func<string, string, RuntimeControlApplyResult> apply) {
    _apply = apply ?? throw new ArgumentNullException(nameof(apply));
  }

  public void Update(float now, TelemetryCoordinator coordinator) {
    if (!PluginConfig.ServerRuntimeControlEnabled.Value || !IsDedicatedServer()) {
      return;
    }

    float pollSeconds = Mathf.Clamp(
        PluginConfig.ServerRuntimeControlPollSeconds.Value, 0.25f, 10.0f);
    if (now < _nextPollAt) {
      return;
    }
    _nextPollAt = now + pollSeconds;

    if (!File.Exists(_commandPath)) {
      return;
    }

    string raw;
    try {
      raw = File.ReadAllText(_commandPath);
      File.Delete(_commandPath);
    } catch (Exception exception) {
      if (!_loggedDisabledKey) {
        _loggedDisabledKey = true;
        ZLog.LogWarning("[ComfyNetworkSense][server-control] command read failed: "
            + exception.GetType().Name + ": " + exception.Message);
      }
      return;
    }
    _loggedDisabledKey = false;

    RuntimeControlCommand command = null;
    try {
      command = JsonUtility.FromJson<RuntimeControlCommand>(raw);
    } catch {
      // Classified below as malformed; the raw command is never logged.
    }

    string requestId = command?.request_id?.Trim() ?? string.Empty;
    string setting = command?.setting?.Trim() ?? string.Empty;
    string requestedValue = command?.value?.Trim() ?? string.Empty;
    RuntimeControlApplyResult result;

    if (command == null || command.schema_version != 1) {
      result = RuntimeControlApplyResult.Refused("malformed_or_unsupported_schema");
    } else if (!IsSafeToken(requestId)) {
      result = RuntimeControlApplyResult.Refused("invalid_request_id");
    } else if (_processed.Contains(requestId)) {
      result = RuntimeControlApplyResult.Refused("duplicate_request_id");
    } else {
      Remember(requestId);
      try {
        result = _apply(setting, requestedValue);
      } catch (Exception exception) {
        result = RuntimeControlApplyResult.Refused(
            "apply_fault:" + exception.GetType().Name);
      }
    }

    Dictionary<string, object> row = new() {
        ["schema_version"] = 1,
        ["event"] = "server_runtime_control",
        ["timestamp_utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
        ["request_id"] = requestId,
        ["setting"] = setting,
        ["requested_value"] = requestedValue,
        ["result"] = result.Success ? "applied" : "refused",
        ["reason"] = result.Reason,
        ["old_value"] = result.OldValue,
        ["effective_value"] = result.EffectiveValue,
        ["effect"] = result.Effect,
        ["auth_context"] = "authenticated_host_filesystem"
    };
    coordinator?.RecordServerRuntimeControl(row);

    string message = "[ComfyNetworkSense][server-control] "
        + (result.Success ? "APPLIED" : "REFUSED")
        + " request=" + (requestId.Length == 0 ? "(invalid)" : requestId)
        + " setting=" + (setting.Length == 0 ? "(missing)" : setting)
        + " old=" + result.OldValue + " effective=" + result.EffectiveValue
        + " reason=" + result.Reason + " effect=" + result.Effect;
    if (result.Success) {
      ZLog.Log(message);
    } else {
      ZLog.LogWarning(message);
    }
  }

  void Remember(string requestId) {
    if (!_processed.Add(requestId)) {
      return;
    }
    _processedOrder.Enqueue(requestId);
    while (_processedOrder.Count > MaxRememberedRequests) {
      _processed.Remove(_processedOrder.Dequeue());
    }
  }

  static bool IsDedicatedServer() =>
      ZNet.instance != null && ZNet.instance.IsServer() && ZNet.instance.IsDedicated();

  static bool IsSafeToken(string value) {
    if (string.IsNullOrWhiteSpace(value) || value.Length > 80) {
      return false;
    }
    foreach (char c in value) {
      if (!char.IsLetterOrDigit(c) && c != '-' && c != '_' && c != '.') {
        return false;
      }
    }
    return true;
  }

  public void Dispose() { }

  [Serializable]
  sealed class RuntimeControlCommand {
    public int schema_version = 0;
    public string request_id = string.Empty;
    public string setting = string.Empty;
    public string value = string.Empty;
  }
}

public readonly struct RuntimeControlApplyResult {
  public readonly bool Success;
  public readonly string Reason;
  public readonly string OldValue;
  public readonly string EffectiveValue;
  public readonly string Effect;

  RuntimeControlApplyResult(
      bool success,
      string reason,
      string oldValue,
      string effectiveValue,
      string effect) {
    Success = success;
    Reason = reason ?? string.Empty;
    OldValue = oldValue ?? string.Empty;
    EffectiveValue = effectiveValue ?? string.Empty;
    Effect = effect ?? string.Empty;
  }

  public static RuntimeControlApplyResult Applied(
      string oldValue, string effectiveValue, string effect) =>
      new(true, "ok", oldValue, effectiveValue, effect);

  public static RuntimeControlApplyResult Refused(string reason) =>
      new(false, reason, string.Empty, string.Empty, "none");
}
