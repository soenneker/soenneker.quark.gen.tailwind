using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Soenneker.Quark.Gen.Tailwind.Tests;

public sealed class NativeExecutableTests
{
    [Test]
    public async Task Native_executable_starts_and_rejects_missing_project_arguments()
    {
        string? executable = Environment.GetEnvironmentVariable("QUARK_NATIVE_TOOL");
        if (string.IsNullOrEmpty(executable)) { TUnit.Core.Skip.Test("Set QUARK_NATIVE_TOOL to run native integration tests."); return; }
        using var process = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false })
            ?? throw new Exception("Native tool could not start.");
        await process.WaitForExitAsync();
        if (process.ExitCode != 1) throw new Exception("Expected missing project arguments to fail with exit code 1.");
    }
}
