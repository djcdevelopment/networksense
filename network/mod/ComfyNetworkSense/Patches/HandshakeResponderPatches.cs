namespace ComfyNetworkSense;

using System;
using System.Reflection;

using HarmonyLib;

using UnityEngine;

// P6/I5 server-side handshake interceptor. The prefix only decodes and banks the request; it never
// performs network I/O. HandshakeResponderRunner asks Lumberjacks on a worker and, once the verdict
// is ready, re-enters this method on Unity's main thread. A one-shot bypass lets that replay reach
// vanilla's real ZNet.AddPeer without being deferred a second time.
[HarmonyPatch(typeof(ZNet))]
static class HandshakeResponderPatches {
  static readonly MethodInfo RpcPeerInfoMethod = AccessTools.Method(
      typeof(ZNet), "RPC_PeerInfo", new[] { typeof(ZRpc), typeof(ZPackage) });

  [HarmonyPrefix]
  [HarmonyPatch("RPC_PeerInfo", new[] { typeof(ZRpc), typeof(ZPackage) })]
  static bool RpcPeerInfoPrefix(ZRpc rpc, ZPackage pkg) {
    HandshakeResponderRunner runner = HandshakeResponderRunner.Active;
    if (runner == null) {
      return true;
    }
    if (runner.ConsumeVanillaResume(rpc)) {
      return true;
    }
    if (!runner.IsRunning) {
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

      // True means the runner owns this call until a worker verdict is drained on Update. A false
      // return means it could not safely bank the request, so vanilla receives the original package.
      bool deferred = runner.TryDefer(
          rpc,
          (byte[]) pkg.GetArray().Clone(),
          uid,
          versionString,
          netVersion,
          refPos,
          playerName,
          hostName,
          passwordHash,
          ticketValid: true);
      return !deferred;
    } catch (Exception exception) {
      // Fail-open: a decode/banking fault must never break a real connection.
      ZLog.LogWarning("[ComfyNetworkSense][handshake] prefix fault (fail-open): "
          + exception.GetType().Name + ": " + exception.Message);
      return true;
    }
  }

  internal static void ResumeVanilla(ZRpc rpc, byte[] packageBytes) {
    if (ZNet.instance == null || rpc == null || packageBytes == null) {
      return;
    }
    if (RpcPeerInfoMethod == null) {
      throw new MissingMethodException(typeof(ZNet).FullName, "RPC_PeerInfo");
    }

    ZPackage replay = new(packageBytes);
    replay.SetPos(0);
    RpcPeerInfoMethod.Invoke(ZNet.instance, new object[] { rpc, replay });
  }
}
