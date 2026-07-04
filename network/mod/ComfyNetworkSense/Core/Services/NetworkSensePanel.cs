namespace ComfyNetworkSense;

using System;
using System.Collections.Generic;

using UnityEngine;

public sealed class NetworkSensePanel {
  enum PanelTab {
    Debug,
    Signals,
    Raven
  }

  Rect _window = new(90.0f, 90.0f, 456.0f, 640.0f);
  Vector2 _scroll;
  PanelTab _tab = PanelTab.Debug;
  string _noteText = string.Empty;
  bool _stylesReady;
  GUIStyle _windowStyle, _headerStyle, _titleStyle, _subtitleStyle, _tabStyle, _sectionStyle, _labelStyle, _mutedStyle,
      _buttonStyle, _primaryButtonStyle, _activeTabStyle, _insetStyle, _glassStyle, _glassTextStyle;

  public bool IsOpen { get; private set; }

  public void Toggle() {
    IsOpen = !IsOpen;
  }

  public void Open(string tab = null) {
    IsOpen = true;
    SelectTab(tab);
  }

  public void Close() {
    IsOpen = false;
  }

  public void SelectTab(string tab) {
    if (string.IsNullOrWhiteSpace(tab)) {
      return;
    }

    string normalized = tab.Trim();
    if (string.Equals(normalized, "ward", StringComparison.OrdinalIgnoreCase)) {
      normalized = "debug";
    }

    if (Enum.TryParse(normalized, ignoreCase: true, out PanelTab parsed)) {
      _tab = parsed;
    }
  }

  public void Draw(
      NetworkSenseSnapshot snapshot,
      Action toggleHud,
      Action cycleDetail,
      Action startBenchmark,
      Action exportSession,
      Action<NetworkSenseMode> setMode,
      Action reloadConfig,
      Action openExportFolder,
      Action<string> recordNote,
      Action<string> recordMarker,
      Action<string> ravenRequest,
      Action<string> applyProfile) {
    if (!IsOpen || snapshot == null) {
      return;
    }

    EnsureStyles();
    _window = GUILayout.Window(
        481921,
        _window,
        _ => DrawWindow(snapshot, toggleHud, cycleDetail, startBenchmark, exportSession, setMode, reloadConfig, openExportFolder, recordNote, recordMarker, ravenRequest, applyProfile),
        GUIContent.none,
        _windowStyle);
  }

  void DrawWindow(
      NetworkSenseSnapshot snapshot,
      Action toggleHud,
      Action cycleDetail,
      Action startBenchmark,
      Action exportSession,
      Action<NetworkSenseMode> setMode,
      Action reloadConfig,
      Action openExportFolder,
      Action<string> recordNote,
      Action<string> recordMarker,
      Action<string> ravenRequest,
      Action<string> applyProfile) {
    GUILayout.BeginVertical();

    DrawHeader(snapshot);
    Divider();
    DrawTabs();
    Divider();

    _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));

    switch (_tab) {
      case PanelTab.Signals:
        DrawSignals(snapshot, startBenchmark);
        break;
      case PanelTab.Raven:
        DrawRaven(snapshot, exportSession, reloadConfig, openExportFolder, recordNote, recordMarker, ravenRequest, applyProfile);
        break;
      default:
        DrawDebug(snapshot, openExportFolder);
        break;
    }

    GUILayout.EndScrollView();
    GUILayout.Space(6.0f);
    Divider();
    DrawBottomControls(snapshot, toggleHud, cycleDetail, exportSession, setMode);
    Divider();
    GUILayout.Label($"<color={NetworkSensePanelTheme.Faint}>NetworkSense Debug - Esc closes - drag the title bar</color>", _mutedStyle);

    GUILayout.EndVertical();
    GUI.DragWindow(new Rect(0.0f, 0.0f, 10000.0f, 42.0f));
  }

  void DrawHeader(NetworkSenseSnapshot snapshot) {
    GUILayout.BeginHorizontal(_headerStyle, GUILayout.Height(46.0f));
    GUILayout.Label($"<color={NetworkSensePanelTheme.Accent}>[N]</color>", _titleStyle, GUILayout.Width(36.0f));
    GUILayout.BeginVertical();
    GUILayout.Label($"<color={NetworkSensePanelTheme.Text}>NETWORKSENSE</color>", _titleStyle);
    GUILayout.Label($"<color={NetworkSensePanelTheme.Faint}>runtime overlay - debug drawer - raven bridge</color>", _subtitleStyle);
    GUILayout.EndVertical();
    GUILayout.FlexibleSpace();
    GUILayout.Label($"<color={NetworkSensePanelTheme.Good}>{Fallback(snapshot.RoleLabel)}</color>", _labelStyle, GUILayout.ExpandWidth(false));
    GUILayout.Space(8.0f);
    if (GUILayout.Button("x", _buttonStyle, GUILayout.Width(32.0f), GUILayout.Height(26.0f))) {
      Close();
    }
    GUILayout.EndHorizontal();
  }

  void DrawBottomControls(
      NetworkSenseSnapshot snapshot,
      Action toggleHud,
      Action cycleDetail,
      Action exportSession,
      Action<NetworkSenseMode> setMode) {
    GUILayout.BeginHorizontal();
    ModeButton(snapshot, NetworkSenseMode.Solo, "Solo", setMode);
    ModeButton(snapshot, NetworkSenseMode.Combat, "Combat", setMode);
    ModeButton(snapshot, NetworkSenseMode.GroupCombat, "Group", setMode);
    ModeButton(snapshot, NetworkSenseMode.Town, "Town", setMode);
    GUILayout.EndHorizontal();

    GUILayout.BeginHorizontal();
    string hudColor = snapshot.HudVisible ? NetworkSensePanelTheme.Good : NetworkSensePanelTheme.Faint;
    if (GUILayout.Button($"<color={hudColor}>HUD {(snapshot.HudVisible ? "ON" : "OFF")}</color>", _buttonStyle, GUILayout.Height(29.0f))) {
      toggleHud();
    }
    if (GUILayout.Button($"Detail {DetailNumber(snapshot.HudDetailLevel)}/3", _buttonStyle, GUILayout.Height(29.0f))) {
      cycleDetail();
    }
    if (GUILayout.Button("Export", _buttonStyle, GUILayout.Height(29.0f))) {
      exportSession();
    }
    GUILayout.EndHorizontal();
  }

  void ModeButton(NetworkSenseSnapshot snapshot, NetworkSenseMode mode, string label, Action<NetworkSenseMode> setMode) {
    bool active = snapshot.Mode == mode;
    string color = active ? NetworkSensePanelTheme.Good : NetworkSensePanelTheme.Dim;
    string prefix = active ? NetworkSensePanelTheme.MarkGood + " " : string.Empty;
    if (GUILayout.Button($"<color={color}>{prefix}{label}</color>", _buttonStyle, GUILayout.Height(28.0f))) {
      setMode(mode);
    }
  }

  void DrawTabs() {
    GUILayout.BeginHorizontal();
    TabButton(PanelTab.Debug, "Debug");
    TabButton(PanelTab.Signals, "Signals");
    TabButton(PanelTab.Raven, "Raven");
    GUILayout.FlexibleSpace();
    GUILayout.EndHorizontal();
  }

  void TabButton(PanelTab tab, string label) {
    bool active = _tab == tab;
    string color = active ? NetworkSensePanelTheme.Text : NetworkSensePanelTheme.Dim;
    string marker = active ? NetworkSensePanelTheme.MarkBar + " " : string.Empty;
    GUIStyle style = active ? _activeTabStyle : _tabStyle;
    if (GUILayout.Button($"<color={color}>{marker}{label.ToUpperInvariant()}</color>", style, GUILayout.Width(112.0f), GUILayout.Height(32.0f))) {
      _tab = tab;
    }
  }

  void DrawDebug(
      NetworkSenseSnapshot snapshot,
      Action openExportFolder) {
    ClientTelemetrySample sample = snapshot.ClientSample;
    ScoreSnapshot scores = snapshot.Scores;

    Section("Live Read");
    Inset(new List<string> {
        HeroLine(snapshot),
        $"Mode {ModeLabel(snapshot.Mode)}: {ModeDescription(snapshot.Mode)}",
        $"Role {Fallback(snapshot.RoleLabel)} | Server {Fallback(snapshot.ServerPulseState)}"
    });

    if (sample != null) {
      Section("Range & Reception");
      DrawReceptionBar("Frame", 1.0f - Mathf.Clamp01((sample.FrameTimeP95Ms - 16.0f) / 50.0f), $"{sample.FrameTimeP95Ms:0.0} ms");
      DrawReceptionBar("Network", 1.0f - Mathf.Clamp01((sample.RttMs + sample.JitterMs) / 250.0f), $"{sample.RttMs:0}/{sample.JitterMs:0} ms");
      DrawReceptionBar("Area", 1.0f - Mathf.Clamp01((sample.NearbyBuildPieces / 250.0f) + (sample.NearbyEntities / 50.0f)), $"{sample.NearbyBuildPieces}/{sample.NearbyEntities}");
    }

    Section("Signals");
    Inset(new List<string> {
        sample == null ? "Waiting for first telemetry sample." : $"FPS {sample.Fps:0} | p95 {sample.FrameTimeP95Ms:0.0} ms | CPU {sample.CpuBoundEstimate:0.00}",
        sample == null ? "Network signals pending." : $"Ping {sample.RttMs:0} ms | jitter {sample.JitterMs:0} ms | zone {sample.RegionId}",
        sample == null
            ? "Area pressure pending."
            : $"Area players {sample.NearbyPlayers} | entities {sample.NearbyEntities} | pieces {sample.NearbyBuildPieces} | danger {(sample.DangerNearby ? "yes" : "no")}",
        $"Connection {scores?.ConnectionLabel ?? "n/a"} | Pressure {scores?.PressureLabel ?? "n/a"} | Owner {scores?.OwnerFitLabel ?? "n/a"}"
    });

    Section("Session");
    Inset(new List<string> {
        $"Session {Fallback(snapshot.SessionId)}",
        string.IsNullOrWhiteSpace(snapshot.LatestExportPath) ? "No export yet." : "Export ready. Use Open Export Folder if needed."
    });
    if (GUILayout.Button("Open Export Folder", _buttonStyle, GUILayout.Height(28.0f))) {
      openExportFolder();
    }
  }

  void DrawSignals(NetworkSenseSnapshot snapshot, Action startBenchmark) {
    ClientTelemetrySample sample = snapshot.ClientSample;
    ServerPulseSnapshot pulse = snapshot.ServerPulse;
    ScoreSnapshot scores = snapshot.Scores;

    Section("Capture");
    GUILayout.BeginHorizontal();
    string benchmarkColor = snapshot.BenchmarkRunning ? NetworkSensePanelTheme.Warn : NetworkSensePanelTheme.Accent;
    if (GUILayout.Button($"<color={benchmarkColor}>{(snapshot.BenchmarkRunning ? "Stop Benchmark" : "Start Benchmark")}</color>", _primaryButtonStyle, GUILayout.Height(30.0f))) {
      startBenchmark();
    }
    GUILayout.EndHorizontal();

    Section("Raw Client Signals");
    Inset(sample == null
        ? new List<string> { "Waiting for client sample." }
        : new List<string> {
            $"fps={sample.Fps:0.###} frame_ms={sample.FrameTimeMs:0.###} p95_ms={sample.FrameTimeP95Ms:0.###}",
            $"rtt_ms={sample.RttMs:0.###} jitter_ms={sample.JitterMs:0.###}",
            $"bytes in/out={sample.BytesInPerSec:0}/{sample.BytesOutPerSec:0} packets in/out={sample.PacketsInPerSec:0.0}/{sample.PacketsOutPerSec:0.0}",
            $"nearby players/entities/pieces={sample.NearbyPlayers}/{sample.NearbyEntities}/{sample.NearbyBuildPieces}",
        });

    Section("Scores");
    Inset(scores == null
        ? new List<string> { "Waiting for score snapshot." }
        : new List<string> {
            $"network={scores.NetworkQuality:0.000} frame={scores.FrameStability:0.000} cpu={scores.CpuHeadroom:0.000}",
            $"owner={scores.OwnerScore:0.000} combat={scores.CombatReadiness:0.000} mergeRisk={scores.MergeRisk:0.000}",
            $"lowImpactRecommended={scores.LowImpactRecommended} confidence={scores.ConfidenceLabel}",
        });

    Section("Server Pulse");
    Inset(pulse == null
        ? new List<string> { snapshot.ServerPulseState }
        : new List<string> {
            $"owner={Fallback(pulse.OwnerId)} reason={Fallback(pulse.OwnerReason)} confidence={pulse.OwnerConfidence:0.00}",
            $"pressure={pulse.RegionPressureScore:0.000} observers={pulse.RegionObserverCount} players={pulse.RegionPlayerCount}",
            $"bytes={pulse.BytesSentPerSec:0} messages={pulse.MessagesSentPerSec:0.0} heartbeat_gap_ms={pulse.HeartbeatGapMs:0}",
        });
  }

  void DrawRaven(
      NetworkSenseSnapshot snapshot,
      Action exportSession,
      Action reloadConfig,
      Action openExportFolder,
      Action<string> recordNote,
      Action<string> recordMarker,
      Action<string> ravenRequest,
      Action<string> applyProfile) {
    Section("Raven Tower");
    GUILayout.BeginHorizontal();
    DrawStatusDot(snapshot.Raven);
    GUILayout.Label($"<color={NetworkSensePanelTheme.Text}>{RavenStatusLine(snapshot.Raven)}</color>", _labelStyle);
    GUILayout.EndHorizontal();

    GUILayout.BeginHorizontal();
    if (GUILayout.Button("Check Tower", _buttonStyle, GUILayout.Height(30.0f))) {
      ravenRequest("health");
    }
    if (GUILayout.Button("Ask Recap", _primaryButtonStyle, GUILayout.Height(30.0f))) {
      ravenRequest("report");
    }
    GUILayout.EndHorizontal();
    GUILayout.BeginHorizontal();
    if (GUILayout.Button("Next Test", _buttonStyle, GUILayout.Height(30.0f))) {
      ravenRequest("next-test");
    }
    if (GUILayout.Button("Suggest Config", _buttonStyle, GUILayout.Height(30.0f))) {
      ravenRequest("config-suggestion");
    }
    GUILayout.EndHorizontal();

    Section("Last Raven");
    DrawLastRaven(snapshot);

    Section("Config Profiles");
    GUILayout.BeginHorizontal();
    ProfileButton("default_dev", applyProfile);
    ProfileButton("dense_base", applyProfile);
    ProfileButton("multiplayer_pulse", applyProfile);
    GUILayout.EndHorizontal();
    if (GUILayout.Button("Reload Config", _buttonStyle, GUILayout.Height(28.0f))) {
      reloadConfig();
    }

    Section("Test Setup");
    Inset(BuildChecklist(snapshot));
    GUILayout.BeginHorizontal();
    MarkerButton("Friend joined", recordMarker);
    MarkerButton("Entered base", recordMarker);
    GUILayout.EndHorizontal();
    GUILayout.BeginHorizontal();
    MarkerButton("Combat started", recordMarker);
    MarkerButton("Portal travel", recordMarker);
    GUILayout.EndHorizontal();

    GUILayout.BeginHorizontal();
    if (GUILayout.Button("Export", _buttonStyle, GUILayout.Height(28.0f))) {
      exportSession();
    }
    if (GUILayout.Button("Open Folder", _buttonStyle, GUILayout.Height(28.0f))) {
      openExportFolder();
    }
    GUILayout.EndHorizontal();

    Section("Dev Note");
    _noteText = GUILayout.TextField(_noteText, GUILayout.Height(28.0f));
    if (GUILayout.Button("Record Note", _buttonStyle, GUILayout.Height(30.0f)) && !string.IsNullOrWhiteSpace(_noteText)) {
      recordNote(_noteText);
      _noteText = string.Empty;
    }

    Section("Recent Events");
    Inset(BuildEventLines(snapshot.RecentEvents, maxLines: 4));

    Section("MCP");
    if (GUILayout.Button("Check MCP", _buttonStyle, GUILayout.Height(30.0f))) {
      ravenRequest("health");
    }
  }

  void DrawLastRaven(NetworkSenseSnapshot snapshot) {
    if (string.IsNullOrWhiteSpace(snapshot.Raven?.LastResponse) && string.IsNullOrWhiteSpace(snapshot.Raven?.LastError)) {
      Inset(new List<string> { "No recap yet. Ask the raven to read your session." });
    } else {
      GUILayout.BeginVertical(_glassStyle);
      if (!string.IsNullOrWhiteSpace(snapshot.Raven?.LastError)) {
        GUILayout.Label($"Error: {snapshot.Raven.LastError}", _glassTextStyle);
      }
      if (snapshot.Raven?.LastBullets != null && snapshot.Raven.LastBullets.Length > 0) {
        foreach (string bullet in snapshot.Raven.LastBullets) {
          GUILayout.Label("- " + bullet, _glassTextStyle);
        }
      } else if (!string.IsNullOrWhiteSpace(snapshot.Raven?.LastResponse)) {
        GUILayout.Label(snapshot.Raven.LastResponse, _glassTextStyle);
      }
      GUILayout.EndVertical();
    }
  }

  void DrawReceptionBar(string label, float value, string rawValue) {
    value = Mathf.Clamp01(value);
    GUILayout.BeginVertical(_insetStyle);
    GUILayout.BeginHorizontal();
    GUILayout.Label($"<color={NetworkSensePanelTheme.Text}>{label}</color>", _labelStyle);
    GUILayout.FlexibleSpace();
    GUILayout.Label($"<color={NetworkSensePanelTheme.Accent}>{rawValue}</color>", _labelStyle, GUILayout.Width(92.0f));
    GUILayout.EndHorizontal();
    Rect rect = GUILayoutUtility.GetRect(1.0f, 9.0f, GUILayout.ExpandWidth(true));
    GUI.DrawTexture(rect, NetworkSensePanelTheme.LineTex);
    Rect fill = rect;
    fill.width *= value;
    GUI.DrawTexture(fill, NetworkSensePanelTheme.HeaderTex);
    GUILayout.EndVertical();
  }

  void MarkerButton(string label, Action<string> recordMarker) {
    if (GUILayout.Button(label, _buttonStyle, GUILayout.Height(28.0f))) {
      recordMarker(label);
    }
  }

  void ProfileButton(string profile, Action<string> applyProfile) {
    if (GUILayout.Button(profile, _buttonStyle, GUILayout.Height(28.0f))) {
      applyProfile(profile);
    }
  }

  void DrawStatusDot(RavenState raven) {
    Texture2D texture = NetworkSensePanelTheme.StatusRustTex;
    if (raven != null && raven.IsBusy) {
      texture = NetworkSensePanelTheme.StatusAmberTex;
    } else if (raven != null && raven.IsOnline) {
      texture = NetworkSensePanelTheme.StatusGreenTex;
    }

    Rect rect = GUILayoutUtility.GetRect(12.0f, 12.0f, GUILayout.Width(12.0f), GUILayout.Height(12.0f));
    GUI.DrawTexture(rect, texture);
  }

  void Section(string label) {
    GUILayout.Space(8.0f);
    GUILayout.Label($"<color={NetworkSensePanelTheme.Accent}>{NetworkSensePanelTheme.MarkBar}</color> <b>{label}</b>", _sectionStyle);
  }

  void Inset(List<string> lines) {
    GUILayout.BeginVertical(_insetStyle);
    foreach (string line in lines) {
      if (!string.IsNullOrWhiteSpace(line)) {
        GUILayout.Label($"<color={NetworkSensePanelTheme.Text}>{line}</color>", _labelStyle);
      }
    }
    GUILayout.EndVertical();
  }

  void Divider() {
    Rect rect = GUILayoutUtility.GetRect(1.0f, 1.0f, GUILayout.ExpandWidth(true));
    GUI.DrawTexture(rect, NetworkSensePanelTheme.LineTex);
  }

  void EnsureStyles() {
    if (_stylesReady) {
      return;
    }

    _stylesReady = true;
    Font serif = NetworkSensePanelTheme.Serif;

    _windowStyle = new(GUI.skin.window) {
        padding = new RectOffset(12, 12, 8, 12)
    };
    _windowStyle.normal.background = NetworkSensePanelTheme.PanelTex;
    _windowStyle.onNormal.background = NetworkSensePanelTheme.PanelTex;

    _headerStyle = new GUIStyle { padding = new RectOffset(8, 8, 6, 6) };
    _headerStyle.normal.background = NetworkSensePanelTheme.HeaderTex;
    _titleStyle = Rich(16, serif, FontStyle.Bold);
    _subtitleStyle = Rich(10, serif, FontStyle.Normal);
    _tabStyle = new(GUI.skin.button) { richText = true, fontSize = 13, fontStyle = FontStyle.Bold };
    _activeTabStyle = new(_tabStyle);
    _activeTabStyle.normal.background = NetworkSensePanelTheme.GlassTex;
    _activeTabStyle.hover.background = NetworkSensePanelTheme.GlassTex;
    _activeTabStyle.active.background = NetworkSensePanelTheme.HeaderTex;
    _sectionStyle = Rich(13, null, FontStyle.Bold);
    _labelStyle = Rich(13, serif, FontStyle.Normal);
    _labelStyle.wordWrap = true;
    _mutedStyle = Rich(11, null, FontStyle.Normal);
    _mutedStyle.alignment = TextAnchor.MiddleCenter;
    _buttonStyle = new(GUI.skin.button) { richText = true, fontSize = 12, fontStyle = FontStyle.Bold };
    _primaryButtonStyle = new(_buttonStyle);
    _primaryButtonStyle.normal.textColor = Color.black;
    _primaryButtonStyle.fontStyle = FontStyle.Bold;
    _insetStyle = new GUIStyle { padding = new RectOffset(10, 10, 8, 8) };
    _insetStyle.normal.background = NetworkSensePanelTheme.InsetTex;
    _glassStyle = new GUIStyle { padding = new RectOffset(12, 12, 10, 10) };
    _glassStyle.normal.background = NetworkSensePanelTheme.GlassTex;
    _glassTextStyle = new GUIStyle(GUI.skin.label) {
        richText = false,
        fontSize = 13,
        wordWrap = true,
        normal = {
            textColor = new Color(0.90f, 0.96f, 0.97f, 1.0f)
        }
    };
  }

  static GUIStyle Rich(int size, Font font, FontStyle style) {
    GUIStyle result = new(GUI.skin.label) {
        richText = true,
        fontSize = size,
        fontStyle = style,
        wordWrap = false
    };
    if (font != null) {
      result.font = font;
    }
    return result;
  }

  static string Fallback(string value) {
    return string.IsNullOrWhiteSpace(value) ? "n/a" : value;
  }

  static int DetailNumber(HudDetailLevel detailLevel) {
    return Mathf.Clamp((int) detailLevel + 1, 1, 3);
  }

  static string ModeDescription(NetworkSenseMode mode) {
    return mode switch {
      NetworkSenseMode.Combat => "Solo danger is active; prioritize responsiveness and frame stability.",
      NetworkSenseMode.GroupCombat => "Multiple players are fighting; watch merge risk, ownership, and server pulse.",
      NetworkSenseMode.Town => "Dense base or community area; expect higher entity/build pressure.",
      _ => "Solo exploration or routine play; monitor without forcing a policy."
    };
  }

  static string ModeLabel(NetworkSenseMode mode) {
    return mode switch {
      NetworkSenseMode.GroupCombat => "Group Combat",
      NetworkSenseMode.Town => "Town",
      NetworkSenseMode.Combat => "Combat",
      _ => "Solo"
    };
  }

  static string HeroLine(NetworkSenseSnapshot snapshot) {
    ClientTelemetrySample sample = snapshot.ClientSample;
    ScoreSnapshot scores = snapshot.Scores;

    if (sample == null) {
      return "Waiting for the first telemetry sample.";
    }

    if (scores != null && scores.ConnectionLabel == "Stable" && scores.PressureLabel == "Low") {
      return "The world is calm. Your frame time is steady and the local area is light.";
    }

    if (sample.DangerNearby) {
      return "There is nearby danger. Treat network recommendations as secondary until the fight is clear.";
    }

    return "Signals are mixed. Check the Signals tab before changing test conditions.";
  }

  static string RavenStatusLine(RavenState raven) {
    if (raven == null) {
      return "Raven tower not checked.";
    }

    if (raven.IsBusy) {
      int dots = Mathf.FloorToInt(Time.unscaledTime * 2.0f) % 4;
      return "Sending raven" + new string('.', dots);
    }

    return raven.IsOnline ? "Raven tower online." : "Raven tower offline or not checked.";
  }

  static string FormatTime(string utc) {
    if (string.IsNullOrWhiteSpace(utc)) {
      return "n/a";
    }

    if (!DateTime.TryParse(utc, out DateTime parsed)) {
      return utc;
    }

    return parsed.ToLocalTime().ToString("HH:mm:ss");
  }

  static List<string> BuildChecklist(NetworkSenseSnapshot snapshot) {
    bool hasExport = !string.IsNullOrWhiteSpace(snapshot.LatestExportPath);
    bool hasBenchmarkEvent = HasEvent(snapshot, "benchmark_end");
    bool hasFriendMarker = HasEventMessage(snapshot, "Friend joined");
    bool hasBaseMarker = HasEventMessage(snapshot, "Entered base");
    bool hasPulse = snapshot.ServerPulse != null;

    return [
        Check(hasFriendMarker, "friend joined"),
        Check(hasBenchmarkEvent, "benchmark"),
        Check(hasBaseMarker, "same area/base"),
        Check(hasExport, "host export"),
        Check(hasPulse, "server pulse"),
        "Then: client export + MCP compare."
    ];
  }

  static List<string> BuildEventLines(NetworkSenseEventRow[] rows, int maxLines) {
    if (rows == null || rows.Length <= 0) {
      return ["No recent events in this session yet."];
    }

    List<string> lines = [];
    int start = Mathf.Max(0, rows.Length - Mathf.Max(1, maxLines));
    for (int index = start; index < rows.Length; index++) {
      NetworkSenseEventRow row = rows[index];
      lines.Add($"{FormatTime(row.TimestampUtc)} | {row.EventName} | {row.Message}");
    }
    return lines;
  }

  static bool HasEvent(NetworkSenseSnapshot snapshot, string eventName) {
    if (snapshot.RecentEvents == null) {
      return false;
    }

    foreach (NetworkSenseEventRow row in snapshot.RecentEvents) {
      if (string.Equals(row.EventName, eventName, StringComparison.OrdinalIgnoreCase)) {
        return true;
      }
    }

    return false;
  }

  static bool HasEventMessage(NetworkSenseSnapshot snapshot, string message) {
    if (snapshot.RecentEvents == null) {
      return false;
    }

    foreach (NetworkSenseEventRow row in snapshot.RecentEvents) {
      if ((row.Message ?? string.Empty).IndexOf(message, StringComparison.OrdinalIgnoreCase) >= 0) {
        return true;
      }
    }

    return false;
  }

  static string Check(bool complete, string label) {
    return $"{(complete ? "[x]" : "[ ]")} {label}";
  }

}
