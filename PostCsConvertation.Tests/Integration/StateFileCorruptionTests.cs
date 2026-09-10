using FluentAssertions;
using PostCsConvertation.Tests.Integration.Base;
using PostCsConvertation.Tests.Integration.Wrappers;
using System;
using System.IO;
using System.Linq;
using Xunit;
using Op = LogRotate.Consts.ConfigSectionDirectives;

namespace PostCsConvertation.Tests.Integration;

[Trait("Category", "Integration")]
public class StateFileCorruptionTests : NewWaveIntegrationTestBase
{
    public StateFileCorruptionTests(ITestOutputHelper output)
        : base(output)
    {
    }

    [Fact]
    public void BadLineInStateFile_IsSkipped_AndRotationHistoryIsKept()
    {
        var log = Runner.NewLog("app.log").Create();

        // First run: rotate once and persist the state entry.
        Runner
            .WithLog(log, l => l
                .ShouldNotBe()
                .ShouldBe(Ext(".1")))
            .WithConfig(c => c
                .WithSection(XPattern.AllLogs, s => s
                    .With(Op.Rotate, 2)
                    .With(Op.Daily))
                .Create())
            .RunAndCheck()
                .Should(r => r.ExitCode.Should().Be(0, "first run must rotate and succeed"));

        string statePath = Runner.State.Filepath;
        var lines = File.ReadAllLines(statePath).ToList();
        lines.Insert(1, "GARBAGE-LINE-WITHOUT-A-DATE");
        File.WriteAllText(statePath, string.Join("\n", lines) + "\n");

        // Re-create the live log so the rotation run really has something to
        // consider (run1 renamed it to app.log.1).
        File.WriteAllText(log.Filepath, "new content");

        // Second run: the garbage line must be reported but skipped; the valid
        // state entry for app.log must survive, so the log must NOT be
        // re-rotated (no app.log.2).
        int rc2 = RunLogRotate("--verbose", "-s", statePath, Runner.Config);
        rc2.Should().Be(1, "a corrupt state line still counts as an error");
        Log.Should().Contain("bad line 2 in state file",
            "the bad line must be diagnosed");
        log.Exists().Should().BeTrue("the re-created log must still be in place");
        log.Exists(".1").Should().BeTrue("the first rotation must still be in place");
        log.Exists(".2").Should().BeFalse(
            "the surviving state entry must prevent a second rotation");

        // Third run: the bad line must have been dropped from the state file
        // by the second run's WriteState, so the state file is clean again.
        File.WriteAllText(log.Filepath, "new content again");
        int rc3 = RunLogRotate("--verbose", "-s", statePath, Runner.Config);
        rc3.Should().Be(0, "the state file must have been healed on the previous run");
        log.Exists(".2").Should().BeFalse("the log still must not be re-rotated");
    }
}