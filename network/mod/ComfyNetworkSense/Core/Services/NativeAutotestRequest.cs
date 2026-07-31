namespace ComfyNetworkSense;

using System;
using System.Globalization;
using System.IO;
using System.Linq;

using BepInEx;
using UnityEngine;

/// <summary>
/// One short-lived request from the native Windows client harness. This is the physical-client
/// equivalent of COMFY_AUTOJOIN for a bounded lab run, without leaving auto-join enabled in the
/// player's normal config. The harness writes the request atomically immediately before launching
/// Valheim; the mod consumes it only after the requested character reaches an actual peer.
/// </summary>
[Serializable]
public sealed class NativeAutotestRequest {
  public const int CurrentSchemaVersion = 1;
  public const string FileName = "native-autotest-request.json";

  public int schema_version;
  public string run_id;
  public string client;
  public string character;
  public string server;
  public string lumberjacks_gateway_url;
  public string created_utc;
  public string expires_utc;
  public bool native_network_poison;
  public bool routed_rpc_cutover;
  public bool zdo_journal_cutover;
  public bool zdo_journal_canonical_session;
  public bool ownership_lease_cutover;

  string _path;
  static string _activeRunId = string.Empty;
  static string _activeClient = string.Empty;
  static string _activeCharacter = string.Empty;
  static string _activeGatewayUrl = string.Empty;
  static bool _activeRoutedRpcCutover;
  static bool _activeZdoJournalCutover;
  static bool _activeZdoJournalCanonicalSession;
  static bool _activeOwnershipLeaseCutover;

  public string RunId => run_id ?? string.Empty;
  public string Client => client ?? string.Empty;
  public string Character => character ?? string.Empty;
  public string Server => server ?? string.Empty;
  public string LumberjacksGatewayUrl => lumberjacks_gateway_url ?? string.Empty;
  public bool NativeNetworkPoison => native_network_poison;
  public bool RoutedRpcCutover => routed_rpc_cutover;
  public bool ZdoJournalCutover => zdo_journal_cutover;
  public bool ZdoJournalCanonicalSession => zdo_journal_canonical_session;
  public bool OwnershipLeaseCutover => ownership_lease_cutover;
  public static string ActiveRunId => _activeRunId;
  public static string ActiveClient => _activeClient;
  public static string ActiveCharacter => _activeCharacter;
  public static string ActiveGatewayUrl => _activeGatewayUrl;
  public static bool ActiveRoutedRpcCutover => _activeRoutedRpcCutover;
  public static bool ActiveZdoJournalCutover => _activeZdoJournalCutover;
  public static bool ActiveZdoJournalCanonicalSession =>
      _activeZdoJournalCanonicalSession;
  public static bool ActiveOwnershipLeaseCutover =>
      _activeOwnershipLeaseCutover;

  public static bool TryLoad(out NativeAutotestRequest request, out string detail) {
    request = null;
    detail = string.Empty;
    string path = RequestPath();
    if (!File.Exists(path)) return false;

    try {
      FileInfo info = new(path);
      if (info.Length is <= 0 or > 4096) {
        detail = "request_size_invalid";
        return false;
      }

      NativeAutotestRequest parsed =
          JsonUtility.FromJson<NativeAutotestRequest>(File.ReadAllText(path));
      if (parsed == null || parsed.schema_version != CurrentSchemaVersion) {
        detail = "request_schema_invalid";
        return false;
      }
      if (!IsSafeToken(parsed.RunId, 80) || !IsSafeToken(parsed.Client, 32)) {
        detail = "request_identity_invalid";
        return false;
      }
      if (!IsSafeCharacter(parsed.Character) || !IsSafeServer(parsed.Server)) {
        detail = "request_target_invalid";
        return false;
      }
      if (!IsSafeGatewayUrl(parsed.LumberjacksGatewayUrl)) {
        detail = "request_gateway_invalid";
        return false;
      }
      if (!DateTimeOffset.TryParseExact(
              parsed.expires_utc ?? string.Empty, "o", CultureInfo.InvariantCulture,
              DateTimeStyles.RoundtripKind, out DateTimeOffset expires)) {
        detail = "request_expiry_invalid";
        return false;
      }
      DateTimeOffset now = DateTimeOffset.UtcNow;
      if (expires <= now || expires > now.AddMinutes(30)) {
        detail = expires <= now ? "request_expired" : "request_expiry_too_far";
        return false;
      }

      string launchTarget = ReadConnectTarget();
      if (!string.IsNullOrWhiteSpace(launchTarget) &&
          !string.Equals(launchTarget, parsed.Server, StringComparison.OrdinalIgnoreCase)) {
        detail = "request_launch_target_mismatch";
        return false;
      }

      parsed._path = path;
      _activeRunId = parsed.RunId;
      _activeClient = parsed.Client;
      _activeCharacter = parsed.Character;
      _activeGatewayUrl = parsed.LumberjacksGatewayUrl;
      _activeRoutedRpcCutover = parsed.RoutedRpcCutover;
      _activeZdoJournalCutover = parsed.ZdoJournalCutover;
      _activeZdoJournalCanonicalSession = parsed.ZdoJournalCanonicalSession;
      _activeOwnershipLeaseCutover = parsed.OwnershipLeaseCutover;
      NativeNetworkLedger.SetRunContext(parsed.RunId, parsed.Client);
      NativeNetworkLedger.SetPoisonOverride(parsed.NativeNetworkPoison);
      request = parsed;
      detail = "request_ready";
      return true;
    } catch (Exception exception) {
      detail = "request_read_failed:" + exception.GetType().Name;
      return false;
    }
  }

  public void Consume() {
    try {
      string path = string.IsNullOrWhiteSpace(_path) ? RequestPath() : _path;
      if (File.Exists(path)) File.Delete(path);
    } catch (Exception exception) {
      ComfyNetworkSense.LogWarning("Native autotest request cleanup failed: " + exception.Message);
    }
  }

  public void Record(string state, string detail = "") {
    string marker = "AUTOTEST_" + (state ?? string.Empty).Trim().ToUpperInvariant();
    string message =
        marker
        + " run_id=" + SafeLog(RunId)
        + " client=" + SafeLog(Client)
        + " character=" + SafeLog(Character)
        + " server=" + SafeLog(Server)
        + (string.IsNullOrWhiteSpace(detail) ? string.Empty : " " + detail);
    ComfyNetworkSense.LogInfo(message);

    try {
      string directory = Path.Combine(Paths.ConfigPath, "comfy-network-sense");
      Directory.CreateDirectory(directory);
      string line =
          "{\"schema_version\":1,\"timestamp_utc\":\"" + DateTimeOffset.UtcNow.ToString("o")
          + "\",\"state\":\"" + Escape(state)
          + "\",\"run_id\":\"" + Escape(RunId)
          + "\",\"client\":\"" + Escape(Client)
          + "\",\"character\":\"" + Escape(Character)
          + "\",\"server\":\"" + Escape(Server)
          + "\",\"detail\":\"" + Escape(detail) + "\"}" + Environment.NewLine;
      File.AppendAllText(Path.Combine(directory, "native-autotest-receipts.jsonl"), line);
    } catch (Exception exception) {
      ComfyNetworkSense.LogWarning("Native autotest receipt failed: " + exception.Message);
    }
  }

  static string RequestPath() =>
      Path.Combine(Paths.ConfigPath, "comfy-network-sense", FileName);

  static string ReadConnectTarget() {
    try {
      string[] arguments = Environment.GetCommandLineArgs();
      for (int i = 0; i + 1 < arguments.Length; i++) {
        if (string.Equals(arguments[i], "+connect", StringComparison.OrdinalIgnoreCase))
          return (arguments[i + 1] ?? string.Empty).Trim();
      }
    } catch { }
    return string.Empty;
  }

  static bool IsSafeToken(string value, int maxLength) =>
      !string.IsNullOrWhiteSpace(value)
      && value.Length <= maxLength
      && value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.');

  static bool IsSafeCharacter(string value) =>
      !string.IsNullOrWhiteSpace(value)
      && value.Length <= 64
      && value.All(c => !char.IsControl(c) && c is not '|' and not '\r' and not '\n');

  static bool IsSafeServer(string value) =>
      !string.IsNullOrWhiteSpace(value)
      && value.Length <= 255
      && value.Contains(':')
      && value.All(c => !char.IsWhiteSpace(c) && !char.IsControl(c) && c is not '"' and not '\\');

  static bool IsSafeGatewayUrl(string value) {
    if (string.IsNullOrWhiteSpace(value)) return true;
    if (value.Length > 240
        || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out Uri parsed))
      return false;
    return parsed.Scheme is "http" or "https" or "ws" or "wss"
        && !string.IsNullOrWhiteSpace(parsed.Host)
        && string.IsNullOrEmpty(parsed.UserInfo);
  }

  static string SafeLog(string value) =>
      new((value ?? string.Empty)
          .Where(c => !char.IsControl(c) && !char.IsWhiteSpace(c))
          .ToArray());

  static string Escape(string value) =>
      (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
}
