namespace ComfyNetworkSense;

using System;

static class Program {
  static int Main() {
    ExpectMcpSwitchDefaultsOff();

    ExpectAccepted("http://127.0.0.1:8721", "/healthz", "http://127.0.0.1:8721/healthz");
    ExpectAccepted("https://localhost:9443/", "/valheim/report?sample_count=30",
        "https://localhost:9443/valheim/report?sample_count=30");
    ExpectAccepted("http://[::1]:8721", "/healthz", "http://[::1]:8721/healthz");

    ExpectRejected("http://192.168.1.20:8721", "/healthz");
    ExpectRejected("http://example.com:8721", "/healthz");
    ExpectRejected("file:///tmp/gateway", "/healthz");
    ExpectRejected("http://127.0.0.1:8721/prefix", "/healthz");
    ExpectRejected("http://user@127.0.0.1:8721", "/healthz");
    ExpectRejected("http://127.0.0.1:8721", "//example.com/healthz");

    Console.WriteLine("MCP boundary tests passed.");
    return 0;
  }

  static void ExpectMcpSwitchDefaultsOff() {
    if (AlphaTransportSwitches.McpEnabled) {
      throw new Exception("MCP switch must start disabled.");
    }

    AlphaTransportSwitches.SetMcpEnabled(true);
    AlphaTransportSwitches.Reset();
    if (AlphaTransportSwitches.McpEnabled) {
      throw new Exception("MCP switch must reset disabled without an explicit opt-in.");
    }

    AlphaTransportSwitches.Reset(mcpEnabled: true);
    if (!AlphaTransportSwitches.McpEnabled) {
      throw new Exception("MCP switch must honor an explicit opt-in.");
    }
    AlphaTransportSwitches.Reset();
  }

  static void ExpectAccepted(string gateway, string path, string expected) {
    if (!McpGatewayEndpoint.TryCreate(gateway, path, out Uri endpoint, out string error)) {
      throw new Exception("Expected accepted endpoint, got: " + error);
    }
    if (endpoint.AbsoluteUri != expected) {
      throw new Exception("Expected '" + expected + "', got '" + endpoint.AbsoluteUri + "'.");
    }
  }

  static void ExpectRejected(string gateway, string path) {
    if (McpGatewayEndpoint.TryCreate(gateway, path, out Uri endpoint, out _)) {
      throw new Exception("Expected rejection, got: " + endpoint.AbsoluteUri);
    }
  }
}
