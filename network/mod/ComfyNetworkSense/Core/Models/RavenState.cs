namespace ComfyNetworkSense;

public sealed class RavenState {
  public string Status { get; set; } = "Not checked";
  public string LastRequest { get; set; } = string.Empty;
  public string LastResponse { get; set; } = string.Empty;
  public string[] LastBullets { get; set; } = [];
  public string RecommendedProfile { get; set; } = string.Empty;
  public string LastError { get; set; } = string.Empty;
  public string LastUpdatedUtc { get; set; } = string.Empty;
  public bool IsBusy { get; set; }
  public bool IsOnline { get; set; }
}
