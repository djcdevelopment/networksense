namespace ComfyNetworkSense;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;

using UnityEngine;

// Swarm check-in poller: continuously collects benchmark data cell-by-cell from a matrix
// gateway. When enabled it loops: check out a cell, teleport the local player to it, measure
// load-time, run a NetworkSense benchmark for the cell's duration honoring its event profile,
// then report the metrics back. It is off by default and only fires for lab/swarm clients that
// opt in via config (matrixCheckinEnabled) or the COMFY_MATRIX_CHECKIN environment variable.
//
// Unity constraint: teleport and benchmark MUST run on the main thread (this class is driven by
// a coroutine on the ComfyNetworkSense MonoBehaviour). All HTTP runs off the main thread on
// Task.Run; the coroutine yields until the background task completes and then reads its result.
public sealed class MatrixCheckinRunner {
  const string AuthHeaderName = "X-Comfy-Key";
  const string AuthHeaderValue = "valheim-mod-local";
  const int HttpTimeoutMs = 8000;

  MonoBehaviour _host;
  TelemetryCoordinator _coordinator;
  bool _running;
  bool _loopActive;

  public bool IsRunning => _running;

  public void Start(MonoBehaviour host, TelemetryCoordinator coordinator) {
    if (_running) {
      return;
    }

    _host = host;
    _coordinator = coordinator;
    _running = true;
    _loopActive = true;
    _host.StartCoroutine(RunLoop());
    ComfyNetworkSense.LogInfo($"Matrix check-in started (client='{ResolveClientId()}', gateway='{ResolveGatewayUrl()}').");
  }

  public void Stop() {
    _loopActive = false;
    _running = false;
    _coordinator?.CancelBenchmark();
  }

  IEnumerator RunLoop() {
    while (_loopActive) {
      float backoff = Mathf.Max(0.5f, PluginConfig.MatrixPollIntervalSeconds.Value);

      // Only check out work when idle and a local player exists to teleport/benchmark.
      if (Player.m_localPlayer == null) {
        yield return new WaitForSeconds(backoff);
        continue;
      }

      CheckoutResponse checkout = null;
      yield return RunHttp(
          () => Checkout(ResolveGatewayUrl(), ResolveClientId()),
          result => checkout = result);

      if (checkout == null || checkout.Error != null) {
        ComfyNetworkSense.LogWarning(
            $"Matrix checkout failed: {checkout?.Error ?? "no response"}; backing off {backoff:0.##}s.");
        yield return new WaitForSeconds(backoff);
        continue;
      }

      if (checkout.Status != "assigned" || checkout.Cell == null) {
        // idle / done / no_plan — nothing to do right now.
        ComfyNetworkSense.LogInfo($"Matrix checkout: {checkout.Status}; backing off {backoff:0.##}s.");
        yield return new WaitForSeconds(backoff);
        continue;
      }

      yield return ExecuteCell(checkout.Cell);
    }

    _running = false;
  }

  IEnumerator ExecuteCell(MatrixCell cell) {
    ComfyNetworkSense.LogInfo(
        $"Matrix cell assigned: {cell.CellId} profile={cell.EventProfile} target=({cell.X:0.##},{cell.Z:0.##}) seconds={cell.BenchmarkSeconds:0.##}.");
    NetworkSensePerfProbe.SetRouteState("matrix", cell.CellId, "start");
    _coordinator.RecordDevMarker($"matrix_cell start {cell.CellId} profile={cell.EventProfile}");

    Player player = Player.m_localPlayer;
    if (player == null) {
      NetworkSensePerfProbe.SetRouteState("idle");
      ComfyNetworkSense.LogWarning($"Matrix cell {cell.CellId} aborted before teleport: no local player.");
      yield break;
    }

    // The cell's x/z already include the observer offset — teleport straight there.
    NetworkSensePerfProbe.SetRouteState("matrix", cell.CellId, "resolve_target");
    Vector3 target;
    using (NetworkSensePerfProbe.Measure("MatrixCheckinRunner.ResolveTarget")) {
      target = ResolveTarget(cell);
    }

    NetworkSensePerfProbe.SetRouteState("matrix", cell.CellId, "teleport");
    bool moved;
    using (NetworkSensePerfProbe.Measure("MatrixCheckinRunner.TryTeleport")) {
      moved = TryTeleport(player, target);
    }
    _coordinator.RecordDevMarker($"matrix_cell teleport {cell.CellId} moved={moved} target={FormatVector(target)}");

    // Measure load-time: from teleport until the destination zone reports loaded (a pragmatic
    // proxy for "first authoritative update at the destination"). Capped so we never hang here.
    NetworkSensePerfProbe.SetRouteState("matrix", cell.CellId, "zone_load_wait");
    float loadTimeMs = -1.0f;
    Vector2i zone = ZoneSystem.instance != null ? ZoneSystem.GetZone(target) : default;
    float loadStart = Time.realtimeSinceStartup;
    const float loadTimeoutSeconds = 30.0f;
    while (Time.realtimeSinceStartup - loadStart < loadTimeoutSeconds) {
      if (IsZoneLoaded(zone)) {
        loadTimeMs = (Time.realtimeSinceStartup - loadStart) * 1000.0f;
        break;
      }
      yield return null;
    }

    if (loadTimeMs < 0.0f) {
      loadTimeMs = (Time.realtimeSinceStartup - loadStart) * 1000.0f;
      _coordinator.RecordDevMarker($"matrix_cell {cell.CellId} load_timeout after {loadTimeMs:0}ms");
    }

    // Settle briefly so the client stabilizes before we start measuring the benchmark window.
    NetworkSensePerfProbe.SetRouteState("matrix", cell.CellId, "settle");
    yield return new WaitForSeconds(1.0f);

    // Honor the event profile. movement_only walks the player in a small pattern during the
    // benchmark; the heavier profiles are recorded but run stationary for now (see TODO below).
    bool moveDuringBenchmark = string.Equals(cell.EventProfile, "movement_only", StringComparison.OrdinalIgnoreCase);
    // TODO: implement build_social / combat_build / event_surge stimulus (spawn/build/combat
    // activity) rather than running stationary. Profile is recorded in the metrics either way.

    float benchmarkSeconds = Mathf.Max(1.0f, cell.BenchmarkSeconds);

    // Drive the shared BenchmarkRunner for exactly this cell's duration: temporarily override the
    // configured benchmark duration so the runner auto-completes and produces a BenchmarkResult,
    // then restore it. This reuses the coordinator's frame-probe logic instead of duplicating it.
    float previousDuration = PluginConfig.BenchmarkDurationSeconds.Value;
    PluginConfig.BenchmarkDurationSeconds.Value = benchmarkSeconds;

    BenchmarkResult benchmarkResult = null;
    try {
      NetworkSensePerfProbe.SetRouteState("matrix", cell.CellId, "benchmark_start");
      _coordinator.StartBenchmark();

      float benchStart = Time.realtimeSinceStartup;
      Vector3 origin = ((Component) player).transform.position;
      float maxWaitSeconds = benchmarkSeconds + 15.0f;

      NetworkSensePerfProbe.SetRouteState("matrix", cell.CellId, "benchmark_window");
      while (_coordinator.BenchmarkRunning && Time.realtimeSinceStartup - benchStart < maxWaitSeconds) {
        if (moveDuringBenchmark) {
          StepMovementPattern(player, origin, Time.realtimeSinceStartup - benchStart);
        }
        yield return null;
      }

      if (_coordinator.BenchmarkRunning) {
        NetworkSensePerfProbe.SetRouteState("matrix", cell.CellId, "benchmark_timeout_cancel");
        _coordinator.CancelBenchmark();
        _coordinator.RecordDevMarker($"matrix_cell {cell.CellId} benchmark_cancelled_after_timeout");
      }

      benchmarkResult = _coordinator.ConsumeLatestBenchmarkResult();
    } finally {
      PluginConfig.BenchmarkDurationSeconds.Value = previousDuration;
    }

    Dictionary<string, object> metrics;
    using (NetworkSensePerfProbe.Measure("MatrixCheckinRunner.BuildMetrics")) {
      metrics = BuildMetrics(cell, loadTimeMs, moveDuringBenchmark, benchmarkResult);
    }

    ReportResponse report = null;
    NetworkSensePerfProbe.SetRouteState("matrix", cell.CellId, "report");
    yield return RunHttp(
        () => Report(ResolveGatewayUrl(), ResolveClientId(), cell.CellId, metrics),
        result => report = result);

    if (report == null || report.Error != null) {
      ComfyNetworkSense.LogWarning($"Matrix report failed for {cell.CellId}: {report?.Error ?? "no response"}.");
    } else {
      ComfyNetworkSense.LogInfo($"Matrix cell reported: {cell.CellId} (load={loadTimeMs:0}ms).");
    }

    NetworkSensePerfProbe.SetRouteState("matrix", cell.CellId, "end");
    _coordinator.RecordDevMarker($"matrix_cell end {cell.CellId}");
    NetworkSensePerfProbe.SetRouteState("idle");
  }

  Dictionary<string, object> BuildMetrics(
      MatrixCell cell,
      float loadTimeMs,
      bool movedDuringBenchmark,
      BenchmarkResult benchmarkResult) {
    Dictionary<string, object> metrics = benchmarkResult != null
        ? new Dictionary<string, object>(benchmarkResult.ToDictionary())
        : new Dictionary<string, object>();

    metrics["load_time_ms"] = loadTimeMs;
    metrics["cell_id"] = cell.CellId;
    metrics["density_band"] = cell.DensityBand;
    metrics["observer_range"] = cell.ObserverRange;
    metrics["event_profile"] = cell.EventProfile;
    metrics["moved_during_benchmark"] = movedDuringBenchmark;
    metrics["benchmark_seconds_requested"] = cell.BenchmarkSeconds;
    metrics["client_id"] = ResolveClientId();
    metrics["benchmark_completed"] = benchmarkResult != null;

    ClientTelemetrySample sample = _coordinator.LatestClientSample;
    if (sample != null) {
      metrics["rtt_ms"] = sample.RttMs;
      metrics["jitter_ms"] = sample.JitterMs;
      metrics["bytes_in_per_sec"] = sample.BytesInPerSec;
      metrics["bytes_out_per_sec"] = sample.BytesOutPerSec;
      metrics["packets_in_per_sec"] = sample.PacketsInPerSec;
      metrics["packets_out_per_sec"] = sample.PacketsOutPerSec;
      metrics["nearby_players"] = sample.NearbyPlayers;
      metrics["nearby_entities"] = sample.NearbyEntities;
      metrics["nearby_build_pieces"] = sample.NearbyBuildPieces;
      metrics["region_id"] = sample.RegionId;
    }

    return metrics;
  }

  // Walk the player in a small square pattern around the teleport origin during movement_only cells.
  static void StepMovementPattern(Player player, Vector3 origin, float elapsedSeconds) {
    using NetworkSensePerfProbe.Section section = NetworkSensePerfProbe.Measure("MatrixCheckinRunner.StepMovementPattern");

    try {
      const float radius = 4.0f;
      float angle = elapsedSeconds * 1.5f;
      Vector3 offset = new(Mathf.Cos(angle) * radius, 0.0f, Mathf.Sin(angle) * radius);
      Vector3 next = origin + offset;
      if (TryResolveGroundHeight(next.x, next.z, out float ground)) {
        next.y = ground + 1.0f;
      } else {
        next.y = origin.y;
      }
      ((Component) player).transform.position = next;
    } catch (Exception exception) {
      ComfyNetworkSense.LogWarning($"Matrix movement step failed: {exception.Message}");
    }
  }

  // ---- Main-thread helpers (mirror ComfyNetworkSense teleport/ground-height logic) ----

  static Vector3 ResolveTarget(MatrixCell cell) {
    float y = 80.0f;
    if (TryResolveGroundHeight(cell.X, cell.Z, out float ground)) {
      y = ground + 3.0f;
    }
    return new Vector3(cell.X, y, cell.Z);
  }

  static bool IsZoneLoaded(Vector2i zone) {
    try {
      return ZoneSystem.instance != null && ZoneSystem.instance.IsZoneLoaded(zone);
    } catch (Exception exception) {
      ComfyNetworkSense.LogWarning($"Matrix zone-load check failed: {exception.Message}");
      return false;
    }
  }

  static bool TryResolveGroundHeight(float x, float z, out float height) {
    using NetworkSensePerfProbe.Section section = NetworkSensePerfProbe.Measure("MatrixCheckinRunner.TryResolveGroundHeight");

    height = 0.0f;
    try {
      if (ZoneSystem.instance == null) {
        return false;
      }
      return ZoneSystem.instance.GetGroundHeight(new Vector3(x, 0.0f, z), out height);
    } catch (Exception exception) {
      ComfyNetworkSense.LogWarning($"Matrix ground-height lookup failed: {exception.Message}");
      return false;
    }
  }

  static bool TryTeleport(Player player, Vector3 target) {
    using NetworkSensePerfProbe.Section section = NetworkSensePerfProbe.Measure("MatrixCheckinRunner.TryTeleportInternal");

    try {
      System.Reflection.MethodInfo method = null;
      foreach (System.Reflection.MethodInfo candidate in player.GetType().GetMethods(
          System.Reflection.BindingFlags.Public
          | System.Reflection.BindingFlags.NonPublic
          | System.Reflection.BindingFlags.Instance)) {
        if (candidate.Name == "TeleportTo" && candidate.GetParameters().Length == 3) {
          method = candidate;
          break;
        }
      }

      if (method != null) {
        object result = method.Invoke(player, new object[] { target, Quaternion.identity, true });
        if (result is bool ok && ok) {
          return true;
        }
      }

      ((Component) player).transform.position = target;
      Rigidbody body = ((Component) player).GetComponent<Rigidbody>();
      if (body != null) {
        body.position = target;
        body.linearVelocity = Vector3.zero;
      }
      return true;
    } catch (Exception exception) {
      ComfyNetworkSense.LogWarning($"Matrix teleport failed: {exception.Message}");
      return false;
    }
  }

  static string FormatVector(Vector3 value) {
    return string.Format(
        CultureInfo.InvariantCulture,
        "{0:0.##},{1:0.##},{2:0.##}",
        value.x,
        value.y,
        value.z);
  }

  // ---- Identity / config resolution ----

  static string ResolveClientId() {
    string configured = PluginConfig.MatrixClientId.Value;
    if (!string.IsNullOrWhiteSpace(configured)) {
      return configured.Trim();
    }

    string host;
    try {
      host = Environment.MachineName;
    } catch {
      host = null;
    }
    if (string.IsNullOrEmpty(host)) {
      host = Environment.GetEnvironmentVariable("HOSTNAME");
    }
    return string.IsNullOrEmpty(host) ? "comfy-client" : host;
  }

  static string ResolveGatewayUrl() {
    string env = Environment.GetEnvironmentVariable("COMFY_GATEWAY_URL");
    string url = !string.IsNullOrWhiteSpace(env) ? env : PluginConfig.MatrixGatewayUrl.Value;
    url = (url ?? string.Empty).Trim();
    if (string.IsNullOrEmpty(url)) {
      url = "http://127.0.0.1:8720";
    }
    return url.TrimEnd('/');
  }

  // Enabled if COMFY_MATRIX_CHECKIN is set (wins), else the config flag.
  public static bool IsEnabled() {
    string env = Environment.GetEnvironmentVariable("COMFY_MATRIX_CHECKIN");
    if (!string.IsNullOrWhiteSpace(env) && TryParseBool(env, out bool enabled)) {
      return enabled;
    }
    return PluginConfig.MatrixCheckinEnabled.Value;
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

  // ---- Off-main-thread HTTP, marshaled back through a coroutine ----

  // Runs a blocking HTTP call on a background task and yields until it finishes, then hands the
  // result to onDone on the main thread. Teleport/benchmark never touch a background thread.
  IEnumerator RunHttp<T>(Func<T> work, Action<T> onDone) where T : class {
    Task<T> task = Task.Run(work);
    while (!task.IsCompleted) {
      yield return null;
    }

    T result = null;
    try {
      result = task.Result;
    } catch (Exception exception) {
      ComfyNetworkSense.LogWarning($"Matrix HTTP task faulted: {exception.Message}");
    }

    onDone(result);
  }

  static CheckoutResponse Checkout(string gatewayUrl, string clientId) {
    string body = "{\"client\":\"" + JsonEscape(clientId) + "\"}";
    try {
      string json = PostJson(gatewayUrl + "/valheim/matrix/checkout", body);
      return ParseCheckout(json);
    } catch (Exception exception) {
      return new CheckoutResponse { Error = $"{exception.GetType().Name}: {exception.Message}" };
    }
  }

  static ReportResponse Report(string gatewayUrl, string clientId, string cellId, Dictionary<string, object> metrics) {
    StringBuilder builder = new();
    builder.Append("{\"client\":\"").Append(JsonEscape(clientId)).Append("\",");
    builder.Append("\"cell_id\":\"").Append(JsonEscape(cellId)).Append("\",");
    builder.Append("\"metrics\":").Append(SerializeMetrics(metrics)).Append('}');
    try {
      PostJson(gatewayUrl + "/valheim/matrix/report", builder.ToString());
      return new ReportResponse();
    } catch (Exception exception) {
      return new ReportResponse { Error = $"{exception.GetType().Name}: {exception.Message}" };
    }
  }

  static string PostJson(string endpoint, string body) {
    HttpWebRequest request = (HttpWebRequest) WebRequest.Create(endpoint);
    request.Method = "POST";
    request.ContentType = "application/json";
    request.Timeout = HttpTimeoutMs;
    request.ReadWriteTimeout = HttpTimeoutMs;
    request.Headers.Add(AuthHeaderName, AuthHeaderValue);

    byte[] payload = Encoding.UTF8.GetBytes(body);
    request.ContentLength = payload.Length;
    using (Stream requestStream = request.GetRequestStream()) {
      requestStream.Write(payload, 0, payload.Length);
    }

    using WebResponse response = request.GetResponse();
    using Stream stream = response.GetResponseStream();
    using StreamReader reader = new(stream);
    return reader.ReadToEnd();
  }

  // ---- Minimal JSON parsing/serialization (no external dependency, matches gateway contract) ----

  static CheckoutResponse ParseCheckout(string json) {
    CheckoutResponse response = new() { Status = ExtractString(json, "status") };
    if (response.Status != "assigned") {
      return response;
    }

    response.Cell = new MatrixCell {
        CellId = ExtractString(json, "cell_id"),
        DensityBand = ExtractString(json, "density_band"),
        ObserverRange = ExtractString(json, "observer_range"),
        EventProfile = ExtractString(json, "event_profile"),
        X = ExtractFloat(json, "x", 0.0f),
        Z = ExtractFloat(json, "z", 0.0f),
        ObserverOffsetM = ExtractFloat(json, "observer_offset_m", 0.0f),
        BenchmarkSeconds = ExtractFloat(json, "benchmark_seconds", 60.0f)
    };
    return response;
  }

  static string SerializeMetrics(Dictionary<string, object> metrics) {
    StringBuilder builder = new();
    builder.Append('{');
    bool first = true;
    foreach (KeyValuePair<string, object> pair in metrics) {
      if (!first) {
        builder.Append(',');
      }
      first = false;
      builder.Append('"').Append(JsonEscape(pair.Key)).Append("\":").Append(SerializeValue(pair.Value));
    }
    builder.Append('}');
    return builder.ToString();
  }

  static string SerializeValue(object value) {
    switch (value) {
      case null:
        return "null";
      case bool b:
        return b ? "true" : "false";
      case float f:
        return float.IsNaN(f) || float.IsInfinity(f)
            ? "null"
            : f.ToString("0.######", CultureInfo.InvariantCulture);
      case double d:
        return double.IsNaN(d) || double.IsInfinity(d)
            ? "null"
            : d.ToString("0.######", CultureInfo.InvariantCulture);
      case int i:
        return i.ToString(CultureInfo.InvariantCulture);
      case long l:
        return l.ToString(CultureInfo.InvariantCulture);
      default:
        return "\"" + JsonEscape(value.ToString()) + "\"";
    }
  }

  static string JsonEscape(string value) {
    if (string.IsNullOrEmpty(value)) {
      return string.Empty;
    }

    StringBuilder builder = new(value.Length);
    foreach (char c in value) {
      switch (c) {
        case '"':
          builder.Append("\\\"");
          break;
        case '\\':
          builder.Append("\\\\");
          break;
        case '\n':
          builder.Append("\\n");
          break;
        case '\r':
          builder.Append("\\r");
          break;
        case '\t':
          builder.Append("\\t");
          break;
        default:
          builder.Append(c);
          break;
      }
    }
    return builder.ToString();
  }

  // Reads "key":"value" from a flat JSON object. The checkout payload nests the cell fields one
  // level deep, but every key we read is unique across the payload, so a first-match scan is safe.
  static string ExtractString(string json, string key) {
    if (string.IsNullOrEmpty(json)) {
      return string.Empty;
    }

    string marker = "\"" + key + "\"";
    int keyPos = json.IndexOf(marker, StringComparison.Ordinal);
    if (keyPos < 0) {
      return string.Empty;
    }

    int colon = json.IndexOf(':', keyPos + marker.Length);
    if (colon < 0) {
      return string.Empty;
    }

    int pos = colon + 1;
    while (pos < json.Length && char.IsWhiteSpace(json[pos])) {
      pos++;
    }
    if (pos >= json.Length || json[pos] != '"') {
      return string.Empty;
    }

    pos++;
    StringBuilder builder = new();
    while (pos < json.Length && json[pos] != '"') {
      if (json[pos] == '\\' && pos + 1 < json.Length) {
        pos++;
        char escaped = json[pos];
        builder.Append(escaped switch {
          'n' => '\n',
          'r' => '\r',
          't' => '\t',
          _ => escaped
        });
      } else {
        builder.Append(json[pos]);
      }
      pos++;
    }
    return builder.ToString();
  }

  static float ExtractFloat(string json, string key, float fallback) {
    if (string.IsNullOrEmpty(json)) {
      return fallback;
    }

    string marker = "\"" + key + "\"";
    int keyPos = json.IndexOf(marker, StringComparison.Ordinal);
    if (keyPos < 0) {
      return fallback;
    }

    int colon = json.IndexOf(':', keyPos + marker.Length);
    if (colon < 0) {
      return fallback;
    }

    int pos = colon + 1;
    while (pos < json.Length && char.IsWhiteSpace(json[pos])) {
      pos++;
    }

    int start = pos;
    while (pos < json.Length && "0123456789.eE+-".IndexOf(json[pos]) >= 0) {
      pos++;
    }
    if (pos <= start) {
      return fallback;
    }

    return float.TryParse(
        json.Substring(start, pos - start),
        NumberStyles.Float,
        CultureInfo.InvariantCulture,
        out float result)
        ? result
        : fallback;
  }

  sealed class CheckoutResponse {
    public string Status;
    public MatrixCell Cell;
    public string Error;
  }

  sealed class ReportResponse {
    public string Error;
  }

  sealed class MatrixCell {
    public string CellId;
    public string DensityBand;
    public string ObserverRange;
    public string EventProfile;
    public float X;
    public float Z;
    public float ObserverOffsetM;
    public float BenchmarkSeconds;
  }
}
