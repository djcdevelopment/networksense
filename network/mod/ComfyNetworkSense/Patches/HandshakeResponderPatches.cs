namespace ComfyNetworkSense;

using System;

using HarmonyLib;

using UnityEngine;

// P6/I5 server-side handshake interceptor. Prefix on ZNet.RPC_PeerInfo: when the responder is
// armed, decode a SetPos(0) CLONE of the client's PeerInfo (never touching vanilla's cursor),
// ask Lumberjacks for the admission verdict, and either veto (Invoke Error + skip vanilla) or
// accept (return true so vanilla runs the real ZNet.AddPeer). Disarmed / non-server / any fault
// => pass-through. The patch always attaches (CreateAndPatchAll); the runner gates behaviour.
[HarmonyPatch(typeof(ZNet))]
static class HandshakeResponderPatches {
  [HarmonyPrefix]
  [HarmonyPatch("RPC_PeerInfo", new[] { typeof(ZRpc), typeof(ZPackage) })]
  static bool RpcPeerInfoPrefix(ZRpc rpc, ZPackage pkg) {
    HandshakeResponderRunner runner = HandshakeResponderRunner.Active;
    if (runner == null || !runner.IsRunning) {
      return true;
    }
    if (ZNet.instance == null || !ZNet.instance.IsServer() || rpc == null || pkg == null) {
      return true;
    }

    try {
      // Read a clone at position 0 — network packages arrive at their read cursor, and vanilla must
      // still read the original from the start (the banked i4 SetPos(0) lesson).
      ZPackage clone = new(pkg.GetArray());
      clone.SetPos(0);

      long uid = clone.ReadLong();
      string versionString = clone.ReadString();
      // Modern clients (>= 0.214.301, i.e. every current 0.22x build) always carry the network
      // version uint here (SendPeerInfo:788). We target the current build; an ancient client that
      // omitted it would misalign and be caught by the fail-open path below, and vanilla would
      // reject it on the version gate regardless.
      uint netVersion = clone.ReadUInt();
      Vector3 refPos = clone.ReadVector3();
      string playerName = clone.ReadString();
      // Client-branch fields follow (SendPeerInfo): passwordHash, then the steam session ticket
      // byte[]. We decode the hash for the password gate; the real ticket crypto stays with vanilla
      // on the accept path (VerifySessionTicket needs Steamworks), so ticketValid is reported true.
      string passwordHash = clone.ReadString();

      string hostName = rpc.GetSocket()?.GetHostName() ?? string.Empty;

      HandshakeDecision decision = runner.Decide(
          uid, versionString, netVersion, refPos, playerName, hostName, passwordHash, ticketValid: true);

      if (decision.IsPassThrough || decision.IsAccept) {
        return true;
      }
      rpc.Invoke("Error", decision.ErrorCode);
      return false;
    } catch (Exception exception) {
      // Fail-open: a decode/socket fault must never break a real connection.
      ZLog.LogWarning("[ComfyNetworkSense][handshake] prefix fault (fail-open): "
          + exception.GetType().Name + ": " + exception.Message);
      return true;
    }
  }
}
