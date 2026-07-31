namespace ComfyNetworkSense.Tests;

using System;
using System.IO;
using ComfyNetworkSense;
using Xunit;

public class ParseHexStrictTests {
  [Theory]
  [InlineData("0", 0)]
  [InlineData("b", 11)]
  [InlineData("B", 11)]
  [InlineData("1a", 26)]
  [InlineData("1A", 26)]
  [InlineData("ff", 255)]
  [InlineData("7fffffff", int.MaxValue)]
  public void Parses_bare_hex(string text, int expected) =>
      Assert.Equal(expected, WireParsers.ParseHexStrict(text.AsSpan()));

  [Theory]
  [InlineData("")]
  [InlineData("zz")]
  [InlineData("1a;ext=1")]
  [InlineData("0x1a")]
  [InlineData("1a 2")]
  public void Rejects_non_hex_loudly(string text) =>
      Assert.Throws<FormatException>(() => WireParsers.ParseHexStrict(text.AsSpan()));

  [Fact]
  public void Overflow_is_loud() =>
      Assert.Throws<OverflowException>(() => WireParsers.ParseHexStrict("ffffffff".AsSpan()));
}

public class DecodeChunkedBodyTests {
  [Fact]
  public void Decodes_single_chunk() =>
      Assert.Equal("hello world", WireParsers.DecodeChunkedBody("b\r\nhello world\r\n0\r\n\r\n"));

  [Fact]
  public void Decodes_multiple_chunks_with_mixed_case_sizes() {
    string body = "A\r\n0123456789\r\n1a\r\nabcdefghijklmnopqrstuvwxyz\r\n0\r\n\r\n";
    Assert.Equal("0123456789abcdefghijklmnopqrstuvwxyz", WireParsers.DecodeChunkedBody(body));
  }

  [Fact]
  public void Size_line_whitespace_is_trimmed() =>
      Assert.Equal("hi", WireParsers.DecodeChunkedBody(" 2 \r\nhi\r\n0\r\n\r\n"));

  [Fact]
  public void Garbage_size_line_throws_instead_of_truncating() =>
      Assert.Throws<FormatException>(() => WireParsers.DecodeChunkedBody("xyz\r\nhello\r\n0\r\n\r\n"));

  [Fact]
  public void Chunk_extension_is_rejected_not_mangled() =>
      Assert.Throws<FormatException>(() => WireParsers.DecodeChunkedBody("2;name=v\r\nhi\r\n0\r\n\r\n"));

  [Fact]
  public void Truncated_chunk_throws() =>
      Assert.Throws<InvalidDataException>(() => WireParsers.DecodeChunkedBody("ff\r\nshort\r\n"));

  [Fact]
  public void Missing_size_terminator_returns_decoded_prefix() =>
      Assert.Equal("hi", WireParsers.DecodeChunkedBody("2\r\nhi\r\n5"));

  [Fact]
  public void Empty_payload_is_empty() =>
      Assert.Equal(string.Empty, WireParsers.DecodeChunkedBody(""));

  [Fact]
  public void Zero_chunk_ends_decoding() =>
      Assert.Equal("AB", WireParsers.DecodeChunkedBody("2\r\nAB\r\n0\r\ntrailers-ignored"));
}

public class JsonScanTests {
  // Real receipt row shape from fieldlab/runs/native-valheim/native-20260731-c8-full26.
  const string ScenarioReceipt =
      "{\"schema_version\":1,\"timestamp_utc\":\"2026-07-31T13:17:39.1554287+00:00\"," +
      "\"state\":\"action_started\",\"run_id\":\"native-20260731-c8-full26\"," +
      "\"client\":\"omen\",\"action_id\":\"c8-ownership-contended\",\"detail\":\"kind=ownership_lease_pickup\"}";

  [Fact]
  public void Extracts_property_strings_from_real_receipt() {
    Assert.Equal(new[] { "native-20260731-c8-full26" },
        WireParsers.ExtractJsonPropertyStringValues(ScenarioReceipt, "run_id"));
    Assert.Equal(new[] { "action_started" },
        WireParsers.ExtractJsonPropertyStringValues(ScenarioReceipt, "state"));
  }

  [Fact]
  public void Collects_every_occurrence_across_concatenated_rows() {
    string rows = ScenarioReceipt + "\n" + ScenarioReceipt.Replace("action_started", "completed");
    Assert.Equal(new[] { "action_started", "completed" },
        WireParsers.ExtractJsonPropertyStringValues(rows, "state"));
  }

  [Fact]
  public void Escaped_quotes_and_backslashes_round_trip() {
    string json = "{\"reason\":\"cap \\\"raised\\\" via C:\\\\lab\"}";
    Assert.Equal(new[] { "cap \"raised\" via C:\\lab" },
        WireParsers.ExtractJsonPropertyStringValues(json, "reason"));
  }

  [Fact]
  public void String_array_extraction() {
    string json = "{\"labels\":[\"a\",\"b c\",\"d\\\"e\"],\"other\":[\"x\"]}";
    Assert.Equal(new[] { "a", "b c", "d\"e" },
        WireParsers.ExtractJsonStringArray(json, "labels"));
    Assert.Equal(new[] { "x" }, WireParsers.ExtractJsonStringArray(json, "other"));
    Assert.Empty(WireParsers.ExtractJsonStringArray(json, "missing"));
  }

  [Fact]
  public void Number_extraction_is_verbatim_not_reformatted() {
    string json = "{\"fps\":59.94,\"frame_time_p95_ms\":18.20,\"cpu_bound_estimate\":-0.41,\"count\":7}";
    Assert.Equal("59.94", WireParsers.ExtractJsonPropertyNumber(json, "fps"));
    Assert.Equal("18.20", WireParsers.ExtractJsonPropertyNumber(json, "frame_time_p95_ms"));
    Assert.Equal("-0.41", WireParsers.ExtractJsonPropertyNumber(json, "cpu_bound_estimate"));
    Assert.Equal("7", WireParsers.ExtractJsonPropertyNumber(json, "count"));
    Assert.Equal(string.Empty, WireParsers.ExtractJsonPropertyNumber(json, "missing"));
  }
}
