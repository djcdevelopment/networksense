namespace ComfyNetworkSense;

using UnityEngine;

public static class NetworkSensePanelTheme {
  public static readonly Color Panel = New("071014");
  public static readonly Color Inset = New("0d1a20");
  public static readonly Color Header = New("101f27");
  public static readonly Color Line = New("263c46");
  public static readonly Color Glass = New("0a161b");
  public static readonly Color StatusGreen = New("7bd88f");
  public static readonly Color StatusAmber = New("e0b85a");
  public static readonly Color StatusRust = New("e06b5f");

  public const string Text = "#e8f1f2";
  public const string Dim = "#b3c4c8";
  public const string Faint = "#8ea2a8";
  public const string Accent = "#67d9e8";
  public const string Good = "#7bd88f";
  public const string Warn = "#e0b85a";
  public const string Bad = "#e06b5f";
  public const string Blue = "#8bc7d8";

  public const string MarkPulse = "*";
  public const string MarkGood = "+";
  public const string MarkWarn = "!";
  public const string MarkBar = "|";

  static Texture2D _panelTex;
  static Texture2D _insetTex;
  static Texture2D _headerTex;
  static Texture2D _lineTex;
  static Texture2D _glassTex;
  static Texture2D _statusGreenTex;
  static Texture2D _statusAmberTex;
  static Texture2D _statusRustTex;
  static Font _serif;
  static bool _fontSearched;

  public static Texture2D PanelTex => _panelTex ??= MakeTex(Panel);
  public static Texture2D InsetTex => _insetTex ??= MakeTex(Inset);
  public static Texture2D HeaderTex => _headerTex ??= MakeTex(Header);
  public static Texture2D LineTex => _lineTex ??= MakeTex(Line);
  public static Texture2D GlassTex => _glassTex ??= MakeTex(Glass);
  public static Texture2D StatusGreenTex => _statusGreenTex ??= MakeTex(StatusGreen);
  public static Texture2D StatusAmberTex => _statusAmberTex ??= MakeTex(StatusAmber);
  public static Texture2D StatusRustTex => _statusRustTex ??= MakeTex(StatusRust);

  public static Font Serif {
    get {
      if (!_fontSearched) {
        _fontSearched = true;
        foreach (Font font in Resources.FindObjectsOfTypeAll<Font>()) {
          if (font != null && font.name.StartsWith("AveriaSerifLibre")) {
            _serif = font;
            break;
          }
        }
      }

      return _serif;
    }
  }

  static Color New(string hex) {
    return ColorUtility.TryParseHtmlString("#" + hex, out Color color) ? color : Color.magenta;
  }

  static Texture2D MakeTex(Color color) {
    Texture2D texture = new(1, 1, TextureFormat.RGBA32, mipChain: false) {
        hideFlags = HideFlags.HideAndDontSave
    };
    texture.SetPixel(0, 0, color);
    texture.Apply();
    return texture;
  }
}
