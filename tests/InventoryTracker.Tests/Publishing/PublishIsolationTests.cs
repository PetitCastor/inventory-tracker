using System.Diagnostics;
using System.Text.RegularExpressions;

namespace InventoryTracker.Tests.Publishing;

public sealed class PublishIsolationTests
{
    [Fact]
    public void DistOther_process_is_not_stopped_by_publish()
    {
        using var repository = new FakeRepository();
        var result = repository.RunPublish(mockProcessPath: Path.Combine(repository.DistOther, "InventoryTracker.exe"));

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Equal("enumerated", File.ReadAllText(repository.ProcessLog).TrimEnd('\r', '\n'));
    }

    [Fact]
    public void Explicit_output_path_preserves_dist_and_running_dist_process()
    {
        using var repository = new FakeRepository();
        Directory.CreateDirectory(repository.Dist);
        var existingFile = Path.Combine(repository.Dist, "keep-me.txt");
        File.WriteAllText(existingFile, "existing dist contents");
        var result = repository.RunPublish(repository.IsolatedOutput,
            mockProcessPath: Path.Combine(repository.Dist, "InventoryTracker.exe"));

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Equal("existing dist contents", File.ReadAllText(existingFile));
        Assert.False(File.Exists(repository.ProcessLog),
            "An explicitly isolated publish must not enumerate or stop running instances.");
        Assert.True(File.Exists(Path.Combine(repository.IsolatedOutput, "InventoryTracker.exe")),
            "The fake dotnet publish should have produced its executable in the requested output directory.");
    }

    [Fact]
    public void Explicit_output_path_through_junction_into_dist_is_rejected_before_publish()
    {
        using var repository = new FakeRepository();
        Directory.CreateDirectory(repository.Dist);
        repository.CreateJunction(repository.DistJunction, repository.Dist);

        try
        {
            var outputPath = Path.Combine(repository.DistJunction, "isolated-output");
            var result = repository.RunPublish(outputPath,
                mockProcessPath: Path.Combine(repository.Dist, "InventoryTracker.exe"));

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("reparse point", result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(repository.ProcessLog),
                "The rejected isolated path must not enumerate or stop processes.");
            Assert.False(File.Exists(repository.DotnetLog),
                "The rejected isolated path must fail before invoking dotnet.");
        }
        finally
        {
            repository.RemoveJunction(repository.DistJunction);
        }
    }

    [Fact]
    public void Publish_job_serializes_runs_for_the_repository()
    {
        var workflow = File.ReadAllText(Path.Combine(FindRepositoryRoot(), ".github", "workflows", "ci.yml"));
        var publishJob = Regex.Match(workflow,
            "(?ms)^  publish:\\r?\\n(?<body>(?:[ \\t].*\\r?\\n|\\r?\\n)*)");

        Assert.True(publishJob.Success, "Could not find the publish job in ci.yml.");
        Assert.Matches(new Regex("(?m)^    concurrency:\\s*$"), publishJob.Groups["body"].Value);
        Assert.Matches(new Regex("(?m)^      group:\\s*.*\\$\\{\\{\\s*github\\.repository\\s*\\}\\}"),
            publishJob.Groups["body"].Value);
        Assert.Matches(new Regex("(?m)^      cancel-in-progress:\\s*false\\s*$"),
            publishJob.Groups["body"].Value);
    }

    private static string FindRepositoryRoot()
    {
        foreach (var startingPoint in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var current = new DirectoryInfo(startingPoint);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "publish.ps1")) &&
                    File.Exists(Path.Combine(current.FullName, ".github", "workflows", "ci.yml")))
                    return current.FullName;
                current = current.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the repository containing publish.ps1 and ci.yml.");
    }

    private sealed class FakeRepository : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "InventoryTracker-publish-test-" + Guid.NewGuid());
        private readonly string _fakeBin;
        private readonly string _publishScript;
        private readonly string _publishWrapper;

        public FakeRepository()
        {
            Directory.CreateDirectory(_root);
            Directory.CreateDirectory(Path.Combine(_root, "src"));
            File.WriteAllText(Path.Combine(_root, "src", "InventoryTracker.csproj"), "fake project");
            File.WriteAllText(Path.Combine(_root, "VERSION"), "1.2.3");
            Dist = Path.Combine(_root, "dist");
            DistOther = Path.Combine(_root, "dist-other");
            DistJunction = Path.Combine(_root, "dist-junction");
            IsolatedOutput = Path.Combine(_root, "isolated-publish-output");

            var sourceScript = Path.Combine(FindRepositoryRoot(), "publish.ps1");
            _publishScript = Path.Combine(_root, "publish.ps1");
            File.Copy(sourceScript, _publishScript);
            _publishWrapper = Path.Combine(_root, "run-publish-with-process-mocks.ps1");
            File.WriteAllText(_publishWrapper, ProcessMockWrapper);

            _fakeBin = Path.Combine(_root, "fake-bin");
            Directory.CreateDirectory(_fakeBin);
            File.WriteAllText(Path.Combine(_fakeBin, "dotnet.cmd"), FakeDotnetScript);
        }

        public string Dist { get; }
        public string DistOther { get; }
        public string DistJunction { get; }
        public string IsolatedOutput { get; }
        public string ProcessLog => Path.Combine(_root, "process-mock.log");
        public string DotnetLog => Path.Combine(_root, "dotnet-mock.log");

        public void CreateJunction(string junctionPath, string targetPath)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J {Quote(junctionPath)} {Quote(targetPath)}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var process = Process.Start(startInfo);
            Assert.NotNull(process);
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, $"Could not create test junction: {output}");
        }

        public void RemoveJunction(string junctionPath)
        {
            if (Directory.Exists(junctionPath))
                Directory.Delete(junctionPath);
        }

        public ProcessResult RunPublish(string? outputPath = null, string? mockProcessPath = null)
        {
            var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                @"WindowsPowerShell\v1.0\powershell.exe");
            Assert.True(File.Exists(powershell), $"Expected Windows PowerShell at {powershell}.");
            var arguments = new List<string>
            {
                "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Quote(_publishWrapper),
                "-PublishScript", Quote(_publishScript), "-Version", "1.2.3"
            };
            if (outputPath is not null)
            {
                arguments.Add("-OutputPath");
                arguments.Add(Quote(outputPath));
            }

            var existingPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            var startInfo = new ProcessStartInfo
            {
                FileName = powershell,
                Arguments = string.Join(" ", arguments),
                WorkingDirectory = _root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.Environment["PATH"] = _fakeBin + Path.PathSeparator + existingPath;
            startInfo.Environment["PUBLISH_PROCESS_LOG"] = ProcessLog;
            startInfo.Environment["PUBLISH_DOTNET_LOG"] = DotnetLog;
            startInfo.Environment["PUBLISH_MOCK_PROCESS_PATH"] = mockProcessPath ?? string.Empty;

            using var process = Process.Start(startInfo);
            Assert.NotNull(process);
            try
            {
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                Assert.True(process.WaitForExit(30000), "The copied publish script did not exit within 30 seconds.");
                return new ProcessResult(process.ExitCode,
                    stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
            }
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }

        private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

        private const string FakeDotnetScript = "@echo off\r\n" +
            "setlocal\r\n" +
            ">>%PUBLISH_DOTNET_LOG% echo invoked\r\n" +
            "set output=\r\n" +
            ":next\r\n" +
            "if \"%~1\"==\"\" goto done\r\n" +
            "if /I \"%~1\"==\"--output\" (set output=%~2& shift)\r\n" +
            "shift\r\n" +
            "goto next\r\n" +
            ":done\r\n" +
            "if not defined output exit /b 2\r\n" +
            "if not exist \"%output%\" mkdir \"%output%\"\r\n" +
            ">\"%output%\\InventoryTracker.exe\" echo fake executable\r\n" +
            "exit /b 0\r\n";

        private const string ProcessMockWrapper = "[CmdletBinding()]\r\n" +
            "param([Parameter(Mandatory = $true)][string]$PublishScript, [string]$Version, [string]$OutputPath)\r\n" +
            "function Get-Process {\r\n" +
            "    [CmdletBinding()] param()\r\n" +
            "    Add-Content -LiteralPath $env:PUBLISH_PROCESS_LOG -Value 'enumerated'\r\n" +
            "    if ($env:PUBLISH_MOCK_PROCESS_PATH) {\r\n" +
            "        $mock = [pscustomobject]@{ Path = $env:PUBLISH_MOCK_PROCESS_PATH; Id = 4242 }\r\n" +
            "        $mock | Add-Member -MemberType ScriptMethod -Name WaitForExit -Value { param($timeout) $true }\r\n" +
            "        return $mock\r\n" +
            "    }\r\n" +
            "}\r\n" +
            "function Stop-Process {\r\n" +
            "    [CmdletBinding()] param([int]$Id, [switch]$Force)\r\n" +
            "    Add-Content -LiteralPath $env:PUBLISH_PROCESS_LOG -Value ('stopped:' + $Id)\r\n" +
            "}\r\n" +
            "if ($OutputPath) { . $PublishScript -Version $Version -OutputPath $OutputPath }\r\n" +
            "else { . $PublishScript -Version $Version }\r\n";
    }

    private sealed record ProcessResult(int ExitCode, string Output);
}
