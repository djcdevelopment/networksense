namespace ComfyNetworkSense;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using UnityEngine;

public sealed class LumberjacksProjectionRunner : IDisposable {
  const int ConnectTimeoutMs = 5000;
  const int ReceiveBufferBytes = 32768;

  readonly ConcurrentQueue<ProjectedEntity> _pendingEntities = new();
  readonly Dictionary<string, ProxyObject> _proxies = new();
  readonly object _statsLock = new();

  CancellationTokenSource _cts;
  Task _receiveTask;
  Task _inputTask;
  ClientWebSocket _socket;

  string _gatewayUrl = "ws://127.0.0.1:4000";
  string _regionId = "region-spawn";
  string _playerId = string.Empty;
  string _status = "idle";
  string _lastError = string.Empty;
  string _lastMessageType = string.Empty;
  DateTime _startedUtc;
  DateTime _lastMessageUtc;

  int _messagesReceived;
  int _snapshotsReceived;
  int _updatesReceived;
  int _entitiesProjected;
  int _inputsSent;
  int _proxyCount;
  int _errors;
  bool _connected;
  bool _sessionStarted;
  bool _worldSnapshotReceived;
  bool _driveInputs;
  bool _clearRequested;

  bool _anchorSet;
  Vector3 _anchorPosition;
  bool _lumberjacksOriginSet;
  Vector3 _lumberjacksOrigin;

  Material _playerMaterial;
  Material _structureMaterial;
  Material _resourceMaterial;
  Material _selfMaterial;

  public bool IsRunning => _cts != null && !_cts.IsCancellationRequested;

  public string Start(string gatewayUrl, string regionId, bool driveInputs, TelemetryCoordinator coordinator) {
    if (IsRunning) {
      return $"Lumberjacks projection already running: {GetStatus()}";
    }

    _gatewayUrl = NormalizeGatewayUrl(gatewayUrl);
    _regionId = string.IsNullOrWhiteSpace(regionId) ? "region-spawn" : regionId.Trim();
    _driveInputs = driveInputs;
    _status = "starting";
    _lastError = string.Empty;
    _lastMessageType = string.Empty;
    _startedUtc = DateTime.UtcNow;
    _lastMessageUtc = DateTime.MinValue;
    _messagesReceived = 0;
    _snapshotsReceived = 0;
    _updatesReceived = 0;
    _entitiesProjected = 0;
    _inputsSent = 0;
    _proxyCount = 0;
    _errors = 0;
    _connected = false;
    _sessionStarted = false;
    _worldSnapshotReceived = false;
    _anchorSet = false;
    _lumberjacksOriginSet = false;

    _cts = new CancellationTokenSource();
    _receiveTask = Task.Run(() => RunReceiveLoop(_cts.Token, coordinator));

    coordinator?.RecordLumberjacksProjection(BuildStatusRow("start"));
    return $"Lumberjacks projection starting: {_gatewayUrl} region={_regionId} driveInputs={_driveInputs}.";
  }

  public string Stop(TelemetryCoordinator coordinator = null) {
    if (!IsRunning) {
      _clearRequested = true;
      return "Lumberjacks projection is not running; clearing local proxies.";
    }

    _status = "stopping";
    try {
      _cts.Cancel();
      _socket?.Abort();
    } catch {
      // Best-effort shutdown.
    }

    _clearRequested = true;
    coordinator?.RecordLumberjacksProjection(BuildStatusRow("stop"));
    return "Lumberjacks projection stopping; local proxies will be cleared.";
  }

  public void Update(float deltaTime) {
    using NetworkSensePerfProbe.Section section = NetworkSensePerfProbe.Measure("LumberjacksProjectionRunner.Update");

    if (_clearRequested) {
      _clearRequested = false;
      DestroyAllProxies();
    }

    int maxEntities = Mathf.Max(1, PluginConfig.LumberjacksProjectionMaxEntities.Value);
    int processed = 0;
    while (processed < maxEntities && _pendingEntities.TryDequeue(out ProjectedEntity entity)) {
      ApplyEntity(entity, maxEntities);
      processed++;
    }

    foreach (ProxyObject proxy in _proxies.Values) {
      if (proxy.GameObject == null) {
        continue;
      }

      proxy.Pulse += deltaTime * 2.5f;
      float scale = proxy.BaseScale * (1.0f + Mathf.Sin(proxy.Pulse) * 0.08f);
      proxy.GameObject.transform.localScale = new Vector3(scale, scale, scale);
      Debug.DrawLine(
          proxy.GameObject.transform.position,
          proxy.GameObject.transform.position + Vector3.up * 2.0f,
          proxy.DebugColor,
          0.05f,
          false);
    }
  }

  public string GetStatus() {
    lock (_statsLock) {
      return
          $"Lumberjacks projection: status={_status}, connected={_connected}, session={_sessionStarted}, "
          + $"snapshot={_worldSnapshotReceived}, proxies={Volatile.Read(ref _proxyCount)}, queued={_pendingEntities.Count}, "
          + $"messages={_messagesReceived}, updates={_updatesReceived}, inputs={_inputsSent}, error={_lastError}";
    }
  }

  public IDictionary<string, object> BuildStatusRow(string eventType) {
    lock (_statsLock) {
      return new Dictionary<string, object> {
          ["event"] = eventType,
          ["gateway_url"] = _gatewayUrl,
          ["region_id"] = _regionId,
          ["status"] = _status,
          ["connected"] = _connected,
          ["session_started"] = _sessionStarted,
          ["world_snapshot"] = _worldSnapshotReceived,
          ["player_id"] = _playerId,
          ["drive_inputs"] = _driveInputs,
          ["messages_received"] = _messagesReceived,
          ["snapshots_received"] = _snapshotsReceived,
          ["entity_updates_received"] = _updatesReceived,
          ["entities_projected"] = _entitiesProjected,
          ["proxy_count"] = Volatile.Read(ref _proxyCount),
          ["queued_entities"] = _pendingEntities.Count,
          ["inputs_sent"] = _inputsSent,
          ["errors"] = _errors,
          ["last_message_type"] = _lastMessageType,
          ["last_message_utc"] = _lastMessageUtc == DateTime.MinValue ? string.Empty : _lastMessageUtc.ToString("o"),
          ["last_error"] = _lastError,
          ["claim"] = "Lumberjacks entity rows are projected into Valheim as local-only Unity primitives without ZNetView or ZDO ownership."
      };
    }
  }

  async Task RunReceiveLoop(CancellationToken token, TelemetryCoordinator coordinator) {
    try {
      using ClientWebSocket socket = new();
      LumberjacksClientAuth.Apply(socket);
      _socket = socket;

      using (CancellationTokenSource connectCts = new(ConnectTimeoutMs)) {
        await socket.ConnectAsync(new Uri(_gatewayUrl), connectCts.Token).ConfigureAwait(false);
      }

      SetStatus("connected", connected: true);

      while (!token.IsCancellationRequested && socket.State == WebSocketState.Open) {
        string message = await TryReceiveText(socket, token).ConfigureAwait(false);
        if (string.IsNullOrEmpty(message)) {
          continue;
        }

        string type = ExtractMessageType(message);
        NoteMessage(type);

        if (string.Equals(type, "session_started", StringComparison.OrdinalIgnoreCase)) {
          _playerId = ExtractJsonString(message, "player_id");
          _sessionStarted = true;
          _status = "session_started";
          await SendText(socket, BuildEnvelope("join_region", "{\"region_id\":\"" + JsonEscape(_regionId) + "\"}"), token)
              .ConfigureAwait(false);

          if (_driveInputs) {
            _inputTask = Task.Run(() => RunInputLoop(socket, token));
          }
        } else if (string.Equals(type, "world_snapshot", StringComparison.OrdinalIgnoreCase)) {
          _worldSnapshotReceived = true;
          _status = "projecting";
          Interlocked.Increment(ref _snapshotsReceived);
          foreach (ProjectedEntity entity in ExtractProjectedEntities(message, "world_snapshot")) {
            _pendingEntities.Enqueue(entity);
          }
          coordinator?.RecordLumberjacksProjection(BuildStatusRow("world_snapshot"));
        } else if (string.Equals(type, "entity_update", StringComparison.OrdinalIgnoreCase)) {
          Interlocked.Increment(ref _updatesReceived);
          foreach (ProjectedEntity entity in ExtractProjectedEntities(message, "entity_update")) {
            _pendingEntities.Enqueue(entity);
          }
        } else if (string.Equals(type, "entity_removed", StringComparison.OrdinalIgnoreCase)) {
          string entityId = ExtractJsonString(message, "entity_id");
          if (!string.IsNullOrEmpty(entityId)) {
            _pendingEntities.Enqueue(ProjectedEntity.ForRemoval(entityId));
          }
        } else if (string.Equals(type, "error", StringComparison.OrdinalIgnoreCase)) {
          SetError(message.Length > 300 ? message.Substring(0, 300) : message);
        }
      }

      if (!token.IsCancellationRequested) {
        SetStatus("closed", connected: false);
      }
    } catch (OperationCanceledException) {
      SetStatus("stopped", connected: false);
    } catch (Exception exception) {
      SetError(exception.GetType().Name + ": " + exception.Message);
      coordinator?.RecordLumberjacksProjection(BuildStatusRow("error"));
    } finally {
      _socket = null;
      if (_cts != null && _cts.IsCancellationRequested) {
        SetStatus("stopped", connected: false);
      }
    }
  }

  async Task RunInputLoop(ClientWebSocket socket, CancellationToken token) {
    ushort sequence = 1;
    float hz = Mathf.Clamp(PluginConfig.LumberjacksProjectionInputHz.Value, 0.5f, 20.0f);
    int delayMs = Mathf.Max(50, Mathf.RoundToInt(1000.0f / hz));

    try {
      while (!token.IsCancellationRequested && socket.State == WebSocketState.Open) {
        byte direction = (byte) ((sequence * 19) % 255);
        string payload =
            "{\"direction\":" + direction
            + ",\"speed_percent\":70"
            + ",\"action_flags\":0"
            + ",\"input_seq\":" + sequence
            + "}";

        await SendText(socket, BuildEnvelope("player_input", payload), token).ConfigureAwait(false);
        Interlocked.Increment(ref _inputsSent);
        sequence++;

        await Task.Delay(delayMs, token).ConfigureAwait(false);
      }
    } catch (OperationCanceledException) {
      // Expected during projection shutdown.
    } catch (Exception exception) {
      SetError("input loop: " + exception.Message);
    }
  }

  static async Task SendText(ClientWebSocket socket, string text, CancellationToken token) {
    byte[] bytes = Encoding.UTF8.GetBytes(text);
    await socket.SendAsync(
        new ArraySegment<byte>(bytes),
        WebSocketMessageType.Text,
        true,
        token).ConfigureAwait(false);
  }

  static async Task<string> TryReceiveText(ClientWebSocket socket, CancellationToken token) {
    byte[] bytes = new byte[ReceiveBufferBytes];
    StringBuilder builder = new();

    WebSocketReceiveResult result;
    do {
      result = await socket.ReceiveAsync(new ArraySegment<byte>(bytes), token).ConfigureAwait(false);
      if (result.MessageType == WebSocketMessageType.Close) {
        return null;
      }

      if (result.MessageType != WebSocketMessageType.Text) {
        continue;
      }

      builder.Append(Encoding.UTF8.GetString(bytes, 0, result.Count));
    } while (!result.EndOfMessage);

    return builder.Length == 0 ? null : builder.ToString();
  }

  void ApplyEntity(ProjectedEntity entity, int maxEntities) {
    if (entity.Removed) {
      RemoveProxy(entity.EntityId);
      return;
    }

    if (string.IsNullOrWhiteSpace(entity.EntityId) || !entity.HasPosition) {
      return;
    }

    if (!_proxies.ContainsKey(entity.EntityId) && _proxies.Count >= maxEntities) {
      return;
    }

    if (!_lumberjacksOriginSet) {
      _lumberjacksOrigin = entity.Position;
      _lumberjacksOriginSet = true;
    }

    if (!_anchorSet) {
      _anchorPosition = ResolveAnchor();
      _anchorSet = true;
    }

    if (!_proxies.TryGetValue(entity.EntityId, out ProxyObject proxy) || proxy.GameObject == null) {
      proxy = CreateProxy(entity);
      _proxies[entity.EntityId] = proxy;
      _entitiesProjected++;
      Volatile.Write(ref _proxyCount, _proxies.Count);
    }

    Vector3 projected = MapPosition(entity.Position);
    proxy.GameObject.transform.position = projected;
    proxy.LastSeenUtc = DateTime.UtcNow;

    if (proxy.Label != null) {
      proxy.Label.text = BuildLabel(entity);
      proxy.Label.transform.position = projected + Vector3.up * 0.75f;
    }
  }

  ProxyObject CreateProxy(ProjectedEntity entity) {
    bool isSelf = !string.IsNullOrEmpty(_playerId) && string.Equals(entity.EntityId, _playerId, StringComparison.OrdinalIgnoreCase);
    PrimitiveType primitive = entity.EntityType == "structure" ? PrimitiveType.Cube : PrimitiveType.Sphere;
    GameObject gameObject = GameObject.CreatePrimitive(primitive);
    gameObject.name = "NetworkSense_LumberjacksProxy_" + SafeName(entity.EntityId);
    gameObject.transform.localScale = new Vector3(BaseScale(entity.EntityType), BaseScale(entity.EntityType), BaseScale(entity.EntityType));

    Collider collider = gameObject.GetComponent<Collider>();
    if (collider != null) {
      UnityEngine.Object.Destroy(collider);
    }

    Renderer renderer = gameObject.GetComponent<Renderer>();
    Color debugColor = ColorFor(entity.EntityType, isSelf);
    if (renderer != null) {
      renderer.material = MaterialFor(entity.EntityType, isSelf, debugColor);
    }

    TextMesh label = null;
    if (PluginConfig.LumberjacksProjectionLabelsEnabled.Value) {
      GameObject labelObject = new("NetworkSense_LumberjacksLabel_" + SafeName(entity.EntityId));
      labelObject.transform.SetParent(gameObject.transform, worldPositionStays: true);
      label = labelObject.AddComponent<TextMesh>();
      label.text = BuildLabel(entity);
      label.characterSize = 0.18f;
      label.anchor = TextAnchor.MiddleCenter;
      label.alignment = TextAlignment.Center;
      label.color = debugColor;
    }

    return new ProxyObject {
        GameObject = gameObject,
        Label = label,
        BaseScale = BaseScale(entity.EntityType),
        DebugColor = debugColor,
        LastSeenUtc = DateTime.UtcNow
    };
  }

  Vector3 ResolveAnchor() {
    Player player = Player.m_localPlayer;
    if (player != null) {
      Transform transform = ((Component) player).transform;
      Vector3 anchor = transform.position + transform.forward * PluginConfig.LumberjacksProjectionAnchorMeters.Value;
      if (TryResolveGroundHeight(anchor.x, anchor.z, out float ground)) {
        anchor.y = ground + 1.0f;
      }
      return anchor;
    }

    return new Vector3(0.0f, 80.0f, 0.0f);
  }

  Vector3 MapPosition(Vector3 lumberjacksPosition) {
    float scale = Mathf.Max(0.05f, PluginConfig.LumberjacksProjectionScale.Value);
    Vector3 offset = (lumberjacksPosition - _lumberjacksOrigin) * scale;
    Vector3 mapped = _anchorPosition + new Vector3(offset.x, 0.0f, offset.z);
    mapped.y = _anchorPosition.y + Mathf.Clamp(offset.y, -3.0f, 12.0f);

    if (TryResolveGroundHeight(mapped.x, mapped.z, out float ground)) {
      mapped.y = ground + 0.7f;
    }

    return mapped;
  }

  static bool TryResolveGroundHeight(float x, float z, out float height) {
    height = 0.0f;
    try {
      return ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(new Vector3(x, 0.0f, z), out height);
    } catch {
      return false;
    }
  }

  void DestroyAllProxies() {
    foreach (ProxyObject proxy in _proxies.Values) {
      if (proxy.GameObject != null) {
        UnityEngine.Object.Destroy(proxy.GameObject);
      }
    }

    _proxies.Clear();
    Volatile.Write(ref _proxyCount, 0);
  }

  void RemoveProxy(string entityId) {
    if (!_proxies.TryGetValue(entityId, out ProxyObject proxy)) {
      return;
    }

    if (proxy.GameObject != null) {
      UnityEngine.Object.Destroy(proxy.GameObject);
    }
    _proxies.Remove(entityId);
    Volatile.Write(ref _proxyCount, _proxies.Count);
  }

  Material MaterialFor(string entityType, bool isSelf, Color color) {
    if (isSelf) {
      return _selfMaterial ??= CreateMaterial(color);
    }

    switch (entityType) {
      case "structure":
        return _structureMaterial ??= CreateMaterial(color);
      case "natural_resource":
      case "oak_tree":
      case "pine_tree":
      case "birch_tree":
        return _resourceMaterial ??= CreateMaterial(color);
      default:
        return _playerMaterial ??= CreateMaterial(color);
    }
  }

  static Material CreateMaterial(Color color) {
    Shader shader = Shader.Find("Standard") ?? Shader.Find("Diffuse");
    Shader fallback = Shader.Find("GUI/Text Shader") ?? Shader.Find("Sprites/Default");
    Material material = shader != null ? new Material(shader) : new Material(fallback);
    material.color = color;
    return material;
  }

  static Color ColorFor(string entityType, bool isSelf) {
    if (isSelf) {
      return new Color(1.0f, 0.88f, 0.22f, 0.92f);
    }

    switch (entityType) {
      case "structure":
        return new Color(0.35f, 0.75f, 1.0f, 0.85f);
      case "natural_resource":
      case "oak_tree":
      case "pine_tree":
      case "birch_tree":
        return new Color(0.35f, 1.0f, 0.45f, 0.85f);
      default:
        return new Color(1.0f, 0.45f, 0.75f, 0.85f);
    }
  }

  static float BaseScale(string entityType) => entityType == "structure" ? 0.55f : 0.42f;

  static string BuildLabel(ProjectedEntity entity) {
    string id = entity.EntityId ?? string.Empty;
    if (id.Length > 8) {
      id = id.Substring(0, 8);
    }
    return $"{entity.EntityType}\n{id}";
  }

  static List<ProjectedEntity> ExtractProjectedEntities(string json, string source) {
    List<ProjectedEntity> entities = new();
    if (string.IsNullOrEmpty(json)) {
      return entities;
    }

    int index = 0;
    while ((index = json.IndexOf("\"entity_id\"", index, StringComparison.OrdinalIgnoreCase)) >= 0) {
      int next = json.IndexOf("\"entity_id\"", index + 1, StringComparison.OrdinalIgnoreCase);
      string segment = next > index ? json.Substring(index, next - index) : json.Substring(index);
      string entityId = ExtractJsonString(segment, "entity_id");
      string entityType = ExtractJsonString(segment, "entity_type");
      if (string.IsNullOrEmpty(entityType)) {
        entityType = ExtractJsonString(segment, "type");
      }
      if (string.IsNullOrEmpty(entityType)) {
        entityType = "entity";
      }

      if (TryExtractPosition(segment, out Vector3 position)) {
        entities.Add(new ProjectedEntity {
            EntityId = entityId,
            EntityType = entityType,
            Position = position,
            HasPosition = true,
            Source = source
        });
      }

      index += "\"entity_id\"".Length;
    }

    return entities;
  }

  static bool TryExtractPosition(string json, out Vector3 position) {
    position = default;
    Match match = Regex.Match(
        json,
        "\"position\"\\s*:\\s*\\{\\s*\"x\"\\s*:\\s*(?<x>-?[0-9]+(?:\\.[0-9]+)?)\\s*,\\s*\"y\"\\s*:\\s*(?<y>-?[0-9]+(?:\\.[0-9]+)?)\\s*,\\s*\"z\"\\s*:\\s*(?<z>-?[0-9]+(?:\\.[0-9]+)?)",
        RegexOptions.IgnoreCase);
    if (!match.Success) {
      return false;
    }

    if (!TryParseFloat(match.Groups["x"].Value, out float x)
        || !TryParseFloat(match.Groups["y"].Value, out float y)
        || !TryParseFloat(match.Groups["z"].Value, out float z)) {
      return false;
    }

    position = new Vector3(x, y, z);
    return true;
  }

  static bool TryParseFloat(string value, out float result) =>
      float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);

  static string BuildEnvelope(string type, string payloadJson) {
    return
        "{\"version\":1,"
        + "\"type\":\"" + JsonEscape(type) + "\","
        + "\"seq\":" + DateTime.UtcNow.Ticks + ","
        + "\"timestamp\":\"" + DateTimeOffset.UtcNow.ToString("o") + "\","
        + "\"payload\":" + payloadJson
        + "}";
  }

  static string ExtractMessageType(string json) => ExtractJsonString(json, "type");

  static string ExtractJsonString(string json, string propertyName) {
    if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(propertyName)) {
      return string.Empty;
    }

    Match match = Regex.Match(
        json,
        "\"" + Regex.Escape(propertyName) + "\"\\s*:\\s*\"(?<value>(?:\\\\.|[^\"])*)\"",
        RegexOptions.IgnoreCase);
    return match.Success ? match.Groups["value"].Value.Replace("\\\"", "\"").Replace("\\\\", "\\") : string.Empty;
  }

  static string NormalizeGatewayUrl(string gatewayUrl) {
    string env = Environment.GetEnvironmentVariable("COMFY_LUMBERJACKS_GATEWAY_URL");
    string value = !string.IsNullOrWhiteSpace(env) ? env : gatewayUrl;
    if (string.IsNullOrWhiteSpace(value)) {
      value = "ws://127.0.0.1:4000";
    }

    value = value.Trim();
    if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) {
      value = "ws://" + value.Substring("http://".Length);
    } else if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) {
      value = "wss://" + value.Substring("https://".Length);
    }

    return value;
  }

  static string JsonEscape(string value) {
    if (string.IsNullOrEmpty(value)) {
      return string.Empty;
    }

    return value
        .Replace("\\", "\\\\")
        .Replace("\"", "\\\"")
        .Replace("\r", "\\r")
        .Replace("\n", "\\n")
        .Replace("\t", "\\t");
  }

  static string SafeName(string value) {
    if (string.IsNullOrEmpty(value)) {
      return "entity";
    }

    StringBuilder builder = new(value.Length);
    foreach (char c in value) {
      builder.Append(char.IsLetterOrDigit(c) ? c : '_');
    }
    return builder.ToString();
  }

  void NoteMessage(string type) {
    lock (_statsLock) {
      _messagesReceived++;
      _lastMessageType = type;
      _lastMessageUtc = DateTime.UtcNow;
    }
  }

  void SetStatus(string status, bool? connected = null) {
    lock (_statsLock) {
      _status = status;
      if (connected.HasValue) {
        _connected = connected.Value;
      }
    }
  }

  void SetError(string error) {
    lock (_statsLock) {
      _status = "error";
      _lastError = error;
      _errors++;
    }
  }

  public void Dispose() {
    Stop();
    DestroyAllProxies();
    _cts?.Dispose();
    _cts = null;
  }

  sealed class ProjectedEntity {
    public string EntityId;
    public string EntityType;
    public Vector3 Position;
    public bool HasPosition;
    public string Source;
    public bool Removed;

    public static ProjectedEntity ForRemoval(string entityId) =>
        new() { EntityId = entityId, Removed = true };
  }

  sealed class ProxyObject {
    public GameObject GameObject;
    public TextMesh Label;
    public float BaseScale;
    public float Pulse;
    public Color DebugColor;
    public DateTime LastSeenUtc;
  }
}
