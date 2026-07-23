namespace ComfyNetworkSense;

using System;

using UnityEngine;

/// <summary>Always-visible alpha transport truth strip and deliberate fault switchboard.</summary>
public sealed class TransportStatusOverlay {
  GUIStyle _box;
  GUIStyle _label;
  GUIStyle _button;
  GUIStyle _good;
  GUIStyle _idle;
  GUIStyle _off;
  bool? _visible;

  public void Draw(
      TransportStatusSnapshot status,
      Action toggleLumberjacksHttp,
      Action toggleLumberjacksWebSocket,
      Action toggleLumberjacksUdp,
      Action toggleMotionApply,
      Action toggleMcp,
      Action disconnectValheim,
      Action openDashboard,
      Action openSetup) {
    if (status == null || !PluginConfig.TransportStripEnabled.Value || IsDedicatedServer()) {
      return;
    }

    _visible ??= PluginConfig.TransportStripVisibleOnStart.Value;
    EnsureStyles();

    DrawToggleTab();
    if (_visible != true) {
      return;
    }

    const float width = 1040.0f;
    const float height = 122.0f;
    float x = Mathf.Max(8.0f, (Screen.width - width) / 2.0f);
    float y = Mathf.Max(8.0f, Screen.height - height - 18.0f);

    GUILayout.BeginArea(new Rect(x, y, Mathf.Min(width, Screen.width - 16.0f), height), _box);
    GUILayout.BeginHorizontal();
    GUILayout.Label("NETWORK", _label, GUILayout.Width(82.0f));
    Status("Native Valheim", status.ValheimConnected, _good, _off, 156.0f);
    Status("LJ ZDO", status.LumberjacksArmed && status.LumberjacksHttpEnabled, _good, _off, 104.0f);
    Status("LJ Motion", status.LumberjacksWebSocketConnected, _good, _off, 118.0f);
    GUILayout.Label("FULL NETCODE [NO]", _off, GUILayout.Width(142.0f));
    GUILayout.Label("Dashboard: " + Fallback(status.DashboardUrl), _label, GUILayout.ExpandWidth(true));
    if (GUILayout.Button("OPEN", _button, GUILayout.Width(58.0f))) openDashboard?.Invoke();
    if (GUILayout.Button("SETUP", _button, GUILayout.Width(62.0f))) openSetup?.Invoke();
    GUILayout.EndHorizontal();

    GUILayout.BeginHorizontal();
    GUILayout.Label("LJ ZDO PATH", _label, GUILayout.Width(82.0f));
    if (GUILayout.Button("HTTP " + Mark(status.LumberjacksHttpEnabled),
        status.LumberjacksHttpEnabled ? _good : _off, GUILayout.Width(104.0f))) {
      toggleLumberjacksHttp?.Invoke();
    }
    Status("JSON", status.LumberjacksArmed && status.LumberjacksHttpEnabled, _good, _off, 92.0f);
    Status("WebSocket", false, _idle, _idle, 126.0f, "[-]");
    Status("UDP", false, _idle, _idle, 82.0f, "[-]");
    if (GUILayout.Button("MCP " + (status.McpEnabled ? (status.McpReachable ? "[x]" : "[?]") : "[ ]"),
        !status.McpEnabled ? _off : status.McpReachable ? _good : _idle, GUILayout.Width(92.0f))) {
      toggleMcp?.Invoke();
    }
    GUILayout.Label("state " + Fallback(status.LumberjacksState), _label, GUILayout.ExpandWidth(true));
    if (GUILayout.Button("DISCONNECT", _off, GUILayout.Width(104.0f))) disconnectValheim?.Invoke();
    GUILayout.EndHorizontal();

    GUILayout.BeginHorizontal();
    GUILayout.Label("LJ MOTION", _label, GUILayout.Width(82.0f));
    if (GUILayout.Button("WebSocket " + Mark(status.LumberjacksWebSocketEnabled && status.LumberjacksWebSocketConnected),
        status.LumberjacksWebSocketEnabled && status.LumberjacksWebSocketConnected ? _good : _off,
        GUILayout.Width(136.0f))) toggleLumberjacksWebSocket?.Invoke();
    if (GUILayout.Button("UDP " + Mark(status.LumberjacksUdpEnabled && status.LumberjacksUdpReady),
        status.LumberjacksUdpEnabled && status.LumberjacksUdpReady ? _good : _off,
        GUILayout.Width(96.0f))) toggleLumberjacksUdp?.Invoke();
    if (GUILayout.Button("APPLY " + Mark(status.MotionApplyEnabled),
        status.MotionApplyEnabled ? _good : _idle, GUILayout.Width(104.0f))) toggleMotionApply?.Invoke();
    GUILayout.Label(
        "snapshots sent " + status.MotionSent + " · received " + status.MotionReceived + " · applied " + status.MotionApplied
        + (status.MotionApplyEnabled ? "" : " · OBSERVE ONLY"),
        _label,
        GUILayout.ExpandWidth(true));
    GUILayout.EndHorizontal();
    GUILayout.EndArea();
  }

  void DrawToggleTab() {
    const float tabWidth = 78.0f;
    const float tabHeight = 34.0f;
    float x = ToggleSideIsLeft() ? 8.0f : Mathf.Max(8.0f, Screen.width - tabWidth - 8.0f);
    float y = Mathf.Max(8.0f, (Screen.height - tabHeight) / 2.0f);
    string label = _visible == true ? "NET HIDE" : "NET SHOW";

    GUILayout.BeginArea(new Rect(x, y, tabWidth, tabHeight));
    if (GUILayout.Button(label, _visible == true ? _idle : _good, GUILayout.Width(tabWidth), GUILayout.Height(tabHeight))) {
      _visible = _visible != true;
    }
    GUILayout.EndArea();
  }

  static bool IsDedicatedServer() =>
      ZNet.instance != null && ZNet.instance.IsServer() && ZNet.instance.IsDedicated();

  void Status(string label, bool active, GUIStyle activeStyle, GUIStyle inactiveStyle, float width, string fixedMark = null) {
    GUILayout.Label(label + " " + (fixedMark ?? Mark(active)), active ? activeStyle : inactiveStyle, GUILayout.Width(width));
  }

  static string Mark(bool active) => active ? "[x]" : "[ ]";
  static string Fallback(string value) => string.IsNullOrWhiteSpace(value) ? "n/a" : value;
  static bool ToggleSideIsLeft() =>
      string.Equals(PluginConfig.TransportStripToggleSide.Value?.Trim(), "left", StringComparison.OrdinalIgnoreCase);

  void EnsureStyles() {
    if (_box != null) return;
    _box = new(GUI.skin.box) {
        padding = new RectOffset(10, 10, 9, 9),
        normal = { background = NetworkSensePanelTheme.PanelTex }
    };
    _label = new(GUI.skin.label) {
        fontSize = 13,
        normal = { textColor = Color.white }
    };
    _button = new(GUI.skin.button) { fontSize = 12 };
    _good = new(_button) { normal = { textColor = NetworkSensePanelTheme.StatusGreen } };
    _idle = new(_button) { normal = { textColor = NetworkSensePanelTheme.StatusAmber } };
    _off = new(_button) { normal = { textColor = NetworkSensePanelTheme.StatusRust } };
  }
}
