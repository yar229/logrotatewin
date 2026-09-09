using FluentAssertions;
using PostCsConvertation.Tests.Integration.Base;
using PostCsConvertation.Tests.Integration.Wrappers;
using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Op = LogRotate.Consts.ConfigSectionDirectives;

namespace PostCsConvertation.Tests.Integration;

[Trait("Category", "Integration")]
public class CompressTests : NewWaveIntegrationTestBase
{
    public CompressTests(ITestOutputHelper output)
        : base(output)
    {
    }

    [Fact]
    public void RotateAndCompress_DecompressedArchiveContent_ShouldEqualOriginalLogContent()
    {
        string content = "line 1\r\n"
            + "log entry 2026-09-09 id=42 value=значение\r\n"
            + "another entry with tabs\tand symbols #%&^$\r\n"
            + "final\r\n";
        byte[] original = Encoding.UTF8.GetBytes(content);

        string logName = "app.log";
        var log = Runner.NewLog(logName);
        File.WriteAllBytes(log.Filepath, original);

        var gz = Runner.NewFile($"{logName}.1.gz");

        Runner
            .WithLog(log, l => l
                .ShouldNotBe())
            .WithFile(gz, f => f
                .ShouldBe())
            .WithConfig(c => c
                .WithSection(XPattern.AllLogs, s => s
                    .With(Op.Rotate, 5)
                    .With(Op.Compress)
                    .With(Op.Monthly))
                .Create())
            .RunAndCheck();

        Runner.ExitCode.Should().Be(0);

        using var inFile = File.OpenRead(gz.Filepath);
        using var gzStream = new GZipStream(inFile, CompressionMode.Decompress);
        using var extracted = new MemoryStream();
        gzStream.CopyTo(extracted);

        extracted.ToArray().Should().Equal(original, because:"log content and rotated to gzip content must be equal");
    }


    [Fact]
    public void CompressExternalCommand_LargeLog_ShouldNotDeadlock()
    {
        byte[] big = new byte[8 * 1024 * 1024];
        for (int i = 0; i < big.Length; i++)
            big[i] = (byte)(i % 251);

        string logName = "app.log";
        var log = Runner.NewLog(logName);
        File.WriteAllBytes(log.Filepath, big);

        string ps1 = Path.Combine(TestDir, "pass-stream.ps1");
        File.WriteAllText(ps1,
            "$in = [Console]::OpenStandardInput()\r\n"
            + "$out = [Console]::OpenStandardOutput()\r\n"
            + "$buf = New-Object byte[] 8192\r\n"
            + "while (($n = $in.Read($buf, 0, $buf.Length)) -gt 0) {\r\n"
            + "    $out.Write($buf, 0, $n)\r\n"
            + "    $out.Flush()\r\n"
            + "}\r\n"
            + "$out.Flush()\r\n");

        var gz = Runner.NewFile($"{logName}.1.gz");

        Runner
            .WithLog(log, l => l
                .ShouldNotBe())
            .WithFile(gz, f => f
                .ShouldBe())
            .WithConfig(c => c
                .WithSection(XPattern.AllLogs, s => s
                    .With(Op.Rotate, 5)
                    .With(Op.Compress)
                    .With(Op.CompressCmd, "powershell.exe")
                    .With(Op.CompressOptions,
                        $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File {ps1}"))
                .Create());

        bool completed = Task.Run(() => Runner.RunAndCheck())
            .Wait(TimeSpan.FromSeconds(90));

        completed.Should().BeTrue(
            "the compressor writes to stdout while still reading stdin; " +
            "waiting for the stdin copy before draining stdout deadlocks on inputs larger than the pipe buffer");

        Runner.ExitCode.Should().Be(0);
        File.ReadAllBytes(gz.Filepath).Should().Equal(big);
    }

}