namespace ComfyNetworkSense;

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading.Tasks;

using BepInEx;

using UnityEngine;

public sealed class TelemetryCoordinator : IDisposable {
  const int MaxRecentEvents = 12;

  readonly string _sessionId =
      DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
  readonly TelemetryLogWriter _logWriter = new();
  readonly NetworkSensePerfProbe _perfProbe;
  readonly ClientTelemetrySampler _clientTelemetrySampler = new();
  readonly ServerPulseBroadcaster _serverPulseBroadcaster = new();
  readonly LumberjacksTelemetryHeartbeatRunner _lumberjacksTelemetryHeartbeatRunner = new();
  readonly BenchmarkRunner _benchmarkRunner = new();
  readonly HudRenderer _hudRenderer = new();
  readonly NetworkSensePanel _panel = new();
  readonly RavenState _ravenState = new();
  readonly List<NetworkSenseEventRow> _recentEvents = [];
  readonly string _exportRoot = Path.Combine(Paths.ConfigPath, "comfy-network-sense", "exports");

  LumberjacksPriorityMirrorRunner _priorityMirrorRunner;
  bool _hudVisible = PluginConfig.ShowHudOnStart.Value;
  HudDetailLevel _hudDetailLevel = HudDetailLevel.Summary;
  NetworkSenseMode _mode = NetworkSenseMode.Solo;

  float _lastClientSampleAt;
  string _lastRegionId = string.Empty;

  ClientTelemetrySample _latestClientSample;
  ServerPulseSnapshot _latestServerPulse;
  ScoreSnapshot _latestScores;
  ZRoutedRpc _registeredRoutedRpc;
  string _latestExportPath = string.Empty;
  BenchmarkResult _latestBenchmarkResult;
  Func<Dictionary<string, object>> _replacementTelemetryProvider;

  public TelemetryCoordinator() {
    _perfProbe = new NetworkSensePerfProbe(_sessionId, _logWriter);
    NetworkSensePerfProbe.SetActive(_perfProbe);
  }

  public void Update(float deltaTime) {
    using NetworkSensePerfProbe.Section section = NetworkSensePerfProbe.Measure("TelemetryCoordinator.Update");

    using (NetworkSensePerfProbe.Measure("TelemetryCoordinator.RegisterServerPulseRpc")) {
      RegisterServerPulseRpc();
    }

    using (NetworkSensePerfProbe.Measure("TelemetryCoordinator.HandleShortcuts")) {
      HandleShortcuts();
    }

    _clientTelemetrySampler.RecordFrame(deltaTime);

    BenchmarkResult benchmarkResult;
    using (NetworkSensePerfProbe.Measure("TelemetryCoordinator.BenchmarkRunner.Update")) {
      benchmarkResult = _benchmarkRunner.Update(deltaTime, _latestClientSample, _sessionId);
    }

    if (benchmarkResult != null) {
      _latestBenchmarkResult = benchmarkResult;
      _logWriter.Write("benchmark-results.jsonl", benchmarkResult.ToDictionary());
      WriteEvent("benchmark_end", $"Benchmark complete. Tier: {benchmarkResult.RecommendedHeadroomTier}");
      MessageHud.instance?.ShowMessage(
          MessageHud.MessageType.TopLeft,
          $"Network benchmark: {benchmarkResult.RecommendedHeadroomTier} headroom");
    }

    float sampleIntervalSeconds = Mathf.Max(0.1f, PluginConfig.LiveSampleIntervalSeconds.Value);

    // A dedicated server has no local player; the client sampler would otherwise fill
    // telemetry-client.jsonl with meaningless solo/no-player rows. Skip it there and let
    // ServerPulseBroadcaster be the server's clean data source.
    if (Time.unscaledTime - _lastClientSampleAt >= sampleIntervalSeconds && !IsDedicatedServer()) {
      _lastClientSampleAt = Time.unscaledTime;

      using (NetworkSensePerfProbe.Measure("TelemetryCoordinator.ClientTelemetrySampler.Capture")) {
        _latestClientSample = _clientTelemetrySampler.Capture(_sessionId, _mode, _latestServerPulse?.OwnerId);
      }

      using (NetworkSensePerfProbe.Measure("TelemetryCoordinator.ScoreCalculator.Calculate")) {
        _latestScores = ScoreCalculator.Calculate(_latestClientSample, _latestServerPulse, _mode);
      }

      Dictionary<string, object> clientRow = _latestClientSample.ToDictionary();
      Dictionary<string, object> replacement = _replacementTelemetryProvider?.Invoke();
      if (replacement != null) {
        foreach (KeyValuePair<string, object> pair in replacement) {
          clientRow[pair.Key] = pair.Value;
        }
      }
      _logWriter.Write("telemetry-client.jsonl", clientRow);

      if (_latestClientSample.RegionId != _lastRegionId) {
        WriteEvent("region_change", $"Region {(_lastRegionId == string.Empty ? "start" : _lastRegionId)} -> {_latestClientSample.RegionId}");
        _lastRegionId = _latestClientSample.RegionId;
      }
    }

    using (NetworkSensePerfProbe.Measure("TelemetryCoordinator.ServerPulseBroadcaster.Update")) {
      _serverPulseBroadcaster.Update(_sessionId, _mode, _logWriter);
    }

    _lumberjacksTelemetryHeartbeatRunner.Update(deltaTime, this);

    _perfProbe?.SetRuntimeContext(_latestClientSample?.RegionId ?? string.Empty, _benchmarkRunner.IsRunning);
  }

  static bool IsDedicatedServer() {
    return ZNet.instance != null && ZNet.instance.IsServer() && ZNet.instance.IsDedicated();
  }

  public void DrawHud() {
    using NetworkSensePerfProbe.Section section = NetworkSensePerfProbe.Measure("TelemetryCoordinator.DrawHud");

    _hudRenderer.Draw(
        _hudVisible,
        _hudDetailLevel,
        _mode,
        _benchmarkRunner.IsRunning,
        GetServerPulseState(),
        _latestClientSample,
        _latestServerPulse,
        _latestScores);

    _panel.Draw(
        GetSnapshot(),
        () => ToggleHud(),
        () => CycleHudDetail(),
        () => ToggleBenchmark(),
        () => ExportSession(),
        mode => SetMode(mode),
        () => ReloadConfig(),
        () => OpenExportFolder(),
        note => RecordDevNote(note),
        marker => RecordDevMarker(marker),
        request => StartRavenRequest(request),
        profile => StartApplyProfile(profile));
  }

  public void HandleServerPulse(long senderId, ZPackage package) {
    try {
      package.SetPos(0);
      _latestServerPulse = ServerPulseSnapshot.ReadFrom(package);
    } catch (Exception exception) {
      ComfyNetworkSense.LogWarning($"Ignored malformed server pulse from {senderId}: {exception.Message}");
      return;
    }

    if (_latestClientSample != null) {
      _latestScores = ScoreCalculator.Calculate(_latestClientSample, _latestServerPulse, _mode);
    }
  }

  public string ToggleHud() {
    _hudVisible = !_hudVisible;
    string message = _hudVisible ? "NetworkSense HUD enabled." : "NetworkSense HUD disabled.";
    WriteEvent("hud_toggle", message);
    MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
    return message;
  }

  public string CycleHudDetail() {
    _hudDetailLevel = (HudDetailLevel) (((int) _hudDetailLevel + 1) % 3);
    string message = $"NetworkSense HUD detail: {_hudDetailLevel}.";
    WriteEvent("hud_detail_change", message);
    MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
    return message;
  }

  public string CycleMode() {
    _mode = (NetworkSenseMode) (((int) _mode + 1) % 4);
    string message = $"NetworkSense mode: {ModeLabel(_mode)}.";
    WriteEvent("mode_change", message);
    MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
    return message;
  }

  public string SetMode(NetworkSenseMode mode) {
    _mode = mode;
    string message = $"NetworkSense mode: {ModeLabel(_mode)}.";
    WriteEvent("mode_change", message);
    MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
    return message;
  }

  public string ToggleBenchmark() {
    bool started = _benchmarkRunner.Toggle();
    string message = started ? "NetworkSense benchmark started." : "NetworkSense benchmark cancelled.";
    WriteEvent(started ? "benchmark_start" : "benchmark_cancel", message);
    MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
    return message;
  }

  public string GetStatus() {
    return
        $"NetworkSense status: hud={(_hudVisible ? "on" : "off")}, detail={_hudDetailLevel}, mode={_mode}, "
            + $"benchmark={(_benchmarkRunner.IsRunning ? "running" : "idle")}, "
            + $"region={_latestClientSample?.RegionId ?? "n/a"}, "
            + $"serverPulse={GetServerPulseState()}.";
  }

  public string GetPerfStatus() {
    string latestRegion = _latestClientSample?.RegionId ?? "n/a";
    return _perfProbe != null
        ? _perfProbe.GetStatus(latestRegion, _benchmarkRunner.IsRunning)
        : "NetworkSense perf probe inactive.";
  }

  public void TogglePanel(string tab = null) {
    if (_panel.IsOpen) {
      _panel.Close();
    } else {
      _panel.Open(tab);
    }
  }

  public void OpenPanel(string tab = null) {
    _panel.Open(tab);
  }

  public bool IsPanelOpen => _panel.IsOpen;

  public void ClosePanel() {
    _panel.Close();
  }

  // Matrix check-in support: the swarm runner drives one benchmark per gateway cell and needs
  // to start a run, observe when it finishes, and read the result it produced. It reuses the
  // same BenchmarkRunner the panel/route flow uses rather than duplicating the frame-probe logic.
  public bool BenchmarkRunning => _benchmarkRunner.IsRunning;

  public ClientTelemetrySample LatestClientSample => _latestClientSample;

  public bool StartBenchmark() {
    if (_benchmarkRunner.IsRunning) {
      return false;
    }

    _latestBenchmarkResult = null;
    _benchmarkRunner.Toggle();
    WriteEvent("benchmark_start", "NetworkSense benchmark started (matrix check-in).");
    return true;
  }

  public void CancelBenchmark() {
    if (_benchmarkRunner.IsRunning) {
      _benchmarkRunner.Toggle();
      WriteEvent("benchmark_cancel", "NetworkSense benchmark cancelled (matrix check-in).");
    }
  }

  // Null until the most recently started run completes. StartBenchmark clears it first.
  public BenchmarkResult ConsumeLatestBenchmarkResult() {
    BenchmarkResult result = _latestBenchmarkResult;
    _latestBenchmarkResult = null;
    return result;
  }

  public NetworkSenseSnapshot GetSnapshot() {
    return new() {
        SessionId = _sessionId,
        RoleLabel = GetRoleLabel(),
        HudVisible = _hudVisible,
        HudDetailLevel = _hudDetailLevel,
        Mode = _mode,
        BenchmarkRunning = _benchmarkRunner.IsRunning,
        ServerPulseState = GetServerPulseState(),
        ClientSample = _latestClientSample,
        ServerPulse = _latestServerPulse,
        Scores = _latestScores,
        LatestExportPath = _latestExportPath,
        ExportDirectory = _exportRoot,
        RecentEvents = _recentEvents.ToArray(),
        Raven = _ravenState
    };
  }

  public string ExportSession() {
    Directory.CreateDirectory(_exportRoot);
    string exportPath = Path.Combine(_exportRoot, $"network-sense-session-{_sessionId}.json");
    Dictionary<string, object> payload = new() {
        ["timestamp_utc"] = DateTime.UtcNow.ToString("o"),
        ["session_id"] = _sessionId,
        ["hud_visible"] = _hudVisible,
        ["hud_detail"] = _hudDetailLevel.ToString(),
        ["mode"] = _mode.ToString(),
        ["benchmark_running"] = _benchmarkRunner.IsRunning,
        ["server_pulse_state"] = GetServerPulseState(),
        ["latest_client_sample"] = _latestClientSample?.ToDictionary(),
        ["latest_server_pulse"] = _latestServerPulse?.ToDictionary(),
        ["latest_scores"] = _latestScores == null ? null : new Dictionary<string, object> {
            ["network_quality"] = _latestScores.NetworkQuality,
            ["frame_stability"] = _latestScores.FrameStability,
            ["cpu_headroom"] = _latestScores.CpuHeadroom,
            ["owner_score"] = _latestScores.OwnerScore,
            ["combat_readiness"] = _latestScores.CombatReadiness,
            ["merge_risk"] = _latestScores.MergeRisk,
            ["low_impact_recommended"] = _latestScores.LowImpactRecommended,
            ["connection_label"] = _latestScores.ConnectionLabel,
            ["owner_fit_label"] = _latestScores.OwnerFitLabel,
            ["pressure_label"] = _latestScores.PressureLabel,
            ["confidence_label"] = _latestScores.ConfidenceLabel
        }
    };

    File.WriteAllText(exportPath, JsonLineSerializer.Serialize(payload));
    _latestExportPath = exportPath;
    WriteEvent("session_export", exportPath);
    string message = $"NetworkSense session exported: {exportPath}";
    MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
    return message;
  }

  public string ReloadConfig() {
    ComfyNetworkSense.Instance.Config.Reload();
    string message = "NetworkSense config reloaded.";
    WriteEvent("config_reload", message);
    MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
    return message;
  }

  public string OpenExportFolder() {
    Directory.CreateDirectory(_exportRoot);
    Application.OpenURL("file://" + _exportRoot.Replace('\\', '/'));
    string message = $"NetworkSense export folder opened: {_exportRoot}";
    WriteEvent("open_export_folder", message);
    return message;
  }

  public string RecordDevNote(string note) {
    WriteEvent("dev_note", note);
    string message = $"NetworkSense note recorded: {note}";
    MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
    return message;
  }

  public string RecordDevMarker(string label) {
    WriteEvent("dev_marker", label);
    string message = $"NetworkSense marker recorded: {label}";
    MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
    return message;
  }

  public void RecordLumberjacksBridgeProbe(IDictionary<string, object> values) {
    Dictionary<string, object> row = new(values) {
        ["timestamp_utc"] = DateTime.UtcNow.ToString("o"),
        ["session_id"] = _sessionId
    };
    _logWriter.Write("lumberjacks-bridge-probes.jsonl", row);

    string status = values.TryGetValue("status", out object value) ? Convert.ToString(value) : "unknown";
    WriteEvent("lumberjacks_bridge_probe", $"Lumberjacks bridge probe {status}");
  }

  public void RecordLumberjacksProjection(IDictionary<string, object> values) {
    Dictionary<string, object> row = new(values) {
        ["timestamp_utc"] = DateTime.UtcNow.ToString("o"),
        ["session_id"] = _sessionId
    };
    _logWriter.Write("lumberjacks-projection.jsonl", row);

    string eventName = values.TryGetValue("event", out object eventValue) ? Convert.ToString(eventValue) : "status";
    string status = values.TryGetValue("status", out object statusValue) ? Convert.ToString(statusValue) : "unknown";
    WriteEvent("lumberjacks_projection", $"Lumberjacks projection {eventName}: {status}");
  }

  public void RecordLumberjacksShadow(IDictionary<string, object> values) {
    Dictionary<string, object> row = new(values) {
        ["timestamp_utc"] = DateTime.UtcNow.ToString("o"),
        ["session_id"] = _sessionId
    };
    _logWriter.Write("lumberjacks-shadow.jsonl", row);

    string eventName = values.TryGetValue("event", out object eventValue) ? Convert.ToString(eventValue) : "status";
    string status = values.TryGetValue("status", out object statusValue) ? Convert.ToString(statusValue) : "unknown";
    WriteEvent("lumberjacks_shadow", $"Lumberjacks shadow {eventName}: {status}");
  }

  public void RecordLumberjacksPriority(IDictionary<string, object> values) {
    Dictionary<string, object> row = new(values) {
        ["timestamp_utc"] = DateTime.UtcNow.ToString("o"),
        ["session_id"] = _sessionId
    };
    _logWriter.Write("priority-load.jsonl", row);
    _priorityMirrorRunner?.ObservePriorityRow(row);

    string eventName = values.TryGetValue("event", out object eventValue) ? Convert.ToString(eventValue) : "status";
    if (string.Equals(eventName, "object", StringComparison.OrdinalIgnoreCase)) {
      return;
    }

    string status = values.TryGetValue("status", out object statusValue) ? Convert.ToString(statusValue) : "unknown";
    WriteEvent("lumberjacks_priority", $"Lumberjacks priority {eventName}: {status}");
  }

  public void RecordNetcodeProbe(IDictionary<string, object> values) {
    Dictionary<string, object> row = new(values) {
        ["timestamp_utc"] = DateTime.UtcNow.ToString("o"),
        ["session_id"] = _sessionId
    };
    _logWriter.Write("netcode-probe.jsonl", row);

    string eventName = values.TryGetValue("event", out object eventValue) ? Convert.ToString(eventValue) : "status";
    // Per-ZDO rows are the bulk of the stream; only surface lifecycle events to the timeline.
    if (string.Equals(eventName, "zdo", StringComparison.OrdinalIgnoreCase)) {
      return;
    }

    string status = values.TryGetValue("status", out object statusValue) ? Convert.ToString(statusValue) : "unknown";
    WriteEvent("netcode_probe", $"Netcode probe {eventName}: {status}");
  }

  public void RecordOwnershipChurn(IDictionary<string, object> values) {
    Dictionary<string, object> row = new(values) {
        ["timestamp_utc"] = DateTime.UtcNow.ToString("o"),
        ["session_id"] = _sessionId
    };
    _logWriter.Write("ownership-churn.jsonl", row);

    string eventName = values.TryGetValue("event", out object eventValue) ? Convert.ToString(eventValue) : "status";
    // Per-change ownership rows are the bulk of the stream; only surface lifecycle events.
    if (string.Equals(eventName, "ownership", StringComparison.OrdinalIgnoreCase)) {
      return;
    }

    string status = values.TryGetValue("status", out object statusValue) ? Convert.ToString(statusValue) : "unknown";
    WriteEvent("ownership_observe", $"Ownership observe {eventName}: {status}");
  }

  public void RecordOwnershipPin(IDictionary<string, object> values) {
    Dictionary<string, object> row = new(values) {
        ["timestamp_utc"] = DateTime.UtcNow.ToString("o"),
        ["session_id"] = _sessionId
    };
    _logWriter.Write("ownership-pin.jsonl", row);

    string eventName = values.TryGetValue("event", out object eventValue) ? Convert.ToString(eventValue) : "status";
    // Per-hold rows are the bulk of the stream; only surface lifecycle events to the timeline.
    if (string.Equals(eventName, "pin_hold", StringComparison.OrdinalIgnoreCase)
        || string.Equals(eventName, "pin_capture", StringComparison.OrdinalIgnoreCase)) {
      return;
    }

    string status = values.TryGetValue("status", out object statusValue) ? Convert.ToString(statusValue) : "unknown";
    WriteEvent("ownership_pin", $"Ownership pin {eventName}: {status}");
  }

  public void RecordZdoRedirect(IDictionary<string, object> values) {
    Dictionary<string, object> row = new(values) {
        ["timestamp_utc"] = DateTime.UtcNow.ToString("o"),
        ["session_id"] = _sessionId
    };
    _logWriter.Write("redirect-send.jsonl", row);

    string eventName = values.TryGetValue("event", out object eventValue) ? Convert.ToString(eventValue) : "status";
    // Per-suppression rows are the bulk of the stream; only surface lifecycle events.
    if (string.Equals(eventName, "redirect", StringComparison.OrdinalIgnoreCase)) {
      return;
    }

    string status = values.TryGetValue("status", out object statusValue) ? Convert.ToString(statusValue) : "unknown";
    WriteEvent("zdo_redirect", $"ZDO redirect {eventName}: {status}");
  }

  public void SetLumberjacksReplacementTelemetryProvider(Func<Dictionary<string, object>> provider) {
    _replacementTelemetryProvider = provider;
  }

  public Dictionary<string, object> GetLumberjacksTelemetryHeartbeat() {
    int peers = 0;
    if (ZNet.instance != null && ZNet.instance.IsServer()) {
      peers = ZNet.instance.GetPeers()?.Count ?? 0;
    }

    Dictionary<string, object> payload = new() {
        ["instance_id"] = _sessionId,
        ["mod_version"] = ComfyNetworkSense.PluginVersion,
        ["timestamp_utc"] = DateTime.UtcNow.ToString("o"),
        ["server_role"] = "dedicated",
        ["server_state"] = ZNet.instance == null ? "starting" : "ready",
        ["cutover_mode"] = EffectiveCutoverMode(),
        ["enrollment_manifest_id"] = EffectiveEnrollmentManifestId(),
        ["coverage_total"] = null,
        ["coverage_lumberjacks"] = null,
        ["coverage_native_only"] = null,
        ["native_fallbacks"] = null,
        ["zdo_probe_running"] = null,
        ["zdo_probe_recv_rows"] = null,
        ["zdo_probe_send_rows"] = null,
        ["zdo_probe_recv_calls"] = null,
        ["zdo_probe_create_sync_calls"] = null,
        ["peer_count"] = peers,
        ["handshake_accepted"] = null,
        ["handshake_rejected"] = null,
        ["redirect_suppressed"] = null,
        ["redirect_received"] = null,
        ["redirect_missing"] = null,
        ["redirect_duplicates"] = null,
        ["injection_applied"] = null,
        ["injection_rendered"] = null,
        ["injection_rejected"] = null
    };

    Dictionary<string, object> replacement = _replacementTelemetryProvider?.Invoke();
    if (replacement != null) {
      foreach (KeyValuePair<string, object> pair in replacement) {
        payload[pair.Key] = pair.Value;
      }
    }
    return payload;
  }

  public static string EffectiveCutoverMode() {
    string configured = PluginConfig.LumberjacksCutoverMode?.Value ?? "native";
    string environment = Environment.GetEnvironmentVariable("COMFY_LUMBERJACKS_CUTOVER_MODE");
    return string.IsNullOrWhiteSpace(environment) ? configured : environment.Trim().ToLowerInvariant();
  }

  static string EffectiveEnrollmentManifestId() {
    string configured = PluginConfig.LumberjacksEnrollmentManifestId?.Value ?? string.Empty;
    string environment = Environment.GetEnvironmentVariable("COMFY_LUMBERJACKS_ENROLLMENT_MANIFEST_ID");
    return string.IsNullOrWhiteSpace(environment) ? configured : environment.Trim();
  }

  public void RecordZdoInjection(IDictionary<string, object> values) {
    Dictionary<string, object> row = new(values) {
        ["timestamp_utc"] = DateTime.UtcNow.ToString("o"),
        ["session_id"] = _sessionId
    };
    _logWriter.Write("injection-apply.jsonl", row);

    string eventName = values.TryGetValue("event", out object eventValue)
        ? Convert.ToString(eventValue) : "status";
    WriteEvent("zdo_injection", "ZDO injection " + eventName);
  }

  public void SetLumberjacksPriorityMirror(LumberjacksPriorityMirrorRunner runner) {
    _priorityMirrorRunner = runner;
  }

  public void RecordLumberjacksPriorityMirror(IDictionary<string, object> values) {
    Dictionary<string, object> row = new(values) {
        ["timestamp_utc"] = DateTime.UtcNow.ToString("o"),
        ["session_id"] = _sessionId
    };
    _logWriter.Write("priority-mirror.jsonl", row);

    string eventName = values.TryGetValue("event", out object eventValue) ? Convert.ToString(eventValue) : "status";
    string status = values.TryGetValue("running", out object runningValue) ? Convert.ToString(runningValue) : "unknown";
    WriteEvent("lumberjacks_priority_mirror", $"Lumberjacks priority mirror {eventName}: {status}");
  }

  public void RecordLumberjacksPriorityManifestListen(IDictionary<string, object> values) {
    Dictionary<string, object> row = new(values) {
        ["timestamp_utc"] = DateTime.UtcNow.ToString("o"),
        ["session_id"] = _sessionId
    };
    _logWriter.Write("priority-manifest-listen.jsonl", row);

    string eventName = values.TryGetValue("event", out object eventValue) ? Convert.ToString(eventValue) : "status";
    string manifestId = values.TryGetValue("manifest_id", out object manifestValue) ? Convert.ToString(manifestValue) : "";
    WriteEvent("lumberjacks_priority_manifest_listen", $"Lumberjacks priority manifest listener {eventName}: {manifestId}");
  }

  public void StartRavenRequest(string requestKind) {
    if (_ravenState.IsBusy) {
      return;
    }

    _ravenState.IsBusy = true;
    _ravenState.Status = "Sending raven";
    _ravenState.LastRequest = requestKind;
    _ravenState.LastError = string.Empty;
    _ravenState.LastUpdatedUtc = DateTime.UtcNow.ToString("o");

    _ = Task.Run(async () => {
      string endpoint = RequestEndpoint(requestKind);
      string responseText = string.Empty;
      string error = string.Empty;

      try {
        HttpWebRequest request = (HttpWebRequest) WebRequest.Create(endpoint);
        request.Method = "GET";
        request.Timeout = 5000;
        request.ReadWriteTimeout = 5000;
        request.Headers.Add("X-Comfy-Key", "valheim-mod-local");

        using WebResponse response = await request.GetResponseAsync().ConfigureAwait(false);
        using Stream stream = response.GetResponseStream();
        using StreamReader reader = new(stream);
        responseText = await reader.ReadToEndAsync().ConfigureAwait(false);
      } catch (Exception exception) {
        error = $"{exception.GetType().Name}: {exception.Message}";
      }

      ApplyRavenResponse(requestKind, responseText, error);
    });
  }

  public void StartApplyProfile(string profile) {
    if (_ravenState.IsBusy) {
      return;
    }

    _ravenState.IsBusy = true;
    _ravenState.Status = "Sending raven";
    _ravenState.LastRequest = $"apply {profile}";
    _ravenState.LastError = string.Empty;
    _ravenState.LastUpdatedUtc = DateTime.UtcNow.ToString("o");

    _ = Task.Run(async () => {
      string responseText = string.Empty;
      string error = string.Empty;

      try {
        HttpWebRequest request = (HttpWebRequest) WebRequest.Create("http://127.0.0.1:8720/valheim/apply-profile");
        request.Method = "POST";
        request.ContentType = "application/json";
        request.Timeout = 5000;
        request.ReadWriteTimeout = 5000;
        request.Headers.Add("X-Comfy-Key", "valheim-mod-local");

        string body = $"{{\"profile\":\"{profile}\"}}";
        using (Stream requestStream = await request.GetRequestStreamAsync().ConfigureAwait(false)) {
          using StreamWriter writer = new(requestStream);
          await writer.WriteAsync(body).ConfigureAwait(false);
        }

        using WebResponse response = await request.GetResponseAsync().ConfigureAwait(false);
        using Stream stream = response.GetResponseStream();
        using StreamReader reader = new(stream);
        responseText = await reader.ReadToEndAsync().ConfigureAwait(false);
      } catch (Exception exception) {
        error = $"{exception.GetType().Name}: {exception.Message}";
      }

      ApplyRavenResponse($"apply {profile}", responseText, error);
    });
  }

  void ApplyRavenResponse(string requestKind, string responseText, string error) {
    _ravenState.IsBusy = false;
    _ravenState.Status = string.IsNullOrWhiteSpace(error) ? "Raven returned" : "No raven tower found";
    _ravenState.IsOnline = string.IsNullOrWhiteSpace(error);
    _ravenState.LastRequest = requestKind;
    _ravenState.LastResponse = FormatRavenResponse(requestKind, responseText, out string[] bullets, out string profile);
    _ravenState.LastBullets = bullets;
    _ravenState.RecommendedProfile = profile;
    _ravenState.LastError = error ?? string.Empty;
    _ravenState.LastUpdatedUtc = DateTime.UtcNow.ToString("o");

    string message = string.IsNullOrWhiteSpace(error)
        ? $"NetworkSense raven returned: {requestKind}"
        : $"NetworkSense raven failed: {error}";
    ComfyNetworkSense.EnqueueMainThreadMessage(message);
    ComfyNetworkSense.LogInfo(message);
    WriteEvent("raven_response", message);
  }

  static string RequestEndpoint(string requestKind) {
    return requestKind switch {
      "health" => "http://127.0.0.1:8720/healthz",
      "next-test" => "http://127.0.0.1:8720/valheim/next-test?sample_count=30",
      "config-suggestion" => "http://127.0.0.1:8720/valheim/config-suggestion?sample_count=30",
      _ => "http://127.0.0.1:8720/valheim/report?sample_count=30"
    };
  }

  static string FormatRavenResponse(string requestKind, string json, out string[] bullets, out string recommendedProfile) {
    List<string> result = [];
    recommendedProfile = string.Empty;

    if (string.IsNullOrWhiteSpace(json)) {
      bullets = ["Empty raven response."];
      return bullets[0];
    }

    if (requestKind == "health") {
      result.Add(json.Contains("\"ok\":true") ? "Raven tower is online." : "Raven tower replied, but health is unclear.");
    } else if (requestKind == "next-test") {
      foreach (string suggestion in ExtractJsonStringArray(json, "suggestions")) {
        result.Add(suggestion);
      }
    } else if (requestKind == "config-suggestion") {
      foreach (string reason in ExtractJsonValues(json, "\"reason\":\"", "\"")) {
        result.Add(reason);
      }

      if (json.Contains("serverPulseIntervalSeconds") && json.Contains("\"2\"")) {
        recommendedProfile = "multiplayer_pulse";
      } else if (json.Contains("dense")) {
        recommendedProfile = "dense_base";
      } else {
        recommendedProfile = "default_dev";
      }

      result.Insert(0, $"Suggested profile: {recommendedProfile}");
    } else if (requestKind.StartsWith("apply ")) {
      result.Add(json.Contains("\"ok\":true")
          ? "Config profile applied. Reload config in-game to activate it."
          : "Config profile apply failed. Check gateway log.");
    } else {
      result.AddRange(FormatReportSummary(json));
    }

    if (result.Count <= 0) {
      string compact = json.Replace("\r", string.Empty).Replace("\n", " ");
      result.Add(compact.Length > 260 ? compact.Substring(0, 260) + "..." : compact);
    }

    bullets = result.ToArray();
    return string.Join("\n", result);
  }

  static IEnumerable<string> FormatReportSummary(string json) {
    List<string> result = [];
    string fps = ExtractJsonNumber(json, "\"fps\":");
    string p95 = ExtractJsonNumber(json, "\"frame_time_p95_ms\":");
    string cpu = ExtractJsonNumber(json, "\"cpu_bound_estimate\":");
    string pulse = json.Contains("\"server_pulse_available\":true") ? "server pulse data is available" : "no server pulse data in this local view";

    if (!string.IsNullOrWhiteSpace(fps)) {
      result.Add($"Frame signal: {fps} fps, p95 {p95} ms, CPU pressure {cpu}.");
    }

    result.Add($"Server signal: {pulse}.");
    result.Add("Use a two-player session to validate host/client pulse behavior.");
    return result;
  }

  static string[] ExtractJsonStringArray(string json, string propertyName) {
    string marker = $"\"{propertyName}\":[";
    int start = json.IndexOf(marker, StringComparison.OrdinalIgnoreCase);

    if (start < 0) {
      return [];
    }

    start += marker.Length;
    int end = json.IndexOf(']', start);

    if (end < 0) {
      return [];
    }

    List<string> values = [];
    foreach (string value in ExtractJsonValues(json.Substring(start, end - start), "\"", "\"")) {
      values.Add(value);
    }

    return values.ToArray();
  }

  static IEnumerable<string> ExtractJsonValues(string text, string prefix, string suffix) {
    int index = 0;
    while (index < text.Length) {
      int start = text.IndexOf(prefix, index, StringComparison.OrdinalIgnoreCase);
      if (start < 0) {
        yield break;
      }
      start += prefix.Length;
      int end = text.IndexOf(suffix, start, StringComparison.OrdinalIgnoreCase);
      if (end < 0) {
        yield break;
      }
      yield return text.Substring(start, end - start).Replace("\\\"", "\"");
      index = end + suffix.Length;
    }
  }

  static string ExtractJsonNumber(string json, string marker) {
    int start = json.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
    if (start < 0) {
      return string.Empty;
    }

    start += marker.Length;
    int end = start;
    while (end < json.Length && "0123456789.-".IndexOf(json[end]) >= 0) {
      end++;
    }

    return json.Substring(start, end - start);
  }

  static string ModeLabel(NetworkSenseMode mode) {
    return mode switch {
      NetworkSenseMode.GroupCombat => "Group Combat",
      NetworkSenseMode.Town => "Town",
      NetworkSenseMode.Combat => "Combat",
      _ => "Solo"
    };
  }

  void HandleShortcuts() {
    if (PluginConfig.ToggleHudShortcut.Value.IsDown()) {
      ToggleHud();
    }

    if (PluginConfig.CycleHudDetailShortcut.Value.IsDown()) {
      CycleHudDetail();
    }

    if (PluginConfig.CycleModeShortcut.Value.IsDown()) {
      CycleMode();
    }

    if (PluginConfig.ToggleBenchmarkShortcut.Value.IsDown()) {
      ToggleBenchmark();
    }
  }

  void RegisterServerPulseRpc() {
    if (ZRoutedRpc.instance == null || _registeredRoutedRpc == ZRoutedRpc.instance) {
      return;
    }

    ZRoutedRpc.instance.Register(
        ServerPulseBroadcaster.ServerPulseRpc,
        new Action<long, ZPackage>(ComfyNetworkSense.HandleServerPulse));

    _registeredRoutedRpc = ZRoutedRpc.instance;
    ComfyNetworkSense.LogInfo($"Registered routed RPC: {ServerPulseBroadcaster.ServerPulseRpc}");
  }

  string GetServerPulseState() {
    if (_latestServerPulse != null) {
      return "received";
    }

    if (ZNet.instance == null) {
      return "waiting for network";
    }

    if (ZNet.instance.GetServerRPC() == null) {
      return "local session, no remote server pulse";
    }

    if (ZRoutedRpc.instance == null) {
      return "waiting for routed RPC";
    }

    return "waiting for server view";
  }

  void WriteEvent(string eventName, string message) {
    NetworkSenseEventRow row = new() {
        TimestampUtc = DateTime.UtcNow.ToString("o"),
        EventName = eventName,
        Message = message,
        Source = eventName.StartsWith("raven", StringComparison.OrdinalIgnoreCase) ? "raven" : "system"
    };

    _recentEvents.Add(row);
    if (_recentEvents.Count > MaxRecentEvents) {
      _recentEvents.RemoveAt(0);
    }

    Dictionary<string, object> payload = new() {
        ["timestamp_utc"] = row.TimestampUtc,
        ["session_id"] = _sessionId,
        ["event_name"] = eventName,
        ["message"] = message,
        ["mode"] = _mode.ToString(),
        ["region_id"] = _latestClientSample?.RegionId ?? string.Empty,
        ["player_id"] = _latestClientSample?.PlayerId ?? string.Empty,
        ["sample_source"] = "event_marker",
        ["build_version"] = ComfyNetworkSense.PluginVersion
    };

    _logWriter.Write("event-timeline.jsonl", payload);
  }

  string GetRoleLabel() {
    if (ZNet.instance == null) {
      return "Loading";
    }

    if (ZNet.instance.IsDedicated()) {
      return "Dedicated";
    }

    if (ZNet.instance.IsServer() && ZNet.instance.GetPeers().Count > 0) {
      return "Host";
    }

    if (ZNet.instance.IsServer()) {
      return "Local Solo";
    }

    if (ZNet.instance.GetServerRPC() != null) {
      return "Remote Client";
    }

    return "Client";
  }

  public void Dispose() {
    _lumberjacksTelemetryHeartbeatRunner.Dispose();
    _perfProbe?.Dispose();
    _logWriter?.Dispose();
  }
}
