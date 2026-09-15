using System.Diagnostics;
using MateMCP.Agent.Desktop;

namespace MateMCP.Agent.Tests;

public sealed class NativeComputerUseEndToEndTests
{
    [Fact]
    public async Task Native_secure_field_accepts_agent_local_secret_without_exposing_value()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("MATEMCP_NATIVE_E2E"), "1", StringComparison.Ordinal)) return;
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS()) return;
        if (OperatingSystem.IsWindows()) await EnsureWindowsSemanticHelperAsync();

        using var app = await ControlledNativeApp.StartAsync();
        var vision = new DesktopVisionService();
        var semantic = new SemanticUiService();
        var window = await WaitForWindowAsync(vision, ControlledNativeApp.WindowTitle, app.Process.Id);

        var before = await semantic.SnapshotAsync(window.Id, 500);
        var secure = Assert.Single(before.Elements, element => element.Protected);
        Assert.Null(secure.Value);

        var result = await semantic.FillSecretAsync(window.Id, secure, "native-e2e-secret");
        Assert.True(result.Protected);
        Assert.Null(result.Value);

        var after = await semantic.SnapshotAsync(window.Id, 500);
        var secureAfterFill = Assert.Single(after.Elements, element => element.Protected);
        Assert.Null(secureAfterFill.Value);
    }

    [Fact]
    public async Task Native_visual_semantic_keyboard_and_coordinate_workflow_runs_end_to_end()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("MATEMCP_NATIVE_E2E"), "1", StringComparison.Ordinal)) return;
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS()) return;
        if (OperatingSystem.IsWindows()) await EnsureWindowsSemanticHelperAsync();

        using var app = await ControlledNativeApp.StartAsync();
        var vision = new DesktopVisionService();
        var semantic = new SemanticUiService();
        var input = new DesktopInputService();
        var macSemantic = new MacSemanticUiActionService();
        var started = Stopwatch.StartNew();

        var window = await WaitForWindowAsync(vision, ControlledNativeApp.WindowTitle, app.Process.Id);
        input.FocusWindow(window.Id, window.ProcessId, window.Title);

        var beforeCapture = await vision.CaptureAsync("window", window.Id);
        Assert.Equal("image/png", beforeCapture.MimeType);
        Assert.InRange(beforeCapture.Bytes.Length, 1, DesktopVisionService.MaxCaptureBytes);

        var initial = await semantic.SnapshotAsync(window.Id, 500);
        Assert.False(initial.Truncated);
        var secure = Assert.Single(initial.Elements, e => e.Protected);
        Assert.Null(secure.Value);
        await semantic.FillSecretAsync(window.Id, secure, "native-e2e-secret");
        var afterSecret = await semantic.SnapshotAsync(window.Id, 500);
        var secureAfterFill = Assert.Single(afterSecret.Elements, e => e.Protected);
        Assert.Null(secureAfterFill.Value);
        Assert.Contains(initial.Elements, e => e.Name == "Name" && e.Role is "textbox" or "text field");
        Assert.Contains(initial.Elements, e => e.Name == "Increment");

        await semantic.TypeAsync(window.Id, new UiSelector(AutomationId: "nameInput"), "MateMCP");
        await semantic.ClickAsync(window.Id, new UiSelector(AutomationId: "incrementButton"));

        input.FocusWindow(window.Id, window.ProcessId, window.Title);
        await semantic.FocusAsync(window.Id, new UiSelector(AutomationId: "nameInput"));
        input.PressShortcut([OperatingSystem.IsMacOS() ? "CMD" : "CTRL", "A"]);
        input.TypeText("Shortcut");
        await Task.Delay(150);

        var afterKeyboard = await semantic.SnapshotAsync(window.Id, 500);
        Assert.Contains(afterKeyboard.Elements, e => e.Name == "Name" && e.Value == "Shortcut");

        var pointTarget = afterKeyboard.Elements.FirstOrDefault(e => string.Equals(e.AutomationId, "pointButton", StringComparison.OrdinalIgnoreCase))
            ?? afterKeyboard.Elements.FirstOrDefault(e => e.Name == "Point target")
            ?? throw new InvalidOperationException("Controlled native app point target was not exposed in the semantic snapshot.");
        var bounds = pointTarget.Bounds ?? throw new InvalidOperationException("Controlled native app point target has no bounds.");
        var x = bounds.X + bounds.Width / 2 - window.X;
        var y = bounds.Y + bounds.Height / 2 - window.Y;

        if (OperatingSystem.IsMacOS())
            await macSemantic.ClickAtAsync(window.Id, x, y);
        else
            await semantic.ClickAtAsync(window.Id, x, y);

        await Task.Delay(150);
        var finalSnapshot = await semantic.SnapshotAsync(window.Id, 500);
        Assert.Contains(finalSnapshot.Elements, e => e.Name?.Contains("Point 1", StringComparison.OrdinalIgnoreCase) == true || e.Value?.Contains("Point 1", StringComparison.OrdinalIgnoreCase) == true);

        var afterCapture = await vision.CaptureAsync("window", window.Id);
        Assert.InRange(afterCapture.Bytes.Length, 1, DesktopVisionService.MaxCaptureBytes);
        Console.WriteLine($"[MateMCP native E2E] {(OperatingSystem.IsMacOS() ? "macOS" : "Windows")}: elapsed={started.ElapsedMilliseconds}ms before={beforeCapture.Bytes.Length}B after={afterCapture.Bytes.Length}B elements={finalSnapshot.Elements.Count}");

        app.Stop();
        await WaitForExitAsync(app.Process);
        var stale = await Assert.ThrowsAsync<InvalidOperationException>(() => semantic.SnapshotAsync(window.Id, 50));
        Assert.Contains("no longer available", stale.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task EnsureWindowsSemanticHelperAsync()
    {
        var destination = Path.Combine(AppContext.BaseDirectory, "MateMCP.WindowsDesktopHelper.exe");
        if (File.Exists(destination)) return;

        DirectoryInfo? current = new(AppContext.BaseDirectory);
        string? project = null;
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "src", "MateMCP.WindowsDesktopHelper", "MateMCP.WindowsDesktopHelper.csproj");
            if (File.Exists(candidate))
            {
                project = candidate;
                break;
            }
            current = current.Parent;
        }

        if (project is null)
            throw new InvalidOperationException("Could not locate MateMCP.WindowsDesktopHelper.csproj for the native Windows E2E field test.");

        var build = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        build.ArgumentList.Add("build");
        build.ArgumentList.Add(project);
        build.ArgumentList.Add("-c");
        build.ArgumentList.Add("Release");

        using (var process = Process.Start(build) ?? throw new InvalidOperationException("Could not build the Windows semantic helper."))
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
                throw new InvalidOperationException("Could not build Windows semantic helper: " + await stderr + Environment.NewLine + await stdout);
        }

        var helperRoot = Path.GetDirectoryName(project)!;
        var helper = Directory.EnumerateFiles(Path.Combine(helperRoot, "bin", "Release"), "MateMCP.WindowsDesktopHelper.exe", SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new InvalidOperationException("Windows semantic helper build succeeded but the executable was not found.");
        File.Copy(helper, destination, overwrite: true);
    }

    private static async Task<DesktopWindowInfo> WaitForWindowAsync(DesktopVisionService vision, string title, int processId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var windows = await vision.ListWindowsAsync();
            var found = windows.FirstOrDefault(w => w.ProcessId == processId && w.Title.Contains(title, StringComparison.OrdinalIgnoreCase));
            if (found is not null) return found;
            await Task.Delay(100);
        }
        throw new TimeoutException($"Controlled native app window '{title}' was not discovered within 10 seconds.");
    }

    private static async Task WaitForExitAsync(Process process)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }

    private sealed class ControlledNativeApp : IDisposable
    {
        public const string WindowTitle = "MateMCP Native E2E";
        private readonly string _directory;
        private bool _stopped;

        private ControlledNativeApp(string directory, Process process)
        {
            _directory = directory;
            Process = process;
        }

        public Process Process { get; }

        public static async Task<ControlledNativeApp> StartAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), "MateMCP", "native-e2e", Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(directory);
            try
            {
                var process = OperatingSystem.IsWindows()
                    ? StartWindows(directory)
                    : await StartMacAsync(directory);
                await Task.Delay(400);
                if (process.HasExited) throw new InvalidOperationException($"Controlled native app exited early with code {process.ExitCode}.");
                return new ControlledNativeApp(directory, process);
            }
            catch
            {
                try { Directory.Delete(directory, recursive: true); } catch { }
                throw;
            }
        }

        public void Stop()
        {
            if (_stopped) return;
            _stopped = true;
            try { if (!Process.HasExited) Process.Kill(entireProcessTree: true); } catch { }
        }

        public void Dispose()
        {
            Stop();
            Process.Dispose();
            try { Directory.Delete(_directory, recursive: true); } catch { }
        }

        private static Process StartWindows(string directory)
        {
            const string script = """
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()
$form = New-Object System.Windows.Forms.Form
$form.Text = 'MateMCP Native E2E'
$form.Name = 'nativeE2EForm'
$form.Size = New-Object System.Drawing.Size(620,420)
$form.StartPosition = 'CenterScreen'

$nameLabel = New-Object System.Windows.Forms.Label
$nameLabel.Text = 'Name'
$nameLabel.Location = New-Object System.Drawing.Point(30,35)
$nameLabel.AutoSize = $true
$form.Controls.Add($nameLabel)
$name = New-Object System.Windows.Forms.TextBox
$name.Name = 'nameInput'
$name.AccessibleName = 'Name'
$name.Location = New-Object System.Drawing.Point(30,60)
$name.Size = New-Object System.Drawing.Size(260,28)
$form.Controls.Add($name)

$secureLabel = New-Object System.Windows.Forms.Label
$secureLabel.Text = 'Password'
$secureLabel.Location = New-Object System.Drawing.Point(30,100)
$secureLabel.AutoSize = $true
$form.Controls.Add($secureLabel)
$secure = New-Object System.Windows.Forms.TextBox
$secure.Name = 'passwordInput'
$secure.AccessibleName = 'Password'
$secure.UseSystemPasswordChar = $true
$secure.Text = 'fixture-value'
$secure.Location = New-Object System.Drawing.Point(30,125)
$secure.Size = New-Object System.Drawing.Size(260,28)
$form.Controls.Add($secure)

$count = New-Object System.Windows.Forms.Label
$count.Name = 'countLabel'
$count.AccessibleName = 'Count 0'
$count.Text = 'Count 0'
$count.Location = New-Object System.Drawing.Point(330,65)
$count.AutoSize = $true
$form.Controls.Add($count)
$increment = New-Object System.Windows.Forms.Button
$increment.Name = 'incrementButton'
$increment.AccessibleName = 'Increment'
$increment.Text = 'Increment'
$increment.Location = New-Object System.Drawing.Point(330,95)
$increment.Size = New-Object System.Drawing.Size(120,36)
$counter = 0
$increment.Add_Click({ $script:counter++; $count.Text = "Count $script:counter"; $count.AccessibleName = $count.Text })
$form.Controls.Add($increment)

$pointStatus = New-Object System.Windows.Forms.Label
$pointStatus.Name = 'pointStatus'
$pointStatus.AccessibleName = 'Point 0'
$pointStatus.Text = 'Point 0'
$pointStatus.Location = New-Object System.Drawing.Point(330,180)
$pointStatus.AutoSize = $true
$form.Controls.Add($pointStatus)
$point = New-Object System.Windows.Forms.Button
$point.Name = 'pointButton'
$point.AccessibleName = 'Point target'
$point.Text = 'Point target'
$point.Location = New-Object System.Drawing.Point(330,210)
$point.Size = New-Object System.Drawing.Size(120,42)
$pointClicks = 0
$point.Add_Click({ $script:pointClicks++; $pointStatus.Text = "Point $script:pointClicks"; $pointStatus.AccessibleName = $pointStatus.Text })
$form.Controls.Add($point)

$form.Add_Shown({ $form.Activate() })
[System.Windows.Forms.Application]::Run($form)
""";
            var path = Path.Combine(directory, "native-e2e.ps1");
            File.WriteAllText(path, script);
            var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-STA");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(path);
            return Process.Start(psi) ?? throw new InvalidOperationException("Could not start Windows native E2E fixture.");
        }

        private static async Task<Process> StartMacAsync(string directory)
        {
            const string source = """
import AppKit

final class AppDelegate: NSObject, NSApplicationDelegate {
    var window: NSWindow!
    var countLabel: NSTextField!
    var pointLabel: NSTextField!
    var count = 0
    var pointCount = 0

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.regular)

        let mainMenu = NSMenu()
        let appItem = NSMenuItem()
        mainMenu.addItem(appItem)
        let appMenu = NSMenu()
        appMenu.addItem(withTitle: "Quit", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        appItem.submenu = appMenu

        let editItem = NSMenuItem()
        mainMenu.addItem(editItem)
        let editMenu = NSMenu(title: "Edit")
        editMenu.addItem(withTitle: "Select All", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
        editItem.submenu = editMenu
        NSApp.mainMenu = mainMenu

        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 620, height: 420), styleMask: [.titled, .closable, .miniaturizable, .resizable], backing: .buffered, defer: false)
        window.title = "MateMCP Native E2E"
        let content = window.contentView!

        let nameLabel = NSTextField(labelWithString: "Name")
        nameLabel.frame = NSRect(x: 30, y: 330, width: 120, height: 24)
        content.addSubview(nameLabel)
        let name = NSTextField(frame: NSRect(x: 30, y: 295, width: 260, height: 28))
        name.identifier = NSUserInterfaceItemIdentifier("nameInput")
        name.setAccessibilityLabel("Name")
        content.addSubview(name)

        let secureLabel = NSTextField(labelWithString: "Password")
        secureLabel.frame = NSRect(x: 30, y: 250, width: 120, height: 24)
        content.addSubview(secureLabel)
        let secure = NSSecureTextField(frame: NSRect(x: 30, y: 215, width: 260, height: 28))
        secure.identifier = NSUserInterfaceItemIdentifier("passwordInput")
        secure.setAccessibilityLabel("Password")
        secure.stringValue = "fixture-value"
        content.addSubview(secure)

        countLabel = NSTextField(labelWithString: "Count 0")
        countLabel.identifier = NSUserInterfaceItemIdentifier("countLabel")
        countLabel.frame = NSRect(x: 340, y: 320, width: 160, height: 24)
        content.addSubview(countLabel)
        let increment = NSButton(title: "Increment", target: self, action: #selector(incrementClicked))
        increment.identifier = NSUserInterfaceItemIdentifier("incrementButton")
        increment.setAccessibilityLabel("Increment")
        increment.frame = NSRect(x: 340, y: 270, width: 130, height: 36)
        content.addSubview(increment)

        pointLabel = NSTextField(labelWithString: "Point 0")
        pointLabel.identifier = NSUserInterfaceItemIdentifier("pointStatus")
        pointLabel.frame = NSRect(x: 340, y: 205, width: 160, height: 24)
        content.addSubview(pointLabel)
        let point = NSButton(title: "Point target", target: self, action: #selector(pointClicked))
        point.identifier = NSUserInterfaceItemIdentifier("pointButton")
        point.setAccessibilityLabel("Point target")
        point.frame = NSRect(x: 340, y: 150, width: 130, height: 40)
        content.addSubview(point)

        window.center()
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    @objc func incrementClicked() {
        count += 1
        countLabel.stringValue = "Count \(count)"
    }

    @objc func pointClicked() {
        pointCount += 1
        pointLabel.stringValue = "Point \(pointCount)"
    }
}

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.run()
""";
            var sourcePath = Path.Combine(directory, "NativeE2E.swift");
            var executablePath = Path.Combine(directory, "MateMCP-Native-E2E");
            await File.WriteAllTextAsync(sourcePath, source);
            var compile = new ProcessStartInfo("/usr/bin/xcrun") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            compile.ArgumentList.Add("swiftc");
            compile.ArgumentList.Add("-framework");
            compile.ArgumentList.Add("AppKit");
            compile.ArgumentList.Add("-o");
            compile.ArgumentList.Add(executablePath);
            compile.ArgumentList.Add(sourcePath);
            using (var compiler = Process.Start(compile) ?? throw new InvalidOperationException("Could not start Swift compiler for native E2E fixture."))
            {
                await compiler.WaitForExitAsync();
                if (compiler.ExitCode != 0)
                    throw new InvalidOperationException("Could not compile macOS native E2E fixture: " + await compiler.StandardError.ReadToEndAsync());
            }
            return Process.Start(new ProcessStartInfo(executablePath) { UseShellExecute = false })
                ?? throw new InvalidOperationException("Could not start macOS native E2E fixture.");
        }
    }
}
