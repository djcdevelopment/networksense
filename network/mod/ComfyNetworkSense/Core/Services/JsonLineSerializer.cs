namespace ComfyNetworkSense;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

public static class JsonLineSerializer {
  public static string Serialize(IDictionary<string, object> values) {
    StringBuilder builder = new();
    builder.Append('{');

    bool isFirst = true;

    foreach (KeyValuePair<string, object> pair in values) {
      if (!isFirst) {
        builder.Append(',');
      }

      isFirst = false;
      builder.Append('"');
      builder.Append(Escape(pair.Key));
      builder.Append("\":");
      AppendValue(builder, pair.Value);
    }

    builder.Append('}');
    return builder.ToString();
  }

  static void AppendValue(StringBuilder builder, object value) {
    switch (value) {
      case null:
        builder.Append("null");
        return;
      case string stringValue:
        builder.Append('"');
        builder.Append(Escape(stringValue));
        builder.Append('"');
        return;
      case bool boolValue:
        builder.Append(boolValue ? "true" : "false");
        return;
      case Enum enumValue:
        builder.Append('"');
        builder.Append(enumValue.ToString());
        builder.Append('"');
        return;
      case float floatValue:
        builder.Append(floatValue.ToString("0.###", CultureInfo.InvariantCulture));
        return;
      case double doubleValue:
        builder.Append(doubleValue.ToString("0.###", CultureInfo.InvariantCulture));
        return;
      case decimal decimalValue:
        builder.Append(decimalValue.ToString(CultureInfo.InvariantCulture));
        return;
      case sbyte or byte or short or ushort or int or uint or long or ulong:
        builder.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
        return;
      case IDictionary<string, object> objectDictionary:
        builder.Append(Serialize(objectDictionary));
        return;
      case IEnumerable enumerable:
        builder.Append('[');
        bool isFirst = true;

        foreach (object entry in enumerable) {
          if (!isFirst) {
            builder.Append(',');
          }

          isFirst = false;
          AppendValue(builder, entry);
        }

        builder.Append(']');
        return;
      default:
        builder.Append('"');
        builder.Append(Escape(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty));
        builder.Append('"');
        return;
    }
  }

  static string Escape(string value) {
    if (string.IsNullOrEmpty(value)) {
      return string.Empty;
    }

    StringBuilder builder = new(value.Length + 8);

    foreach (char character in value) {
      switch (character) {
        case '\\':
          builder.Append("\\\\");
          break;
        case '"':
          builder.Append("\\\"");
          break;
        case '\r':
          builder.Append("\\r");
          break;
        case '\n':
          builder.Append("\\n");
          break;
        case '\t':
          builder.Append("\\t");
          break;
        default:
          builder.Append(character);
          break;
      }
    }

    return builder.ToString();
  }
}
