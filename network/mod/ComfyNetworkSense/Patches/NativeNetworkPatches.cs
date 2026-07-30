namespace ComfyNetworkSense;

using HarmonyLib;

// These patches are both the C0 evidence boundary and the final-cutover poison guard. A bool
// prefix returning false is intentional: poison mode must prove it can suppress the original
// native call, not merely report that the call happened.
[HarmonyPatch(typeof(ZSteamSocket))]
static class NativeSteamSocketPatches {
  [HarmonyPatch("SendQueuedPackages")]
  [HarmonyPrefix]
  [HarmonyPriority(Priority.First)]
  static bool SendQueuedPackagesPrefix() =>
      !NativeNetworkLedger.Observe(
          "steam_send_queued_packages", "outbound", "steam_packet_batch");

  [HarmonyPatch(nameof(ZSteamSocket.Recv))]
  [HarmonyPrefix]
  [HarmonyPriority(Priority.First)]
  static bool RecvPrefix(ref ZPackage __result) {
    bool blocked = NativeNetworkLedger.Observe(
        "steam_recv", "inbound", "steam_packet_poll");
    if (blocked) {
      __result = null;
    }
    return !blocked;
  }
}

[HarmonyPatch(typeof(ZNet))]
static class NativeHandshakeLedgerPatches {
  [HarmonyPatch("OnNewConnection")]
  [HarmonyPrefix]
  [HarmonyPriority(Priority.First)]
  static bool OnNewConnectionPrefix() {
    NativeNetworkLedger.Stage("on_new_connection_enter");
    return !NativeNetworkLedger.Observe(
        "native_peer_connection", "bidirectional", "on_new_connection");
  }

  [HarmonyPatch("OnNewConnection")]
  [HarmonyPostfix]
  static void OnNewConnectionPostfix() =>
      NativeNetworkLedger.Stage("on_new_connection_exit");

  [HarmonyPatch("RPC_ServerHandshake")]
  [HarmonyPrefix]
  [HarmonyPriority(Priority.First)]
  static bool RpcServerHandshakePrefix() {
    NativeNetworkLedger.Stage("rpc_server_handshake_enter");
    return !NativeNetworkLedger.Observe(
        "server_handshake", "client_to_server", "ServerHandshake");
  }

  [HarmonyPatch("RPC_ServerHandshake")]
  [HarmonyPostfix]
  static void RpcServerHandshakePostfix() =>
      NativeNetworkLedger.Stage("rpc_server_handshake_exit");

  [HarmonyPatch("RPC_ClientHandshake")]
  [HarmonyPrefix]
  [HarmonyPriority(Priority.First)]
  static bool RpcClientHandshakePrefix() {
    NativeNetworkLedger.Stage("rpc_client_handshake_enter");
    return !NativeNetworkLedger.Observe(
        "client_handshake", "server_to_client", "ClientHandshake");
  }

  [HarmonyPatch("RPC_ClientHandshake")]
  [HarmonyPostfix]
  static void RpcClientHandshakePostfix() =>
      NativeNetworkLedger.Stage("rpc_client_handshake_exit");

  [HarmonyPatch("SendPeerInfo")]
  [HarmonyPrefix]
  [HarmonyPriority(Priority.First)]
  static bool SendPeerInfoPrefix() {
    NativeNetworkLedger.Stage("send_peer_info_enter");
    return !NativeNetworkLedger.Observe(
        "peer_info_send", "outbound", "PeerInfo");
  }

  [HarmonyPatch("SendPeerInfo")]
  [HarmonyPostfix]
  static void SendPeerInfoPostfix() =>
      NativeNetworkLedger.Stage("send_peer_info_exit");

  [HarmonyPatch("RPC_PeerInfo")]
  [HarmonyPrefix]
  [HarmonyPriority(Priority.First)]
  static bool RpcPeerInfoPrefix() {
    NativeNetworkLedger.Stage("rpc_peer_info_enter");
    return !NativeNetworkLedger.Observe(
        "peer_info_receive", "inbound", "PeerInfo");
  }

  [HarmonyPatch("RPC_PeerInfo")]
  [HarmonyPostfix]
  static void RpcPeerInfoPostfix() =>
      NativeNetworkLedger.Stage("rpc_peer_info_exit");
}

[HarmonyPatch(typeof(ZDOMan))]
static class NativeZdoLedgerPatches {
  [HarmonyPatch("RPC_ZDOData")]
  [HarmonyPrefix]
  [HarmonyPriority(Priority.First)]
  static bool RpcZdoDataPrefix() =>
      !NativeNetworkLedger.Observe(
          "zdo_data_receive", "inbound", "ZDOData");

  [HarmonyPatch(nameof(ZDOMan.AddPeer))]
  [HarmonyPrefix]
  static void AddPeerPrefix() =>
      NativeNetworkLedger.Stage("zdo_add_peer");
}

[HarmonyPatch(typeof(ZRoutedRpc))]
static class NativeRoutedRpcLedgerPatches {
  [HarmonyPatch("RPC_RoutedRPC")]
  [HarmonyPrefix]
  [HarmonyPriority(Priority.First)]
  static bool RpcRoutedRpcPrefix() =>
      !NativeNetworkLedger.Observe(
          "routed_rpc_receive", "inbound", "RoutedRPC");

  [HarmonyPatch(nameof(ZRoutedRpc.AddPeer))]
  [HarmonyPrefix]
  static void AddPeerPrefix() =>
      NativeNetworkLedger.Stage("routed_rpc_add_peer");
}
