namespace ComfyNetworkSense;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

using BepInEx;

using HarmonyLib;

using UnityEngine;

/// <summary>
/// Fixed-file, allow-listed action driver for the physical-client cutover lane. It is intentionally
/// not a console or input bridge: every action is bounded, correlated to the expiring native
/// autotest request, and emits a durable receipt before the harness advances.
/// </summary>
public sealed class NativeCutoverScenarioController : IDisposable {
  const int CurrentSchemaVersion = 1;
  const int MaxManifestBytes = 65536;
  const int MaxActions = 64;
  const float PollSeconds = 0.25f;
  const string ManifestFileName = "native-cutover-scenario.json";
  const string ReceiptFileName = "native-cutover-scenario-receipts.jsonl";

  readonly string _directory = Path.Combine(Paths.ConfigPath, "comfy-network-sense");
  readonly string _manifestPath;
  readonly string _receiptPath;
  readonly HashSet<string> _completedActionIds = new(StringComparer.Ordinal);
  readonly LumberjacksGameSessionRunner _gameSession;
  readonly RoutedRpcCutoverRunner _routedRpc;
  readonly ZdoJournalCutoverRunner _zdoJournal;
  readonly OwnershipLeaseCutoverRunner _ownershipLease;

  NativeCutoverScenarioManifest _manifest;
  NativeCutoverScenarioAction[] _actions = Array.Empty<NativeCutoverScenarioAction>();
  NativeCutoverScenarioAction _active;
  float _nextPollAt;
  float _actionStartedAt;
  float _actionDeadlineAt;
  Vector3 _origin;
  Vector2i _originZone;
  int _inventoryBefore;
  object _ownershipTarget;
  bool _sessionProbeStarted;
  bool _terminal;

  public NativeCutoverScenarioController(
      LumberjacksGameSessionRunner gameSession,
      RoutedRpcCutoverRunner routedRpc,
      ZdoJournalCutoverRunner zdoJournal,
      OwnershipLeaseCutoverRunner ownershipLease) {
    _gameSession = gameSession;
    _routedRpc = routedRpc;
    _zdoJournal = zdoJournal;
    _ownershipLease = ownershipLease;
    Directory.CreateDirectory(_directory);
    _manifestPath = Path.Combine(_directory, ManifestFileName);
    _receiptPath = Path.Combine(_directory, ReceiptFileName);
  }

  public void Update(float now) {
    if (_terminal) return;
    if (_manifest == null) {
      if (now < _nextPollAt) return;
      _nextPollAt = now + PollSeconds;
      TryLoad();
      return;
    }
    if (!JoinedClientReady()) return;

    if (_active == null) {
      NativeCutoverScenarioAction next =
          _actions.FirstOrDefault(action => !_completedActionIds.Contains(action.id));
      if (next == null) {
        WriteReceipt("scenario_complete", string.Empty, "all_actions_completed");
        TryDeleteManifest();
        _terminal = true;
        return;
      }
      Begin(next, now);
      if (_active == null || _terminal) return;
    }

    if (now > _actionDeadlineAt) {
      FailActive("deadline_exceeded");
      return;
    }
    TickActive(now);
  }

  void TryLoad() {
    if (!File.Exists(_manifestPath)
        || string.IsNullOrEmpty(NativeAutotestRequest.ActiveRunId)
        || string.IsNullOrEmpty(NativeAutotestRequest.ActiveClient)) {
      return;
    }

    try {
      FileInfo info = new(_manifestPath);
      if (info.Length is <= 0 or > MaxManifestBytes) {
        RejectManifest("manifest_size_invalid");
        return;
      }
      NativeCutoverScenarioManifest parsed = DeserializeManifest(File.ReadAllText(_manifestPath));
      string validation = Validate(parsed);
      if (!string.IsNullOrEmpty(validation)) {
        RejectManifest(validation);
        return;
      }

      _manifest = parsed;
      _actions = parsed.actions
          .Where(action => string.Equals(action.client, NativeAutotestRequest.ActiveClient,
                               StringComparison.OrdinalIgnoreCase)
              || string.Equals(action.client, "*", StringComparison.Ordinal))
          .ToArray();
      LoadCompletedActionIds();
      WriteReceipt("scenario_loaded", string.Empty, "actions=" + _actions.Length);
    } catch (Exception exception) {
      RejectManifest("manifest_read_failed:" + exception.GetType().Name);
    }
  }

  string Validate(NativeCutoverScenarioManifest parsed) {
    if (parsed == null || parsed.schema_version != CurrentSchemaVersion)
      return "manifest_schema_invalid";
    if (!SafeToken(parsed.run_id, 80)
        || !string.Equals(parsed.run_id, NativeAutotestRequest.ActiveRunId,
            StringComparison.Ordinal))
      return "manifest_run_mismatch";
    if (parsed.actions == null || parsed.actions.Length == 0
        || parsed.actions.Length > MaxActions)
      return "manifest_action_count_invalid:"
          + (parsed.actions == null ? "null" : parsed.actions.Length.ToString(CultureInfo.InvariantCulture));
    if (!DateTimeOffset.TryParseExact(
            parsed.expires_utc ?? string.Empty, "o", CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out DateTimeOffset expires)
        || expires <= DateTimeOffset.UtcNow || expires > DateTimeOffset.UtcNow.AddHours(2))
      return "manifest_expiry_invalid";

    HashSet<string> ids = new(StringComparer.Ordinal);
    foreach (NativeCutoverScenarioAction action in parsed.actions) {
      if (action == null || !SafeToken(action.id, 80) || !ids.Add(action.id))
        return "manifest_action_id_invalid";
      if (!SafeClient(action.client)) return "manifest_action_client_invalid";
      if (action.deadline_seconds is < 1.0f or > 300.0f)
        return "manifest_action_deadline_invalid";
      switch ((action.kind ?? string.Empty).Trim().ToLowerInvariant()) {
        case "wait":
          if (action.duration_seconds is < 0.1f or > 60.0f)
            return "manifest_wait_duration_invalid";
          break;
        case "move":
          if (action.duration_seconds is < 0.25f or > 60.0f
              || action.distance_meters is < 0.1f or > 64.0f
              || !AllowedDirection(action.direction))
            return "manifest_move_invalid";
          break;
        case "pickup_nearest":
          if (action.radius_meters is < 0.1f or > 8.0f)
            return "manifest_pickup_radius_invalid";
          break;
        case "ownership_target":
          if (action.radius_meters is < 0.1f or > 16.0f
              || !SafeTargetTag(action.target_tag, parsed.run_id))
            return "manifest_ownership_target_invalid";
          break;
        case "zone_cross":
          if (action.distance_meters is < 65.0f or > 160.0f
              || !AllowedDirection(action.direction))
            return "manifest_zone_cross_invalid";
          break;
        case "disconnect":
        case "disconnect_resume":
          break;
        case "session_resume_probe":
        case "session_timeout_probe":
        case "direct_control_pulse":
        case "direct_control_withhold":
        case "routed_request":
        case "routed_broadcast":
        case "routed_target_zdo":
        case "routed_withhold":
        case "zdo_journal_drive":
        case "zdo_journal_observe":
          break;
        case "ownership_lease_pickup":
          break;
        default:
          return "manifest_action_kind_invalid";
      }
    }
    return string.Empty;
  }

  void Begin(NativeCutoverScenarioAction action, float now) {
    _active = action;
    _actionStartedAt = now;
    _actionDeadlineAt = now + action.deadline_seconds;
    _origin = ((Component)Player.m_localPlayer).transform.position;
    _originZone = ZoneSystem.GetZone(_origin);
    _inventoryBefore = InventoryCount();
    _ownershipTarget = null;
    _sessionProbeStarted = false;
    WriteReceipt("action_started", action.id, "kind=" + action.kind);

    switch (action.kind.Trim().ToLowerInvariant()) {
      case "pickup_nearest":
        if (!TryInteractNearest(action, out string pickupDetail))
          FailActive(pickupDetail);
        break;
      case "ownership_target":
        if (!TryClaimOwnership(action, out string ownershipDetail))
          FailActive(ownershipDetail);
        break;
      case "disconnect":
        CompleteActive("disconnect_requested");
        Game.instance?.Logout();
        break;
      case "disconnect_resume":
        _completedActionIds.Add(action.id);
        WriteReceipt("resume_requested", action.id, "disconnect_then_relaunch");
        _active = null;
        Game.instance?.Logout();
        break;
    }
  }

  void TickActive(float now) {
    string kind = _active.kind.Trim().ToLowerInvariant();
    switch (kind) {
      case "wait":
        if (now - _actionStartedAt >= _active.duration_seconds)
          CompleteActive("wait_elapsed");
        break;
      case "move": {
        float fraction = Mathf.Clamp01((now - _actionStartedAt) / _active.duration_seconds);
        Vector3 target = _origin + Direction(_active.direction) * (_active.distance_meters * fraction);
        target.y = _origin.y;
        ((Component)Player.m_localPlayer).transform.position = target;
        if (fraction >= 1.0f) CompleteActive("distance_reached");
        break;
      }
      case "pickup_nearest":
        if (_inventoryBefore >= 0 && InventoryCount() > _inventoryBefore)
          CompleteActive("inventory_incremented");
        break;
      case "ownership_target":
        if (OwnershipReached(_ownershipTarget))
          CompleteActive("local_owner_observed");
        break;
      case "zone_cross": {
        Vector3 target = _origin + Direction(_active.direction) * _active.distance_meters;
        target.y = _origin.y;
        ((Component)Player.m_localPlayer).transform.position = target;
        Vector2i currentZone = ZoneSystem.GetZone(target);
        if (currentZone.x != _originZone.x || currentZone.y != _originZone.y)
          CompleteActive(
              "zone_changed_from=" + _originZone.x + "," + _originZone.y
              + "_to=" + currentZone.x + "," + currentZone.y);
        break;
      }
      case "session_resume_probe":
      case "session_timeout_probe": {
        if (!_sessionProbeStarted) {
          string mode = kind == "session_resume_probe" ? "resume" : "withhold_receipt";
          if (!_gameSession.BeginProbe(
                  _active.id, mode, Mathf.Max(1.0f, _active.deadline_seconds - 1.0f),
                  out string startDetail)) {
            if (startDetail == "lumberjacks_session_not_connected") return;
            FailActive(startDetail);
            return;
          }
          _sessionProbeStarted = true;
        }
        if (!_gameSession.TryGetProbeResult(
                _active.id, out bool terminal, out bool success, out string probeDetail)
            || !terminal) return;
        if (success) CompleteActive(probeDetail);
        else FailActive(probeDetail);
        break;
      }
      case "direct_control_pulse":
      case "direct_control_withhold": {
        if (!_sessionProbeStarted) {
          string mode = kind == "direct_control_pulse" ? "deliver" : "withhold";
          if (!_gameSession.BeginDirectPulseProbe(
                  _active.id, mode, Mathf.Max(1.0f, _active.deadline_seconds - 1.0f),
                  out string startDetail)) {
            if (startDetail == "lumberjacks_session_not_connected") return;
            FailActive(startDetail);
            return;
          }
          _sessionProbeStarted = true;
        }
        if (!_gameSession.TryGetDirectPulseProbeResult(
                _active.id, out bool terminal, out bool success, out string probeDetail)
            || !terminal) return;
        if (success) CompleteActive(probeDetail);
        else FailActive(probeDetail);
        break;
      }
      case "routed_request":
      case "routed_broadcast":
      case "routed_target_zdo":
      case "routed_withhold": {
        if (!_sessionProbeStarted) {
          string mode = kind switch {
              "routed_request" => "request",
              "routed_broadcast" => "broadcast",
              "routed_target_zdo" => "target_zdo",
              _ => "withhold"
          };
          if (!_routedRpc.BeginProbe(
                  _active.id, mode,
                  Mathf.Max(1.0f, _active.deadline_seconds - 1.0f),
                  out string startDetail)) {
            if (startDetail is "lumberjacks_session_not_connected"
                or "routed_probe_client_not_ready") return;
            FailActive(startDetail);
            return;
          }
          _sessionProbeStarted = true;
        }
        if (!_routedRpc.TryGetProbeResult(
                _active.id, out bool terminal, out bool success, out string probeDetail)
            || !terminal) return;
        if (success) CompleteActive(probeDetail);
        else FailActive(probeDetail);
        break;
      }
      case "zdo_journal_drive":
      case "zdo_journal_observe": {
        if (!_sessionProbeStarted) {
          string mode =
              kind == "zdo_journal_drive" ? "drive" : "observe";
          if (!_zdoJournal.BeginProbe(
                  _active.id, mode,
                  Mathf.Max(5.0f, _active.deadline_seconds - 1.0f),
                  out string startDetail)) {
            if (startDetail == "zdo_journal_client_not_ready") return;
            FailActive(startDetail);
            return;
          }
          _sessionProbeStarted = true;
        }
        if (!_zdoJournal.TryGetProbeResult(
                _active.id, out bool terminal, out bool success,
                out string probeDetail) || !terminal) return;
        if (success) CompleteActive(probeDetail);
        else FailActive(probeDetail);
        break;
      }
      case "ownership_lease_pickup": {
        if (!_sessionProbeStarted) {
          if (!_ownershipLease.BeginProbe(
                  _active.id,
                  Mathf.Max(20.0f, _active.deadline_seconds - 1.0f),
                  out string startDetail)) {
            if (startDetail is "lumberjacks_session_not_connected"
                or "ownership_lease_client_not_ready") return;
            FailActive(startDetail);
            return;
          }
          _sessionProbeStarted = true;
        }
        if (!_ownershipLease.TryGetProbeResult(
                _active.id, out bool terminal, out bool success,
                out string probeDetail) || !terminal) return;
        if (success) CompleteActive(probeDetail);
        else FailActive(probeDetail);
        break;
      }
    }
  }

  bool TryInteractNearest(NativeCutoverScenarioAction action, out string detail) {
    detail = string.Empty;
    Type pickableType = AccessTools.TypeByName("Pickable");
    if (pickableType == null) {
      detail = "pickable_type_unavailable";
      return false;
    }

    Component nearest = null;
    float nearestDistance = float.MaxValue;
    foreach (UnityEngine.Object candidate in Resources.FindObjectsOfTypeAll(pickableType)) {
      if (candidate is not Component component || component.gameObject == null
          || !component.gameObject.activeInHierarchy) continue;
      float distance =
          Vector3.Distance(((Component)Player.m_localPlayer).transform.position,
              component.transform.position);
      if (distance <= action.radius_meters && distance < nearestDistance) {
        nearest = component;
        nearestDistance = distance;
      }
    }
    if (nearest == null) {
      detail = "pickable_not_found";
      return false;
    }

    MethodInfo interact = nearest.GetType().GetMethods(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        .FirstOrDefault(method => method.Name == "Interact"
            && method.GetParameters().Length == 3);
    if (interact == null) {
      detail = "pickable_interact_unavailable";
      return false;
    }
    try {
      object result = interact.Invoke(nearest, new object[] { Player.m_localPlayer, false, false });
      if (result is bool accepted && !accepted) {
        detail = "pickable_interact_refused";
        return false;
      }
      return true;
    } catch (Exception exception) {
      detail = "pickable_interact_failed:" + (exception.InnerException ?? exception).GetType().Name;
      return false;
    }
  }

  bool TryClaimOwnership(NativeCutoverScenarioAction action, out string detail) {
    detail = string.Empty;
    ZNetView nearest = null;
    float nearestDistance = float.MaxValue;
    foreach (ZNetView candidate in Resources.FindObjectsOfTypeAll<ZNetView>()) {
      if (candidate == null || candidate.gameObject == null || !candidate.gameObject.activeInHierarchy
          || candidate.gameObject.name.IndexOf(action.target_tag,
                 StringComparison.OrdinalIgnoreCase) < 0) continue;
      float distance =
          Vector3.Distance(((Component)Player.m_localPlayer).transform.position,
              candidate.transform.position);
      if (distance <= action.radius_meters && distance < nearestDistance) {
        nearest = candidate;
        nearestDistance = distance;
      }
    }
    if (nearest == null) {
      detail = "run_tagged_ownership_target_not_found";
      return false;
    }
    try {
      nearest.ClaimOwnership();
      _ownershipTarget = nearest;
      return true;
    } catch (Exception exception) {
      detail = "ownership_claim_failed:" + exception.GetType().Name;
      return false;
    }
  }

  static bool OwnershipReached(object target) {
    if (target is not ZNetView view || view == null) return false;
    try {
      object zdo = view.GetZDO();
      if (zdo == null) return false;
      MethodInfo isOwner = AccessTools.Method(zdo.GetType(), "IsOwner", Type.EmptyTypes);
      return isOwner?.Invoke(zdo, null) is bool owned && owned;
    } catch {
      return false;
    }
  }

  static int InventoryCount() {
    try {
      object inventory = AccessTools.Method(Player.m_localPlayer.GetType(), "GetInventory")
          ?.Invoke(Player.m_localPlayer, null);
      if (inventory == null) return -1;
      object count = AccessTools.Method(inventory.GetType(), "NrOfItems", Type.EmptyTypes)
          ?.Invoke(inventory, null);
      return count == null ? -1 : Convert.ToInt32(count, CultureInfo.InvariantCulture);
    } catch {
      return -1;
    }
  }

  void CompleteActive(string detail) {
    string id = _active?.id ?? string.Empty;
    if (!string.IsNullOrEmpty(id)) _completedActionIds.Add(id);
    WriteReceipt("completed", id, detail);
    _active = null;
  }

  void FailActive(string detail) {
    WriteReceipt("failed", _active?.id ?? string.Empty, detail);
    _active = null;
    _terminal = true;
  }

  void RejectManifest(string detail) {
    WriteReceipt("manifest_rejected", string.Empty, detail);
    _terminal = true;
  }

  void LoadCompletedActionIds() {
    if (!File.Exists(_receiptPath)) return;
    try {
      foreach (string line in File.ReadLines(_receiptPath)) {
        NativeCutoverScenarioReceipt receipt =
            JsonUtility.FromJson<NativeCutoverScenarioReceipt>(line);
        if (receipt == null
            || !string.Equals(receipt.run_id, NativeAutotestRequest.ActiveRunId,
                StringComparison.Ordinal)
            || !string.Equals(receipt.client, NativeAutotestRequest.ActiveClient,
                StringComparison.OrdinalIgnoreCase)
            || receipt.state is not ("completed" or "resume_requested")
            || !SafeToken(receipt.action_id, 80)) continue;
        _completedActionIds.Add(receipt.action_id);
      }
    } catch (Exception exception) {
      RejectManifest("receipt_resume_read_failed:" + exception.GetType().Name);
    }
  }

  void WriteReceipt(string state, string actionId, string detail) {
    try {
      string line =
          "{\"schema_version\":1,\"timestamp_utc\":\""
          + DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture)
          + "\",\"state\":\"" + Escape(state)
          + "\",\"run_id\":\"" + Escape(NativeAutotestRequest.ActiveRunId)
          + "\",\"client\":\"" + Escape(NativeAutotestRequest.ActiveClient)
          + "\",\"action_id\":\"" + Escape(actionId)
          + "\",\"detail\":\"" + Escape(detail) + "\"}" + Environment.NewLine;
      File.AppendAllText(_receiptPath, line);
      ComfyNetworkSense.LogInfo(
          "CUTOVER_SCENARIO state=" + SafeMarker(state)
          + " run_id=" + SafeMarker(NativeAutotestRequest.ActiveRunId)
          + " client=" + SafeMarker(NativeAutotestRequest.ActiveClient)
          + " action=" + SafeMarker(actionId)
          + " detail=" + SafeMarker(detail));
    } catch (Exception exception) {
      ComfyNetworkSense.LogWarning(
          "Native cutover scenario receipt failed: " + exception.GetType().Name);
    }
  }

  void TryDeleteManifest() {
    try {
      if (File.Exists(_manifestPath)) File.Delete(_manifestPath);
    } catch (Exception exception) {
      ComfyNetworkSense.LogWarning(
          "Native cutover scenario cleanup failed: " + exception.GetType().Name);
    }
  }

  static NativeCutoverScenarioManifest DeserializeManifest(string raw) {
    DataContractJsonSerializer serializer =
        new(typeof(NativeCutoverScenarioManifest));
    using MemoryStream stream = new(Encoding.UTF8.GetBytes(raw ?? string.Empty));
    return serializer.ReadObject(stream) as NativeCutoverScenarioManifest;
  }

  static bool JoinedClientReady() {
    ZNet znet = ZNet.instance;
    return znet != null && !znet.IsServer() && Player.m_localPlayer != null
        && (znet.GetPeers()?.Count ?? 0) > 0;
  }

  static bool SafeToken(string value, int maxLength) =>
      !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength
      && value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.');

  static bool SafeClient(string value) =>
      string.Equals(value, "*", StringComparison.Ordinal)
      || SafeToken(value, 32);

  static bool SafeTargetTag(string value, string runId) =>
      !string.IsNullOrWhiteSpace(value) && value.Length <= 96
      && value.StartsWith("cutover-" + runId, StringComparison.Ordinal)
      && value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.');

  static bool AllowedDirection(string direction) =>
      (direction ?? string.Empty).Trim().ToLowerInvariant()
          is "north" or "east" or "south" or "west";

  static Vector3 Direction(string direction) {
    return (direction ?? string.Empty).Trim().ToLowerInvariant() switch {
        "east" => Vector3.right,
        "south" => Vector3.back,
        "west" => Vector3.left,
        _ => Vector3.forward
    };
  }

  static string SafeMarker(string value) =>
      string.IsNullOrWhiteSpace(value)
          ? "none"
          : value.Trim().Replace(' ', '_').Replace('\t', '_')
              .Replace('\r', '_').Replace('\n', '_');

  static string Escape(string value) =>
      (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"")
          .Replace("\r", "_").Replace("\n", "_");

  public void Dispose() {
    if (_active != null && !_terminal)
      WriteReceipt("failed", _active.id, "plugin_disposed");
  }
}

[Serializable, DataContract]
public sealed class NativeCutoverScenarioManifest {
  [DataMember(Name = "schema_version")]
  public int schema_version;
  [DataMember(Name = "run_id")]
  public string run_id;
  [DataMember(Name = "expires_utc")]
  public string expires_utc;
  [DataMember(Name = "actions")]
  public NativeCutoverScenarioAction[] actions;
}

[Serializable, DataContract]
public sealed class NativeCutoverScenarioAction {
  [DataMember(Name = "id")]
  public string id;
  [DataMember(Name = "client")]
  public string client;
  [DataMember(Name = "kind")]
  public string kind;
  [DataMember(Name = "deadline_seconds")]
  public float deadline_seconds;
  [DataMember(Name = "duration_seconds")]
  public float duration_seconds;
  [DataMember(Name = "distance_meters")]
  public float distance_meters;
  [DataMember(Name = "radius_meters")]
  public float radius_meters;
  [DataMember(Name = "direction")]
  public string direction;
  [DataMember(Name = "target_tag")]
  public string target_tag;
}

[Serializable]
public sealed class NativeCutoverScenarioReceipt {
  public string state;
  public string run_id;
  public string client;
  public string action_id;
}
