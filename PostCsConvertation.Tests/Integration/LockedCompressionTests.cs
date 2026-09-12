using FluentAssertions;
using PostCsConvertation.Tests.Integration.Base;
using PostCsConvertation.Tests.Integration.Wrappers;
using System;
using System.IO;
using Xunit;
using Op = LogRotate.Consts.ConfigSectionDirectives;

namespace PostCsConvertation.Tests.Integration;

[Trait("Category", "Integration")]
public class LockedCompressionTests : NewWaveIntegrationTestBase
{
    public LockedCompressionTests(ITestOutputHelper output)
        : base(output)
    {
    }

    [Fact]
    public void RotatedFileHeldByWriter_ReportsLockOwner_ThenCompressesAfterRelease()
    {
        var log = Runner.NewLog("app.log")
            .WithContent("first generation" + Environment.NewLine)
            .Create();

        Runner
            .WithLog(log, l => l
                .ShouldNotBe()
                .ShouldBe(Ext(".1")))
            .WithConfig(c => c
                .WithSection(XPattern.AllLogs, s => s
                    .With(Op.Rotate, 2)
                    .With(Op.Compress)
                    .With(Op.Monthly))
                .Create())
            .WithForce();

        // Windows has no POSIX open-handle semantics: a writer that holds the
        // log open (rename allowed, read denied) keeps its handle attached to
        // the rotated file after the rotation rename, so the internal gzip can
        // only hit a sharing violation. This mirrors the real-world "log file
        // opened once at process start, never reopened" writer.
        var share = FileShare.Delete | FileShare.Write;
        using (var held = new FileStream(log.Filepath, FileMode.Open,
            FileAccess.Write, share))
        {
            Runner.RunAndCheck();

            Runner.ExitCode.Should().Be(1,
                "compressing a rotated file still held by its writer must fail");
            Runner.Log.Should().Contain(".1 (read-only) for compression",
                "the sharing violation must be reported after the retries");
            Runner.Log.Should().MatchRegex(
                @"file .* is locked by: .*|cannot identify who locks .*",
                "the process holding the rotated file must be reported");
            log.Exists().Should().BeFalse("the original log must have been renamed away");
            log.Exists(".1").Should().BeTrue("the held file must have been renamed to .1");
            log.Exists(".1.gz").Should().BeFalse("compression of a locked file must not produce an archive");
        }

        // Once the writer has released the handle, a forced run must rotate and
        // compress the .1 file normally.
        File.WriteAllText(log.Filepath, "second generation" + Environment.NewLine);
        Runner.Run();

        Runner.ExitCode.Should().Be(0,
            "once the lock is gone, compression must succeed");
        log.Exists(".1.gz").Should().BeTrue("the rotated log must now be compressed");
        log.Exists(".1").Should().BeFalse("the uncompressed copy must be shredded");
    }
}