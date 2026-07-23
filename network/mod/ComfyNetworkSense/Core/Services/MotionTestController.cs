namespace ComfyNetworkSense;

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using BepInEx;
using UnityEngine;

/// <summary>
/// Bounded, local-only movement driver for alpha transport experiments. Companion writes one
/// pipe-delimited command and this client consumes it on the Unity main thread. It is deliberately
/// not a general console bridge: only named patterns and short durations are accepted.
/// </summary>
public sealed class MotionTestController : IDisposable {
  const float MaxDurationSeconds = 60.0f;
  const float PollSeconds = 0.25f;
  readonly string _directory = Path.Combine(Paths.ConfigPath, "comfy-network-sense");
  readonly string _commandPath;
  readonly string _receiptPath;
  float _nextPollAt;
  float _startedAt;
  float _duration;
  Vector3 _origin;
  string _id = string.Empty;
  string _pattern = string.Empty;
  bool _active;

  public MotionTestController() {
    _commandPath = Path.Combine(_directory, "companion-motion.command");
    _receiptPath = Path.Combine(_directory, "companion-motion-receipts.jsonl");
    Directory.CreateDirectory(_directory);
  }

  public bool Active => _active;
  public string Pattern => _pattern;
  public float RemainingSeconds => _active ? Mathf.Max(0.0f, _duration - (Time.unscaledTime - _startedAt)) : 0.0f;

  public void Update() {
    if (Time.unscaledTime >= _nextPollAt) {
      _nextPollAt = Time.unscaledTime + PollSeconds;
      ConsumeCommand();
    }

    if (!_active) return;
    if (Player.m_localPlayer == null || ZNet.instance == null || ZNet.instance.IsServer()) {
      Finish("error", "local_player_unavailable");
      return;
    }

    float elapsed = Time.unscaledTime - _startedAt;
    if (elapsed >= _duration) {
      Finish("completed", string.Empty);
      return;
    }

    Vector3 next = _origin + ResolveOffset(elapsed, _pattern);
    next.y = _origin.y;
    ((Component)Player.m_localPlayer).transform.position = next;
  }

  void ConsumeCommand() {
    if (!File.Exists(_commandPath)) return;
    string line;
    try {
      line = File.ReadAllText(_commandPath).Trim();
      File.Delete(_commandPath);
    } catch (Exception exception) {
      WriteReceipt("error", string.Empty, string.Empty, 0.0f, exception.GetType().Name);
      return;
    }

    string[] fields = line.Split('|');
    if (fields.Length < 2) {
      WriteReceipt("error", string.Empty, string.Empty, 0.0f, "malformed_command");
      return;
    }

    string id = fields[0].Trim();
    string action = fields[1].Trim().ToLowerInvariant();
    if (action == "stop") {
      if (_active) Finish("stopped", string.Empty);
      else WriteReceipt("stopped", id, string.Empty, 0.0f, string.Empty);
      return;
    }
    if (action != "start" || fields.Length < 4 || !IsSafeToken(id)) {
      WriteReceipt("error", id, string.Empty, 0.0f, "invalid_command");
      return;
    }

    string pattern = fields[2].Trim().ToLowerInvariant();
    if (pattern is not ("straight_north" or "straight_east" or "stutter_north" or "circle")) {
      WriteReceipt("error", id, pattern, 0.0f, "pattern_not_allowed");
      return;
    }
    if (!float.TryParse(fields[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float duration) ||
        duration is < 1.0f or > MaxDurationSeconds) {
      WriteReceipt("error", id, pattern, duration, "duration_out_of_range");
      return;
    }
    if (Player.m_localPlayer == null || ZNet.instance == null || ZNet.instance.IsServer()) {
      WriteReceipt("error", id, pattern, duration, "client_player_required");
      return;
    }

    if (_active) Finish("stopped", "replaced_by_new_command");
    _id = id;
    _pattern = pattern;
    _duration = duration;
    _startedAt = Time.unscaledTime;
    _origin = ((Component)Player.m_localPlayer).transform.position;
    _active = true;
    WriteReceipt("started", _id, _pattern, _duration, string.Empty);
  }

  Vector3 ResolveOffset(float elapsed, string pattern) {
    switch (pattern) {
      case "straight_east": return new Vector3(Mathf.Min(8.0f, elapsed * 3.0f), 0.0f, 0.0f);
      case "stutter_north": {
        float phase = elapsed % 4.0f;
        return new Vector3(0.0f, 0.0f, phase < 2.5f ? Mathf.Min(8.0f, phase * 3.0f) : Mathf.Min(8.0f, 7.5f));
      }
      case "circle": {
        float angle = elapsed * 1.5f;
        return new Vector3(Mathf.Cos(angle) * 4.0f, 0.0f, Mathf.Sin(angle) * 4.0f);
      }
      default: return new Vector3(0.0f, 0.0f, Mathf.Min(8.0f, elapsed * 3.0f));
    }
  }

  void Finish(string state, string detail) {
    WriteReceipt(state, _id, _pattern, _duration, detail);
    _active = false;
    _id = string.Empty;
    _pattern = string.Empty;
  }

  void WriteReceipt(string state, string id, string pattern, float duration, string detail) {
    try {
      string line = "{\"schema_version\":1,\"timestamp_utc\":\"" +
          DateTimeOffset.UtcNow.ToString("o") + "\",\"state\":\"" + Escape(state) +
          "\",\"id\":\"" + Escape(id) + "\",\"pattern\":\"" + Escape(pattern) +
          "\",\"duration_seconds\":" + duration.ToString("0.###", CultureInfo.InvariantCulture) +
          ",\"detail\":\"" + Escape(detail) + "\"}" + Environment.NewLine;
      File.AppendAllText(_receiptPath, line);
    } catch (Exception exception) {
      Debug.LogWarning("[ComfyNetworkSense] Motion test receipt failed: " + exception.Message);
    }
  }

  static bool IsSafeToken(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 80 &&
      value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.');
  static string Escape(string value) => (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
  public void Dispose() { if (_active) Finish("stopped", "plugin_disposed"); }
}
