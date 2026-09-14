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
    [Fact]
    public void Windows_preview_uses_native_graphics_capture_stream_when_helper_is_packaged()
    {
        var root = FindRepositoryRoot();
        var service = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent", "Desktop", "ComputerUsePreviewService.cs"));
        var stream = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent", "Desktop", "WindowsGraphicsCapturePreviewStream.cs"));
        var helper = File.ReadAllText(Path.Combine(root, "src", "MateMCP.WindowsCaptureHelper", "main.cpp"));

        Assert.Contains("WindowsGraphicsCapturePreviewStream", service, StringComparison.Ordinal);
        Assert.Contains("_windowsStream.WaitForFrameAsync", service, StringComparison.Ordinal);
        Assert.Contains("windows-graphics-capture-stream", stream, StringComparison.Ordinal);
        Assert.Contains("MateMCP.WindowsCaptureHelper.exe", stream, StringComparison.Ordinal);
        Assert.Contains("Direct3D11CaptureFramePool::CreateFreeThreaded", helper, StringComparison.Ordinal);
        Assert.Contains("CreateForWindow", helper, StringComparison.Ordinal);
        Assert.Contains("IsCursorCaptureEnabled(false)", helper, StringComparison.Ordinal);
        Assert.Contains("BitmapEncoder::JpegEncoderId", helper, StringComparison.Ordinal);
    }

    [Fact]
    public void Windows_preview_helper_is_staged_into_agent_and_desktop_packages()
    {
        var root = FindRepositoryRoot();
        var build = File.ReadAllText(Path.Combine(root, ".github", "workflows", "build.yml"));
        var desktop = File.ReadAllText(Path.Combine(root, ".github", "workflows", "companion-build.yml"));

        Assert.Contains("Build Windows Graphics Capture helper", build, StringComparison.Ordinal);
        Assert.Contains("MateMCP.WindowsCaptureHelper.exe", build, StringComparison.Ordinal);
        Assert.Contains("Build Windows Graphics Capture helper", desktop, StringComparison.Ordinal);
        Assert.Contains("Installed Windows Graphics Capture helper missing", desktop, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "MateMCP.slnx"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not find repository root.");
    }

}
