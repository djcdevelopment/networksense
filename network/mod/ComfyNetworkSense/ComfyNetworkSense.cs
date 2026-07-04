namespace ComfyNetworkSense;

using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;

using BepInEx;
using BepInEx.Logging;

using HarmonyLib;

using UnityEngine;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class ComfyNetworkSense : BaseUnityPlugin {
  public const string PluginGuid = "djcdevelopment.valheim.comfynetworksense";
  public const string PluginName = "ComfyNetworkSense";
  public const string PluginVersion = "0.1.0";

  public static ComfyNetworkSense Instance { get; private set; }

  static ManualLogSource _logger;
  static readonly ConcurrentQueue<string> _mainThreadMessages = new();
  TelemetryCoordinator _coordinator;
  Harmony _harmony;

  void Awake() {
    Instance = this;
    _logger = Logger;

    PluginConfig.Bind(Config);

    _coordinator = new();

    _harmony = Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), harmonyInstanceId: PluginGuid);
    PanelInputPatches.Apply(_harmony);
    RegisterConsoleCommands();

    LogInfo("Telemetry scaffold ready.");
  }

  void Update() {
    if (!PluginConfig.IsModEnabled.Value) {
      return;
    }

    FlushMainThreadMessages();

    if (_coordinator != null && _coordinator.IsPanelOpen && Input.GetKeyDown(KeyCode.Escape)) {
      _coordinator.ClosePanel();
    }

    _coordinator?.Update(Time.unscaledDeltaTime);
  }

  void OnGUI() {
    if (!PluginConfig.IsModEnabled.Value) {
      return;
    }

    _coordinator?.DrawHud();
  }

  void OnDestroy() {
    _coordinator?.Dispose();
    _coordinator = null;
    _harmony?.UnpatchSelf();
    _harmony = null;

    if (Instance == this) {
      Instance = null;
    }
  }

  public static void HandleServerPulse(long senderId, ZPackage package) {
    Instance?._coordinator?.HandleServerPulse(senderId, package);
  }

  void RegisterConsoleCommands() {
    new Terminal.ConsoleCommand("network_sense_hud", "toggle the ComfyNetworkSense HUD", _ => RunCommand(_coordinator.ToggleHud));
    new Terminal.ConsoleCommand(
        "network_sense_detail",
        "cycle ComfyNetworkSense HUD detail: Summary -> Diagnostic -> DeepDebug",
        _ => RunCommand(_coordinator.CycleHudDetail));
    new Terminal.ConsoleCommand(
        "network_sense_mode",
        "cycle or set ComfyNetworkSense mode: network_sense_mode [solo|combat|group|town]",
        SetModeCommand);
    new Terminal.ConsoleCommand(
        "network_sense_benchmark",
        "start or cancel the ComfyNetworkSense benchmark capture",
        _ => RunCommand(_coordinator.ToggleBenchmark));
    new Terminal.ConsoleCommand(
        "network_sense_status",
        "show ComfyNetworkSense HUD/detail/mode/benchmark status",
        _ => RunCommand(_coordinator.GetStatus));
    new Terminal.ConsoleCommand(
        "network_sense_panel",
        "toggle the ComfyNetworkSense debug panel: network_sense_panel [debug|signals|raven]",
        PanelCommand);
    new Terminal.ConsoleCommand(
        "network_sense_debug",
        "open the ComfyNetworkSense debug panel",
        _ => OpenPanelCommand("debug"));
    new Terminal.ConsoleCommand(
        "network_sense_raven",
        "open the ComfyNetworkSense Raven panel",
        _ => OpenPanelCommand("raven"));
    new Terminal.ConsoleCommand(
        "network_sense_reload_config",
        "reload ComfyNetworkSense BepInEx config",
        _ => ReloadPluginConfig());
    new Terminal.ConsoleCommand(
        "network_sense_export_session",
        "write a compact ComfyNetworkSense session export JSON",
        _ => RunCommand(_coordinator.ExportSession));
    new Terminal.ConsoleCommand(
        "network_sense_mcp_status",
        "check whether the local Comfy MCP gateway is reachable",
        _ => CheckMcpGateway());
    new Terminal.ConsoleCommand(
        "network_sense_mcp_note",
        "record a NetworkSense developer note: network_sense_mcp_note <text>",
        RecordNoteCommand);
    new Terminal.ConsoleCommand(
        "network_sense_mcp_mark",
        "record a NetworkSense test marker: network_sense_mcp_mark <label>",
        RecordMarkerCommand);
  }

  object SetModeCommand(Terminal.ConsoleEventArgs args) {
    if (args.Length < 2) {
      return RunCommand(_coordinator.CycleMode);
    }

    if (TryParseMode(args[1], out NetworkSenseMode mode)) {
      return RunCommand(() => _coordinator.SetMode(mode));
    }

    string message = "Usage: network_sense_mode [solo|combat|group|town]";
    MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
    LogWarning(message);
    return false;
  }

  object PanelCommand(Terminal.ConsoleEventArgs args) {
    string tab = args.Length >= 2 ? args[1] : null;
    _coordinator.TogglePanel(tab);
    string message = "NetworkSense panel toggled.";
    MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
    LogInfo(message);
    return message;
  }

  object OpenPanelCommand(string tab) {
    _coordinator.OpenPanel(tab);
    string message = $"NetworkSense {tab} panel opened.";
    MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
    LogInfo(message);
    return message;
  }

  static object RunCommand(Func<string> action) {
    string message = action();
    LogInfo(message);
    return message;
  }

  object ReloadPluginConfig() {
    Config.Reload();
    string message = "NetworkSense config reloaded.";
    MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
    LogInfo(message);
    return message;
  }

  object CheckMcpGateway() {
    const string endpoint = "http://127.0.0.1:8720/healthz";
    _ = Task.Run(async () => {
      string message;
      try {
        HttpWebRequest request = (HttpWebRequest) WebRequest.Create(endpoint);
        request.Method = "GET";
        request.Timeout = 2000;
        request.ReadWriteTimeout = 2000;
        request.Headers.Add("X-Comfy-Key", "valheim-mod-local");

        using WebResponse response = await request.GetResponseAsync().ConfigureAwait(false);
        message = $"Comfy MCP gateway reachable: HTTP {((HttpWebResponse) response).StatusCode}.";
      } catch (Exception exception) {
        message = $"Comfy MCP gateway unreachable: {exception.GetType().Name}: {exception.Message}";
      }

      _mainThreadMessages.Enqueue(message);
      LogInfo(message);
    });

    return "NetworkSense MCP status check started.";
  }

  object RecordNoteCommand(Terminal.ConsoleEventArgs args) {
    string note = JoinArgs(args, startIndex: 1);

    if (string.IsNullOrWhiteSpace(note)) {
      string message = "Usage: network_sense_mcp_note <text>";
      MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
      return false;
    }

    return RunCommand(() => _coordinator.RecordDevNote(note));
  }

  object RecordMarkerCommand(Terminal.ConsoleEventArgs args) {
    string label = JoinArgs(args, startIndex: 1);

    if (string.IsNullOrWhiteSpace(label)) {
      string message = "Usage: network_sense_mcp_mark <label>";
      MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
      return false;
    }

    return RunCommand(() => _coordinator.RecordDevMarker(label));
  }

  static string JoinArgs(Terminal.ConsoleEventArgs args, int startIndex) {
    if (args.Length <= startIndex) {
      return string.Empty;
    }

    string[] values = new string[args.Length - startIndex];

    for (int index = startIndex; index < args.Length; index++) {
      values[index - startIndex] = args[index];
    }

    return string.Join(" ", values);
  }

  static void FlushMainThreadMessages() {
    while (_mainThreadMessages.TryDequeue(out string message)) {
      MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, message);
    }
  }

  public static void EnqueueMainThreadMessage(string message) {
    _mainThreadMessages.Enqueue(message);
  }

  public static bool IsPanelOpen => Instance?._coordinator?.IsPanelOpen == true;

  static bool TryParseMode(string value, out NetworkSenseMode mode) {
    switch ((value ?? string.Empty).Trim().ToLowerInvariant()) {
      case "solo":
      case "auto":
        mode = NetworkSenseMode.Solo;
        return true;
      case "combat":
        mode = NetworkSenseMode.Combat;
        return true;
      case "group":
      case "groupcombat":
      case "group-combat":
      case "group_combat":
        mode = NetworkSenseMode.GroupCombat;
        return true;
      case "town":
      case "base":
      case "village":
      case "low":
      case "lowimpact":
      case "low-impact":
      case "staging":
      case "stage":
        mode = NetworkSenseMode.Town;
        return true;
      default:
        mode = NetworkSenseMode.Solo;
        return false;
    }
  }

  public static void LogInfo(object message) {
    _logger?.LogInfo($"[{DateTime.Now.ToString(DateTimeFormatInfo.InvariantInfo)}] {message}");
  }

  public static void LogWarning(object message) {
    _logger?.LogWarning($"[{DateTime.Now.ToString(DateTimeFormatInfo.InvariantInfo)}] {message}");
  }
}
