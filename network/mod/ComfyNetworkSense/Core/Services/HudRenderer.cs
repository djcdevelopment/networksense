namespace ComfyNetworkSense;

using System;
using System.Collections.Generic;

using UnityEngine;

public sealed class HudRenderer {
  GUIStyle _boxStyle;
  GUIStyle _labelStyle;
  GUIStyle _mutedStyle;
  Texture2D _boxTexture;
  float _boxOpacity = -1.0f;

  public void Draw(
      bool isVisible,
      HudDetailLevel detailLevel,
      NetworkSenseMode mode,
      bool benchmarkRunning,
      string serverPulseState,
      ClientTelemetrySample clientSample,
      ServerPulseSnapshot serverPulse,
      ScoreSnapshot scores) {
    if (!isVisible || clientSample == null) {
      return;
    }

    float opacity = Mathf.Clamp01(PluginConfig.HudOpacity.Value);
    EnsureStyles(opacity);

    HudDetailLevel effectiveDetail = GetEffectiveDetail(detailLevel);
    bool minimalPreset = IsHudPreset("minimal");
    List<string> lines = minimalPreset
        ? [
            $"NetworkSense | {ModeLabel(mode)} | ping {clientSample.RttMs:0} ms | fps {clientSample.Fps:0} | p95 {clientSample.FrameTimeP95Ms:0.0} ms | {scores?.ConnectionLabel ?? "n/a"} | {scores?.PressureLabel ?? "n/a"}"
        ]
        : [
            $"Mode {ModeLabel(mode)} | HUD {effectiveDetail} | Benchmark {(benchmarkRunning ? "Running" : "Idle")} | Client ping {clientSample.RttMs:0} ms | jitter {clientSample.JitterMs:0} ms | fps {clientSample.Fps:0} | p95 {clientSample.FrameTimeP95Ms:0.0} ms",
            $"Area players {clientSample.NearbyPlayers} | entities {clientSample.NearbyEntities} | pieces {clientSample.NearbyBuildPieces} | zone {clientSample.RegionId} | connection {scores?.ConnectionLabel ?? "n/a"} | owner {scores?.OwnerFitLabel ?? "n/a"} | pressure {scores?.PressureLabel ?? "n/a"} | server {serverPulseState}"
        ];

    if (!minimalPreset && scores != null && scores.LowImpactRecommended) {
      lines.Add("Hint: Low Impact is recommended for the current local conditions.");
    }

    if (!minimalPreset && serverPulse != null) {
      lines[1] +=
          $" | pulse owner {Fallback(serverPulse.OwnerId)} ({serverPulse.OwnerReason}) | pulse pressure {serverPulse.RegionPressureScore:0.00} | age {GetPulseAgeSeconds(serverPulse):0.0}s";
    }

    if (effectiveDetail >= HudDetailLevel.Diagnostic) {
      lines.Add(
          $"Rates: in {clientSample.BytesInPerSec:0} B/s, out {clientSample.BytesOutPerSec:0} B/s, pkts {clientSample.PacketsInPerSec:0.0}/{clientSample.PacketsOutPerSec:0.0} per s");
      lines.Add(
          $"Load: cpu {clientSample.CpuBoundEstimate:0.00} | danger {(clientSample.DangerNearby ? "yes" : "no")} | confidence {scores?.ConfidenceLabel ?? "Low"}");
    }

    if (effectiveDetail >= HudDetailLevel.DeepDebug && lines.Count >= 4) {
      lines[2] +=
          $" | scores net {scores?.NetworkQuality:0.00} frame {scores?.FrameStability:0.00} cpu {scores?.CpuHeadroom:0.00} owner {scores?.OwnerScore:0.00}";
      lines[3] +=
          $" | pressure region {scores?.RegionPressure:0.00} load {scores?.CurrentLoadPenalty:0.00} merge {scores?.MergeRisk:0.00} | server bytes {serverPulse?.BytesSentPerSec ?? 0.0f:0} msg {serverPulse?.MessagesSentPerSec ?? 0.0f:0.0} stability {serverPulse?.AuthorityStabilitySec ?? 0.0f:0.0}s";
    }

    float scale = Mathf.Max(0.75f, PluginConfig.HudScale.Value);
    int margin = Mathf.Max(0, PluginConfig.HudMarginPixels.Value);
    float width = Mathf.Clamp(PluginConfig.HudMaxWidth.Value, 420.0f, 1800.0f) * scale;
    float lineHeight = 22.0f * scale;
    float height = (lines.Count + 1.15f) * lineHeight;

    Rect boxRect = new(margin, margin, width, height);
    GUI.Box(boxRect, "ComfyNetworkSense", _boxStyle);

    Rect lineRect = new(boxRect.x + 12.0f, boxRect.y + 28.0f, boxRect.width - 24.0f, lineHeight);

    for (int i = 0; i < lines.Count; i++) {
      GUI.Label(lineRect, lines[i], i == lines.Count - 1 && serverPulse == null ? _mutedStyle : _labelStyle);
      lineRect.y += lineHeight;
    }
  }

  void EnsureStyles(float opacity) {
    if (_boxTexture == null || !Mathf.Approximately(_boxOpacity, opacity)) {
      _boxOpacity = opacity;
      _boxTexture = MakeTex(new Color(0.03f, 0.07f, 0.09f, opacity));
      if (_boxStyle != null) {
        _boxStyle.normal.background = _boxTexture;
        _boxStyle.onNormal.background = _boxTexture;
      }
    }

    if (_boxStyle != null) {
      return;
    }

    _boxStyle = new(GUI.skin.box) {
        alignment = TextAnchor.UpperLeft,
        fontSize = 15,
        richText = false,
        padding = new RectOffset(10, 10, 8, 10)
    };
    _boxStyle.normal.background = _boxTexture;
    _boxStyle.onNormal.background = _boxTexture;

    _labelStyle = new(GUI.skin.label) {
        fontSize = 14,
        richText = false,
        normal = {
            textColor = new Color(0.92f, 0.95f, 0.97f, 1.0f)
        }
    };

    _mutedStyle = new(_labelStyle) {
        normal = {
            textColor = new Color(0.75f, 0.78f, 0.80f, 1.0f)
        }
    };
  }

  static string Fallback(string value) {
    return string.IsNullOrWhiteSpace(value) ? "n/a" : value;
  }

  static HudDetailLevel GetEffectiveDetail(HudDetailLevel detailLevel) {
    if (IsHudPreset("diagnostic") && detailLevel < HudDetailLevel.Diagnostic) {
      return HudDetailLevel.Diagnostic;
    }

    return detailLevel;
  }

  static bool IsHudPreset(string preset) {
    return string.Equals(PluginConfig.HudPreset.Value?.Trim(), preset, StringComparison.OrdinalIgnoreCase);
  }

  static string ModeLabel(NetworkSenseMode mode) {
    return mode switch {
      NetworkSenseMode.GroupCombat => "Group Combat",
      NetworkSenseMode.Town => "Town",
      NetworkSenseMode.Combat => "Combat",
      _ => "Solo"
    };
  }

  static Texture2D MakeTex(Color color) {
    Texture2D texture = new(1, 1, TextureFormat.RGBA32, mipChain: false) {
        hideFlags = HideFlags.HideAndDontSave
    };
    texture.SetPixel(0, 0, color);
    texture.Apply();
    return texture;
  }

  static double GetPulseAgeSeconds(ServerPulseSnapshot pulse) {
    if (pulse == null || string.IsNullOrWhiteSpace(pulse.TimestampUtc)) {
      return 0.0d;
    }

    if (!DateTime.TryParse(pulse.TimestampUtc, out DateTime timestamp)) {
      return 0.0d;
    }

    return Math.Max(0.0d, (DateTime.UtcNow - timestamp.ToUniversalTime()).TotalSeconds);
  }
}
