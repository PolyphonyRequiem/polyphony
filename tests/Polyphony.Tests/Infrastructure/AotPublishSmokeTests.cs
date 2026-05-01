using System.Diagnostics;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Infrastructure;

/// <summary>
/// Smoke tests that verify the AOT-published binary starts and responds correctly.
/// These tests require a prior <c>dotnet publish -c Release</c> and are skipped
/// when the published binary is not found.
/// </summary>
public sealed class AotPublishSmokeTests
{
    private static readonly string PublishDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "Polyphony", "bin", "Release", "net11.0", "win-x64", "publish"));

    private static readonly string BinaryPath = Path.Combine(PublishDir,
        OperatingSystem.IsWindows() ? "polyphony.exe" : "polyphony");

    private static bool BinaryExists => File.Exists(BinaryPath);

    [Fact]
    public async Task AotBinary_Help_ExitsWithZeroAndShowsCommands()
    {
        if (!BinaryExists)
        {
            return; // Skip: AOT binary not published. Run 'dotnet publish -c Release' first.
        }

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = BinaryPath,
            Arguments = "--help",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        process.ExitCode.ShouldBe(0);
        stdout.ShouldContain("route");
        stdout.ShouldContain("validate");
        stdout.ShouldContain("hierarchy");
    }

    [Fact]
    public async Task AotBinary_Version_ExitsWithZero()
    {
        if (!BinaryExists)
        {
            return; // Skip: AOT binary not published. Run 'dotnet publish -c Release' first.
        }

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = BinaryPath,
            Arguments = "--version",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        process.ExitCode.ShouldBe(0);
        stdout.Trim().ShouldNotBeNullOrWhiteSpace();
    }
}
