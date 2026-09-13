using MateMCP.Agent.Desktop;

namespace MateMCP.Agent.Tests;

public sealed class ComputerUsePreviewServiceTests
{
    [Fact]
    public void NormalizeCursor_uses_window_relative_coordinates()
    {
        var window = new DesktopWindowInfo("w", "title", "app", 1, 100, 200, 800, 600, false, false);
        var bounds = new UiRect(300, 350, 200, 100);

        var cursor = ComputerUsePreviewService.NormalizeCursor(window, bounds);

        Assert.Equal(0.375d, cursor.X, 6);
        Assert.Equal(1d / 3d, cursor.Y, 6);
    }

    [Fact]
    public void NormalizeCursor_clamps_elements_outside_window_bounds()
    {
        var window = new DesktopWindowInfo("w", "title", "app", 1, 100, 200, 800, 600, false, false);

        Assert.Equal((0d, 0d), ComputerUsePreviewService.NormalizeCursor(window, new UiRect(-100, -100, 10, 10)));
        Assert.Equal((1d, 1d), ComputerUsePreviewService.NormalizeCursor(window, new UiRect(2000, 2000, 10, 10)));
    }
}
