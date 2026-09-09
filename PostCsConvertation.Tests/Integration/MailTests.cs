using FluentAssertions;
using PostCsConvertation.Tests.Integration.Base;
using PostCsConvertation.Tests.Integration.Wrappers;
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Xunit;
using Op = LogRotate.Consts.ConfigSectionDirectives;

namespace PostCsConvertation.Tests.Integration;

[Trait("Category", "Integration")]
public class MailTests : NewWaveIntegrationTestBase
{
    public MailTests(ITestOutputHelper output)
        : base(output)
    {
    }

    [Fact]
    public void MailCommandExitsEarly_ShouldLogErrorButStillWriteState()
    {
        // mail broken pipe test

        var log = Runner.NewLog("app.log").Create();

        string statePath = Path.Combine(TestDir, "state");

        // A mail command that exits immediately without reading its stdin.
        var mailer = Runner.NewFile("mail-exit.cmd")
            .WithContent("@echo off\r\nexit /b 3\r\n")
            .Create();

        // A pre-existing rotated + compressed log. With rotate 1 this oldest
        // file is disposed on the next run, which triggers the mail to the
        // (early-exiting) mail command. Its payload is big enough to overflow
        // the mail pipe: the in-process gunzip then hits the broken pipe.
        var rotated = Runner.NewFile($"{log.Filename}.1.gz");
        string bigPayload = string.Concat(Enumerable.Repeat("rotated line with substantial content 0123456789 abcdefghij\n", 300_000));
        using (var fs = File.Create(rotated.Filepath))
        using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(bigPayload);
            gz.Write(bytes, 0, bytes.Length);
        }

        var config = new XConfig(TestDir, "mail-broken.conf");
        config.WithSection(XPattern.AllLogs, s => s
                .With(Op.Rotate, 1)
                .With(Op.Compress)
                .With(Op.Mail, "root@localhost")
                .With(Op.CompressExt, "gz"))
            .Create();

        int exitCode = RunLogRotate("-v", "-m", mailer, "-s", statePath, "-f", config);

        Log.Should().Contain("executing mail command",
            "the disposed rotated log must be mailed");
        Log.Should().Contain("pipe to mail command broke",
            "the broken mail pipe must be reported as a handled error, not crash the run");

        exitCode.Should().Be(1, "the broken mail pipe must yield a controlled failure exit code");
        File.Exists(statePath).Should().BeTrue(
            "the state file must be written even if the mail pipe broke, so the rotation is not repeated");

        var state = File.ReadAllText(statePath);
        state.Should().Contain(log.Filename, "the rotated log should be recorded in the state file");
    }
}