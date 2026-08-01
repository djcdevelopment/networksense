namespace ComfyNetworkSense;

using System;

/// <summary>
/// Builds MCP helper endpoints from one explicitly configured, loopback-only gateway root.
/// Keeping this validation Unity-free makes the development boundary independently testable.
/// </summary>
internal static class McpGatewayEndpoint {
  public static bool TryCreate(
      string gatewayUrl, string pathAndQuery, out Uri endpoint, out string error) {
    endpoint = null;
    error = string.Empty;

    if (!Uri.TryCreate(gatewayUrl?.Trim(), UriKind.Absolute, out Uri gateway)) {
      error = "MCP gateway URL must be an absolute HTTP(S) URL.";
      return false;
    }

    if (gateway.Scheme != Uri.UriSchemeHttp && gateway.Scheme != Uri.UriSchemeHttps) {
      error = "MCP gateway URL must use HTTP or HTTPS.";
      return false;
    }

    if (!gateway.IsLoopback) {
      error = "MCP gateway URL must resolve to the local loopback host.";
      return false;
    }

    if (!string.IsNullOrEmpty(gateway.UserInfo)
        || !string.IsNullOrEmpty(gateway.Query)
        || !string.IsNullOrEmpty(gateway.Fragment)
        || (gateway.AbsolutePath != "/" && gateway.AbsolutePath.Length != 0)) {
      error = "MCP gateway URL must be a loopback origin without credentials, a path, query, or fragment.";
      return false;
    }

    if (string.IsNullOrWhiteSpace(pathAndQuery)
        || pathAndQuery[0] != '/'
        || pathAndQuery.StartsWith("//", StringComparison.Ordinal)) {
      error = "MCP endpoint path must be root-relative.";
      return false;
    }

    endpoint = new Uri(gateway.GetLeftPart(UriPartial.Authority) + pathAndQuery, UriKind.Absolute);
    return true;
  }
}
