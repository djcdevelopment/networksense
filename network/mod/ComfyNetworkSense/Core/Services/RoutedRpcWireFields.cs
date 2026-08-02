namespace ComfyNetworkSense;

using System.Globalization;

// Keep the hand-built net48 WebSocket payload isolated and executable in the
// Unity-free test project. A missing delimiter here otherwise appears only
// after a physical client reaches the routed-RPC lane.
internal static class RoutedRpcWireFields {
  internal static string Build(
      string runId,
      string actionId,
      string routeId,
      long messageId,
      long senderPeerId,
      long targetPeerId,
      long targetZdoUserId,
      uint targetZdoId,
      string targetKind,
      string methodName,
      int methodHash,
      string parametersBase64,
      string deliveryMode) =>
      "\"run_id\":\"" + Escape(runId)
      + "\",\"action_id\":\"" + Escape(actionId)
      + "\",\"route_id\":\"" + Escape(routeId)
      + "\",\"message_id\":" + messageId.ToString(CultureInfo.InvariantCulture)
      + ",\"sender_peer_id\":" + senderPeerId.ToString(CultureInfo.InvariantCulture)
      + ",\"target_peer_id\":" + targetPeerId.ToString(CultureInfo.InvariantCulture)
      + ",\"target_zdo_user_id\":"
      + targetZdoUserId.ToString(CultureInfo.InvariantCulture)
      + ",\"target_zdo_id\":" + targetZdoId.ToString(CultureInfo.InvariantCulture)
      + ",\"target_kind\":\"" + Escape(targetKind)
      + "\",\"method_name\":\"" + Escape(methodName)
      + "\",\"method_hash\":" + methodHash.ToString(CultureInfo.InvariantCulture)
      + ",\"parameters_base64\":\"" + Escape(parametersBase64)
      + "\",\"delivery_mode\":\"" + Escape(deliveryMode) + "\"";

  static string Escape(string value) =>
      (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"")
          .Replace("\r", "_").Replace("\n", "_");
}
