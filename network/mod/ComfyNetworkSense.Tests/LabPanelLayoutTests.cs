using ComfyQuestLab;
using Xunit;

namespace ComfyNetworkSense.Tests;

public sealed class LabPanelLayoutTests {
  [Fact]
  public void Desktop_defaults_remain_unchanged() {
    LabPanelBounds bounds = LabPanelLayout.Clamp(
        80f, 90f, 900f, 620f, 1f, 1920f, 1080f);

    Assert.Equal(80f, bounds.X);
    Assert.Equal(90f, bounds.Y);
    Assert.Equal(900f, bounds.Width);
    Assert.Equal(620f, bounds.Height);
  }

  [Fact]
  public void High_zoom_never_leaves_a_saved_window_wider_than_the_logical_viewport() {
    LabPanelBounds bounds = LabPanelLayout.Clamp(
        9000f, 9000f, 2400f, 1800f, 2f, 800f, 600f);

    Assert.Equal(376f, bounds.Width);
    Assert.Equal(276f, bounds.Height);
    Assert.InRange(bounds.X + bounds.Width, 0f, 400f);
    Assert.InRange(bounds.Y + bounds.Height, 0f, 300f);
  }

  [Fact]
  public void Invalid_persisted_values_recover_to_finite_visible_bounds() {
    LabPanelBounds bounds = LabPanelLayout.Clamp(
        float.NaN, float.PositiveInfinity, float.NaN, float.NegativeInfinity,
        float.NaN, 1280f, 720f);

    Assert.Equal(80f, bounds.X);
    Assert.Equal(90f, bounds.Y);
    Assert.Equal(900f, bounds.Width);
    Assert.Equal(620f, bounds.Height);
  }
}
