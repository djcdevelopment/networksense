namespace ComfyNetworkSense.Tests;

using System.Text.Json;
using Xunit;

public class RoutedRpcWireFieldsTests {
  [Fact]
  public void Typed_target_and_method_are_distinct_valid_json_fields() {
    string fields = RoutedRpcWireFields.Build(
        "run-1",
        "drive-1",
        "route-1",
        11,
        22,
        33,
        44,
        55,
        "ship",
        "RequestControl",
        66,
        "AAECAw==",
        "deliver");

    using JsonDocument document = JsonDocument.Parse("{" + fields + "}");
    JsonElement root = document.RootElement;
    Assert.Equal("ship", root.GetProperty("target_kind").GetString());
    Assert.Equal("RequestControl", root.GetProperty("method_name").GetString());
    Assert.Equal(55u, root.GetProperty("target_zdo_id").GetUInt32());
    Assert.Equal("deliver", root.GetProperty("delivery_mode").GetString());
  }

  [Fact]
  public void String_fields_remain_json_safe() {
    string fields = RoutedRpcWireFields.Build(
        "run-1",
        "drive-1",
        "route-1",
        1,
        2,
        3,
        4,
        5,
        "ship",
        "method\"name",
        6,
        "C:\\payload",
        "deliver");

    using JsonDocument document = JsonDocument.Parse("{" + fields + "}");
    Assert.Equal(
        "method\"name",
        document.RootElement.GetProperty("method_name").GetString());
    Assert.Equal(
        "C:\\payload",
        document.RootElement.GetProperty("parameters_base64").GetString());
  }
}
