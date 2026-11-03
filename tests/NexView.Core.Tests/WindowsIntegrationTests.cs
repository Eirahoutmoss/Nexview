using Microsoft.Win32;
using NexView.Platform.Windows;

namespace NexView.Core.Tests;

public class WindowsIntegrationTests
{
    [Fact]
    public void RegistersAndCleansUp()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = @"Software\NexViewTest-" + Guid.NewGuid().ToString("N");
        try
        {
            var shell = new ShellIntegration(@"C:\Test\NexView.exe", root);
            var exts = new[] { ".jpg", ".heic", ".cr3" };
            shell.RegisterFileTypes(exts);
            shell.RegisterContextMenu(exts);
            Assert.True(shell.IsFileTypesRegistered);
            Assert.True(shell.IsContextMenuRegistered);
            Assert.Equal(@"C:\Test\NexView.exe", shell.RegisteredExePath());
            using (var owp = Registry.CurrentUser.OpenSubKey($@"{root}\Classes\.heic\OpenWithProgids"))
                Assert.Contains(ShellIntegration.ProgId, owp!.GetValueNames());
            using (var cmd = Registry.CurrentUser.OpenSubKey($@"{root}\Classes\SystemFileAssociations\.cr3\shell\NexView\shell\b_jpeg\command"))
                Assert.Equal("\"C:\\Test\\NexView.exe\" --convert jpeg \"%1\"", cmd!.GetValue(""));

            shell.UnregisterAll();
            Assert.False(shell.IsFileTypesRegistered);
            Assert.False(shell.IsContextMenuRegistered);
            Assert.Null(Registry.CurrentUser.OpenSubKey($@"{root}\Classes\{ShellIntegration.ProgId}"));
            using (var owp = Registry.CurrentUser.OpenSubKey($@"{root}\Classes\.heic\OpenWithProgids"))
                Assert.DoesNotContain(ShellIntegration.ProgId, owp?.GetValueNames() ?? []);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(root, false);
        }
    }

    [Fact]
    public void GroupsCoverRawAndCommonFormats()
    {
        if (!OperatingSystem.IsWindows()) return;
        var raw = ShellIntegration.Groups.Single(g => g.Id == "RAW");
        Assert.Contains(".cr3", raw.Extensions);
        Assert.Contains(".arw", raw.Extensions);
        Assert.Contains(ShellIntegration.Groups, g => g.Id == "HEIC");
    }

    [Fact]
    public void CommandLineOptionsResolve()
    {
        Assert.Equal("JPEG", Core.Conversion.Presets.Resolve("jpeg")!.Target.Id);
        Assert.Equal("PDF", Core.Conversion.Presets.Resolve("pdf")!.Target.Id);
        Assert.Equal(1024 * 1024, Core.Conversion.Presets.Resolve("official")!.TargetSizeBytes);
        Assert.Null(Core.Conversion.Presets.Resolve("bilinmeyen"));
    }
}
