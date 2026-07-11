namespace ComfyNetworkSense;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using HarmonyLib;

using UnityEngine;

public sealed class ZdoInjectionPollResponse {
  public bool ok;
  public ZdoInjectionCommandDto[] commands;
}

public sealed class ZdoInjectionCommandDto {
  public string command_id;
  public string action;
  public string prefab;
  public long uid_user;
  public long uid_id;
  public long owner;
  public int owner_rev;
  public long data_rev;
  public float[] pos;
}

// P5/I4 inbound injection. Commands are fetched off-thread, validated and applied on Unity's
// main thread, then fed through Valheim's own RPC_ZDOData receive funnel. No arbitrary ZPackage
// bytes are accepted: this rung constructs one minimal persistent synthetic prefab body after
// checking the authority namespace, prefab allowlist, revision bounds, and local-player range.
public sealed class ZdoInjectionRunner : IDisposable {
  const float MaxTargetDistance = 128.0f;
  const int MaxCommandsPerPoll = 32;

  static readonly MethodInfo RpcZdoDataMethod = AccessTools.Method(
      typeof(ZDOMan), "RPC_ZDOData", new[] { typeof(ZRpc), typeof(ZPackage) });
  static volatile ZdoInjectionRunner _active;
  static volatile ZRpc _lastRpc;

  readonly object _lock = new();
  readonly ConcurrentQueue<ZdoInjectionCommandDto> _commands = new();
  readonly HashSet<string> _queuedIds = new(StringComparer.Ordinal);
  readonly HashSet<string> _completedIds = new(StringComparer.Ordinal);
  readonly Dictionary<ZDOID, AppliedObject> _awaitingRender = new();

  TelemetryCoordinator _coordinator;
  HashSet<string> _allowedPrefabs;
  string _endpoint = string.Empty;
  string _windowId = string.Empty;
  string _clientId = string.Empty;
  long _authorityId;
  bool _running;
  float _pollAt;
  float _stopAt = -1.0f;
  int _pollInFlight;
  long _polls;
  long _pollErrors;
  long _received;
  long _applied;
  long _rendered;
  long _rejected;
  long _duplicates;
  string _lastError = string.Empty;

  public bool IsRunning => _running;

  public string Start(TelemetryCoordinator coordinator) {
    if (RpcZdoDataMethod == null) {
      return "ZDO injection REFUSED: RPC_ZDOData reflection handle unavailable (game update?).";
    }
    _ = ParsePollResponse("{\"ok\":true,\"commands\":[]}");
    if (ZNet.instance == null || ZNet.instance.IsServer()) {
      return "ZDO injection REFUSED: rendered-client side only.";
    }
    if (!long.TryParse(PluginConfig.ZdoInjectionAuthorityId.Value,
        NumberStyles.Integer, CultureInfo.InvariantCulture, out long authority) || authority == 0) {
      return "ZDO injection REFUSED: zdoInjectionAuthorityId must be a non-zero Int64.";
    }
    HashSet<string> prefabs = BuildPrefabFilter(PluginConfig.ZdoInjectionPrefabs.Value);
    if (prefabs == null) {
      return "ZDO injection REFUSED: zdoInjectionPrefabs is empty; name the synthetic prefab(s).";
    }

    lock (_lock) {
      if (_running) {
        return StatusLineLocked();
      }
      _coordinator = coordinator;
      _allowedPrefabs = prefabs;
      _endpoint = PluginConfig.ZdoInjectionEndpoint.Value.TrimEnd('/');
      _windowId = string.IsNullOrWhiteSpace(PluginConfig.ZdoInjectionWindowId.Value)
          ? "i4-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
          : PluginConfig.ZdoInjectionWindowId.Value.Trim();
      _clientId = string.IsNullOrWhiteSpace(PluginConfig.ZdoInjectionClientId.Value)
          ? Environment.MachineName.ToLowerInvariant()
          : PluginConfig.ZdoInjectionClientId.Value.Trim();
      _authorityId = authority;
      _pollAt = Time.unscaledTime;
      float activeSeconds = Math.Max(0.0f, PluginConfig.ZdoInjectionActiveSeconds.Value);
      _stopAt = activeSeconds > 0.0f ? Time.unscaledTime + activeSeconds : -1.0f;
      _polls = _pollErrors = _received = _applied = _rendered = _rejected = _duplicates = 0;
      _lastError = string.Empty;
      _queuedIds.Clear();
      _completedIds.Clear();
      _awaitingRender.Clear();
      _running = true;
    }
    _active = this;
    coordinator?.RecordZdoInjection(BuildStatusRow("injection_start"));
    return "ZDO injection ARMED (client-side, synthetic-only) window=" + _windowId
        + " prefabs=" + prefabs.Count + " authority=" + authority
        + ". Rollback: zdoInjectionEnabled=false.";
  }

  public string Stop() => StopInternal("injection_stop");

  string StopInternal(string eventName) {
    lock (_lock) {
      if (!_running) {
        return "ZDO injection is not running.";
      }
      _running = false;
      _active = null;
    }
    _coordinator?.RecordZdoInjection(BuildStatusRow(eventName));
    return "ZDO injection disarmed; no further Lumberjacks commands will be applied.";
  }

  public string GetStatus() {
    lock (_lock) {
      return StatusLineLocked();
    }
  }

  public void Update(float deltaTime, TelemetryCoordinator coordinator) {
    if (!_running) {
      return;
    }
    if (_stopAt > 0.0f && Time.unscaledTime >= _stopAt) {
      StopInternal("injection_auto_stop");
      return;
    }

    CheckRenderedObjects();
    while (_commands.TryDequeue(out ZdoInjectionCommandDto command)) {
      lock (_lock) {
        _queuedIds.Remove(command.command_id ?? string.Empty);
      }
      Apply(command);
    }

    if (Time.unscaledTime >= _pollAt) {
      _pollAt = Time.unscaledTime + Math.Max(0.25f, PluginConfig.ZdoInjectionPollSeconds.Value);
      Poll();
    }
  }

  public static void CaptureRpc(ZRpc rpc) {
    if (rpc != null) {
      _lastRpc = rpc;
    }
  }

  void Poll() {
    if (Interlocked.CompareExchange(ref _pollInFlight, 1, 0) != 0) {
      return;
    }
    string url = _endpoint + "/valheim/zdo-injection/next/"
        + Uri.EscapeDataString(_windowId) + "?client_id=" + Uri.EscapeDataString(_clientId);
    _ = Task.Run(async () => {
      try {
        HttpWebRequest request = (HttpWebRequest) WebRequest.Create(url);
        request.Method = "GET";
        request.Timeout = 5000;
        request.ReadWriteTimeout = 5000;
        using WebResponse response = await request.GetResponseAsync().ConfigureAwait(false);
        using StreamReader reader = new(response.GetResponseStream());
        string json = await reader.ReadToEndAsync().ConfigureAwait(false);
        ZdoInjectionPollResponse payload = ParsePollResponse(json);
        lock (_lock) {
          _polls++;
        }
        int count = Math.Min(payload.commands.Length, MaxCommandsPerPoll);
        for (int i = 0; i < count; i++) {
          ZdoInjectionCommandDto command = payload.commands[i];
          string id = command?.command_id ?? string.Empty;
          lock (_lock) {
            if (_completedIds.Contains(id) || !_queuedIds.Add(id)) {
              _duplicates++;
              continue;
            }
            _received++;
          }
          _commands.Enqueue(command);
        }
      } catch (Exception exception) {
        lock (_lock) {
          _pollErrors++;
          _lastError = "poll: " + exception.GetType().Name + ": " + exception.Message;
        }
      } finally {
        Interlocked.Exchange(ref _pollInFlight, 0);
      }
    });
  }

  void Apply(ZdoInjectionCommandDto command) {
    if (!TryValidate(command, out ValidatedCommand valid, out string reason, out bool defer)) {
      if (defer) {
        return;
      }
      Complete(command?.command_id, applied: false, rendered: false, reason, null);
      return;
    }

    try {
      ZPackage body = new();
      body.Write((ushort) 0x100); // Persistent; no arbitrary extra-data collections.
      body.Write(valid.PrefabHash);

      ZPackage packet = new();
      packet.Write(0); // No invalid-sector entries.
      packet.Write(valid.Uid);
      packet.Write(valid.OwnerRevision);
      packet.Write(valid.DataRevision);
      packet.Write(_authorityId);
      packet.Write(valid.Position);
      packet.Write(body);
      packet.Write(ZDOID.None);
      // Network-delivered packages are positioned at zero; a locally built package
      // remains at its write cursor until explicitly rewound.
      packet.SetPos(0);

      // Keep the wire-shape observable when the game rejects a synthetic packet.
      _coordinator?.RecordZdoInjection(new Dictionary<string, object> {
          ["event"] = "injection_packet",
          ["status"] = _running ? "running" : "stopped",
          ["window_id"] = _windowId,
          ["command_id"] = command.command_id,
          ["packet_bytes"] = packet.Size(),
          ["body_bytes"] = body.Size(),
          ["timestamp_utc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
      });

      RpcZdoDataMethod.Invoke(ZDOMan.instance, new object[] { _lastRpc, packet });
      ZDO applied = ZDOMan.instance.GetZDO(valid.Uid);
      if (applied == null || applied.GetPrefab() != valid.PrefabHash
          || applied.GetOwner() != _authorityId || applied.DataRevision != valid.DataRevision) {
        throw new InvalidOperationException("vanilla receive funnel did not produce the expected ZDO readback");
      }

      lock (_lock) {
        _applied++;
        _completedIds.Add(command.command_id);
        _awaitingRender[valid.Uid] = new AppliedObject(command.command_id, valid.Uid, _authorityId);
      }
      RecordCommand("injection_applied", command, valid.Uid, applied.GetOwner(), string.Empty);
      PostAck(command.command_id, applied: true, rendered: false, "applied_waiting_render", applied.GetOwner());
    } catch (Exception exception) {
      Exception root = exception is TargetInvocationException tie && tie.InnerException != null
          ? tie.InnerException : exception;
      Complete(command.command_id, applied: false, rendered: false,
          root.GetType().Name + ": " + root.Message
          + (string.IsNullOrEmpty(root.StackTrace) ? string.Empty : " @ " + root.StackTrace.Split('\n')[0].Trim()), null);
    }
  }

  bool TryValidate(ZdoInjectionCommandDto command, out ValidatedCommand valid,
      out string reason, out bool defer) {
    valid = default;
    reason = string.Empty;
    defer = false;
    if (command == null || !ValidToken(command.command_id)) {
      reason = "invalid command_id";
      return false;
    }
    if (!string.Equals(command.action, "upsert", StringComparison.Ordinal)) {
      reason = "action must be upsert";
      return false;
    }
    if (string.IsNullOrWhiteSpace(command.prefab) || !_allowedPrefabs.Contains(command.prefab)) {
      reason = "prefab is not allowlisted";
      return false;
    }
    if (command.uid_user != _authorityId || command.owner != _authorityId
        || command.uid_id <= 0 || command.uid_id > uint.MaxValue) {
      reason = "command is outside the configured synthetic authority namespace";
      return false;
    }
    if (command.owner_rev <= 0 || command.owner_rev > ushort.MaxValue
        || command.data_rev <= 0 || command.data_rev > uint.MaxValue) {
      reason = "revision is outside the Valheim wire bounds";
      return false;
    }
    if (command.pos == null || command.pos.Length != 3
        || !Finite(command.pos[0]) || !Finite(command.pos[1]) || !Finite(command.pos[2])) {
      reason = "pos must contain three finite values";
      return false;
    }
    Vector3 position = new(command.pos[0], command.pos[1], command.pos[2]);
    if (Math.Abs(position.x) > 20000 || position.y < -500 || position.y > 2000
        || Math.Abs(position.z) > 20000) {
      reason = "position is outside the bounded test world envelope";
      return false;
    }
    if (Player.m_localPlayer == null
        || Vector3.Distance(Player.m_localPlayer.transform.position, position) > MaxTargetDistance) {
      reason = "waiting for local player to enter the synthetic fixture zone";
      defer = true;
      return false;
    }
    if (_lastRpc == null || ZDOMan.instance == null || ZNetScene.instance == null) {
      reason = "waiting for a live peer and scene";
      defer = true;
      return false;
    }
    int prefabHash = command.prefab.GetStableHashCode();
    if (!ZNetScene.instance.HasPrefab(prefabHash)) {
      reason = "prefab is not registered in the live ZNetScene";
      return false;
    }
    ZDOID uid = new(command.uid_user, checked((uint) command.uid_id));
    ZDO existing = ZDOMan.instance.GetZDO(uid);
    if (existing != null && existing.GetPrefab() != 0 && existing.GetPrefab() != prefabHash) {
      reason = "synthetic uid collides with a different prefab";
      return false;
    }
    valid = new(uid, prefabHash, checked((ushort) command.owner_rev),
        checked((uint) command.data_rev), position);
    return true;
  }

  void CheckRenderedObjects() {
    if (ZNetScene.instance == null) {
      return;
    }
    List<AppliedObject> renderedNow = new();
    lock (_lock) {
      foreach (AppliedObject pending in _awaitingRender.Values) {
        if (ZNetScene.instance.FindInstance(pending.Uid) != null) {
          renderedNow.Add(pending);
        }
      }
      foreach (AppliedObject item in renderedNow) {
        _awaitingRender.Remove(item.Uid);
        _rendered++;
      }
    }
    foreach (AppliedObject item in renderedNow) {
      ZdoInjectionCommandDto command = new() { command_id = item.CommandId };
      RecordCommand("injection_rendered", command, item.Uid, item.Owner, string.Empty);
      PostAck(item.CommandId, applied: true, rendered: true, "render_confirmed", item.Owner);
    }
  }

  void Complete(string commandId, bool applied, bool rendered, string reason, long? owner) {
    lock (_lock) {
      _rejected++;
      _lastError = reason;
      if (!string.IsNullOrEmpty(commandId)) {
        _completedIds.Add(commandId);
      }
    }
    ZdoInjectionCommandDto command = new() { command_id = commandId };
    RecordCommand("injection_rejected", command, null, owner, reason);
    PostAck(commandId, applied, rendered, reason, owner);
  }

  void PostAck(string commandId, bool applied, bool rendered, string reason, long? owner) {
    if (!ValidToken(commandId)) {
      return;
    }
    Dictionary<string, object> values = new() {
        ["window_id"] = _windowId,
        ["command_id"] = commandId,
        ["client_id"] = _clientId,
        ["applied"] = applied,
        ["rendered"] = rendered,
        ["reason"] = reason ?? string.Empty,
        ["observed_owner"] = owner
    };
    string json = JsonLineSerializer.Serialize(values);
    string url = _endpoint + "/valheim/zdo-injection/ack";
    _ = Task.Run(async () => {
      try {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);
        HttpWebRequest request = (HttpWebRequest) WebRequest.Create(url);
        request.Method = "POST";
        request.ContentType = "application/json";
        request.ContentLength = bytes.Length;
        request.Timeout = 5000;
        request.ReadWriteTimeout = 5000;
        using (Stream stream = await request.GetRequestStreamAsync().ConfigureAwait(false)) {
          await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
        }
        using WebResponse response = await request.GetResponseAsync().ConfigureAwait(false);
      } catch (Exception exception) {
        lock (_lock) {
          _lastError = "ack: " + exception.GetType().Name + ": " + exception.Message;
        }
      }
    });
  }

  void RecordCommand(string eventName, ZdoInjectionCommandDto command, ZDOID? uid, long? owner, string reason) {
    Dictionary<string, object> row = new() {
        ["event"] = eventName,
        ["status"] = _running ? "running" : "stopped",
        ["window_id"] = _windowId,
        ["command_id"] = command?.command_id ?? string.Empty,
        ["uid"] = uid?.ToString() ?? string.Empty,
        ["owner"] = owner?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        ["reason"] = reason ?? string.Empty,
        ["build_version"] = ComfyNetworkSense.PluginVersion
    };
    _coordinator?.RecordZdoInjection(row);
  }

  public IDictionary<string, object> BuildStatusRow(string eventName) {
    lock (_lock) {
      return new Dictionary<string, object> {
          ["event"] = eventName,
          ["status"] = _running ? "running" : "stopped",
          ["window_id"] = _windowId,
          ["client_id"] = _clientId,
          ["authority_id"] = _authorityId.ToString(CultureInfo.InvariantCulture),
          ["polls"] = _polls,
          ["poll_errors"] = _pollErrors,
          ["received"] = _received,
          ["applied"] = _applied,
          ["rendered"] = _rendered,
          ["rejected"] = _rejected,
          ["duplicates"] = _duplicates,
          ["awaiting_render"] = _awaitingRender.Count,
          ["last_error"] = _lastError,
          ["build_version"] = ComfyNetworkSense.PluginVersion
      };
    }
  }

  string StatusLineLocked() => "ZDO injection " + (_running ? "RUNNING" : "idle")
      + " window=" + _windowId + " received=" + _received + " applied=" + _applied
      + " rendered=" + _rendered + " rejected=" + _rejected + " poll_errors=" + _pollErrors;

  static HashSet<string> BuildPrefabFilter(string raw) {
    if (string.IsNullOrWhiteSpace(raw)) {
      return null;
    }
    HashSet<string> result = new(StringComparer.OrdinalIgnoreCase);
    foreach (string value in raw.Split(',')) {
      string trimmed = value.Trim();
      if (!string.IsNullOrEmpty(trimmed)) {
        result.Add(trimmed);
      }
    }
    return result.Count == 0 ? null : result;
  }

  static bool ValidToken(string value) {
    if (string.IsNullOrWhiteSpace(value) || value.Length > 128) {
      return false;
    }
    foreach (char c in value) {
      if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.')) {
        return false;
      }
    }
    return true;
  }

  static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

  static ZdoInjectionPollResponse ParsePollResponse(string json) {
    if (string.IsNullOrWhiteSpace(json) || !Regex.IsMatch(json, "\"ok\"\\s*:\\s*true")) {
      throw new InvalidDataException("poll response is missing ok:true");
    }
    Match commandsMatch = Regex.Match(json, "\"commands\"\\s*:\\s*\\[");
    if (!commandsMatch.Success) {
      throw new InvalidDataException("poll response is missing commands");
    }

    int cursor = commandsMatch.Index + commandsMatch.Length;
    List<ZdoInjectionCommandDto> commands = new();
    while (cursor < json.Length) {
      cursor = SkipWhitespace(json, cursor);
      if (cursor >= json.Length) {
        break;
      }
      if (json[cursor] == ']') {
        return new ZdoInjectionPollResponse { ok = true, commands = commands.ToArray() };
      }
      if (json[cursor] == ',') {
        cursor++;
        continue;
      }
      if (json[cursor] != '{') {
        throw new InvalidDataException("poll response command is not an object");
      }
      int end = FindMatchingBrace(json, cursor);
      commands.Add(ParseCommandObject(json.Substring(cursor, end - cursor + 1)));
      cursor = end + 1;
      if (commands.Count > MaxCommandsPerPoll) {
        break;
      }
    }

    throw new InvalidDataException("poll response commands array is unterminated");
  }

  static ZdoInjectionCommandDto ParseCommandObject(string json) => new() {
      command_id = RequiredString(json, "command_id"),
      action = RequiredString(json, "action"),
      prefab = RequiredString(json, "prefab"),
      uid_user = RequiredLong(json, "uid_user"),
      uid_id = RequiredLong(json, "uid_id"),
      owner = RequiredLong(json, "owner"),
      owner_rev = checked((int) RequiredLong(json, "owner_rev")),
      data_rev = RequiredLong(json, "data_rev"),
      pos = RequiredFloatArray3(json, "pos")
  };

  static string RequiredString(string json, string field) {
    Match match = Regex.Match(json, "\"" + Regex.Escape(field) + "\"\\s*:\\s*\"((?:\\\\.|[^\"])*)\"");
    if (!match.Success) {
      throw new InvalidDataException("poll command is missing " + field);
    }
    return Regex.Unescape(match.Groups[1].Value);
  }

  static long RequiredLong(string json, string field) {
    Match match = Regex.Match(json, "\"" + Regex.Escape(field) + "\"\\s*:\\s*(-?\\d+)");
    if (!match.Success || !long.TryParse(match.Groups[1].Value,
        NumberStyles.Integer, CultureInfo.InvariantCulture, out long value)) {
      throw new InvalidDataException("poll command is missing numeric " + field);
    }
    return value;
  }

  static float[] RequiredFloatArray3(string json, string field) {
    Match match = Regex.Match(json, "\"" + Regex.Escape(field)
        + "\"\\s*:\\s*\\[\\s*(-?\\d+(?:\\.\\d+)?)\\s*,\\s*(-?\\d+(?:\\.\\d+)?)\\s*,\\s*(-?\\d+(?:\\.\\d+)?)\\s*\\]");
    if (!match.Success) {
      throw new InvalidDataException("poll command is missing pos[3]");
    }
    return new[] {
        float.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
        float.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
        float.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture)
    };
  }

  static int SkipWhitespace(string value, int cursor) {
    while (cursor < value.Length && char.IsWhiteSpace(value[cursor])) {
      cursor++;
    }
    return cursor;
  }

  static int FindMatchingBrace(string value, int start) {
    bool escaped = false;
    bool inString = false;
    int depth = 0;
    for (int i = start; i < value.Length; i++) {
      char c = value[i];
      if (escaped) {
        escaped = false;
        continue;
      }
      if (c == '\\' && inString) {
        escaped = true;
        continue;
      }
      if (c == '"') {
        inString = !inString;
        continue;
      }
      if (inString) {
        continue;
      }
      if (c == '{') {
        depth++;
      } else if (c == '}') {
        depth--;
        if (depth == 0) {
          return i;
        }
      }
    }
    throw new InvalidDataException("poll command object is unterminated");
  }

  public void Dispose() {
    if (_running) {
      StopInternal("injection_dispose");
    }
  }

  readonly struct ValidatedCommand {
    public readonly ZDOID Uid;
    public readonly int PrefabHash;
    public readonly ushort OwnerRevision;
    public readonly uint DataRevision;
    public readonly Vector3 Position;

    public ValidatedCommand(
        ZDOID uid, int prefabHash, ushort ownerRevision, uint dataRevision, Vector3 position) {
      Uid = uid;
      PrefabHash = prefabHash;
      OwnerRevision = ownerRevision;
      DataRevision = dataRevision;
      Position = position;
    }
  }

  readonly struct AppliedObject {
    public readonly string CommandId;
    public readonly ZDOID Uid;
    public readonly long Owner;

    public AppliedObject(string commandId, ZDOID uid, long owner) {
      CommandId = commandId;
      Uid = uid;
      Owner = owner;
    }
  }

}

[HarmonyPatch(typeof(ZDOMan))]
static class ZdoInjectionPatches {
  [HarmonyPrefix]
  [HarmonyPatch("RPC_ZDOData", new[] { typeof(ZRpc), typeof(ZPackage) })]
  static void RpcZdoDataPrefix(ZRpc rpc) {
    ZdoInjectionRunner.CaptureRpc(rpc);
  }
}
