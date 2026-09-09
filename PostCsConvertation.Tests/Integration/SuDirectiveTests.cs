using FluentAssertions;
using LogRotate;
using PostCsConvertation.Tests.Integration.Base;
using PostCsConvertation.Tests.Integration.Wrappers;
using System;
using System.IO;
using System.Security.Principal;
using Xunit;
using Op = LogRotate.Consts.ConfigSectionDirectives;

namespace PostCsConvertation.Tests.Integration;

/// <summary>
/// Integration tests for the su/supasswd config directives.
///
/// Su mirrors the switch_user() behavior of the reference: the account
/// given in 'su owner group' is used as the owner/group of re-created log
/// files (create) and of directories created by createolddir, unless
/// create/createolddir specifies an explicit owner/group.
/// </summary>
public class SuDirectiveTests : NewWaveIntegrationTestBase
{
    public SuDirectiveTests(ITestOutputHelper output)
        : base(output)
    {
    }

    private const string TestGroupName = "IIS_IUSRS";

    private static readonly SecurityIdentifier TestGroupSid =
        (SecurityIdentifier)new NTAccount(TestGroupName).Translate(typeof(SecurityIdentifier));

    [Fact]
    public void RotateLog_WithSuAndCreate_ShouldApplySuGroupToNewLogDacl()
    {
        var log = Runner.NewLog("test.log").Create();

        Runner
            .WithLog(log, l => l
                .ShouldBe("the log file should be re-created"))
            .WithConfig(c => c
                .WithSection(log, s => s
                    .With(Op.Rotate, 2)
                    .With(Op.Daily)
                    .With(Op.Create, "0644")
                    .With(Op.Su, $"{Environment.UserName} {TestGroupName}"))
                .Create())
            .WithForce()
            .RunAndCheck()
            .ExitCode.Should().Be(0, "rotation with su should succeed");

        AclApi.DefinesAccessAce(log.Filepath, TestGroupSid).Should().BeTrue(
            "the DACL of the re-created log must grant the su group (owner/group buckets)");
    }

    [Fact]
    public void CreateOldDir_WithSu_ShouldApplySuGroupToCreatedOldDir()
    {
        var log = Runner.NewLog("test.log").Create();
        string oldDir = Path.Combine(TestDir, "rotated");
        var logrotated = Runner.NewLog(Path.Combine(oldDir, "test.log.1"));


        Runner
            .WithLog(log, l => l
                .ShouldBe())
            .WithFile(logrotated, f => f
                .ShouldBe("rotated file should go into olddir"))
            .WithConfig(c => c
                .WithSection(log, s => s
                    .With(Op.Rotate, 2)
                    .With(Op.Daily)
                    .With(Op.Create)
                    .With(Op.OldDir, oldDir)
                    .With(Op.CreateOldDir, "0770")
                    .With(Op.Su, $"{Environment.UserName} {TestGroupName}"))
                .Create())
            .WithForce()
            .RunAndCheck()
                .ExitCode.Should().Be(0, "rotation with su and createolddir should succeed");

        Directory.Exists(oldDir).Should().BeTrue("createolddir should create the olddir");
        AclApi.DefinesAccessAce(oldDir, TestGroupSid).Should().BeTrue("the DACL of the created olddir must grant the su group");
    }

    [Fact]
    public void RotateLog_WithSuForAnotherUser_ShouldWarnScriptsRunUnderCurrentAccount()
    {
        var log = Runner.NewLog("test.log").Create();

        Runner
            .WithLog(log, l => l
                .ShouldBe())
            .WithConfig(c => c
                .WithSection(log, s => s
                    .With(Op.Rotate, 2)
                    .With(Op.Daily)
                    .With(Op.Create, "0644")
                    .With(Op.Su, $"SYSTEM {TestGroupName}"))
                .Create())
            .WithForce()
            .RunAndCheck()
            .ExitCode.Should().Be(0, "rotation should proceed despite the su mismatch");

        Runner.Log.Should().MatchRegex("cannot switch users on Windows",
            "a warning about scripts running under the current account must be logged "
            + "when 'su' names a different user");
        Runner.Log.Should().MatchRegex("will run under the current account",
            "the warning must name the scripts affected by the su limitation");
    }

    [Fact]
    public void RotateLog_WithSuForCurrentUser_ShouldNotWarn()
    {
        var log = Runner.NewLog("test.log").Create();

        Runner
            .WithLog(log, l => l
                .ShouldBe())
            .WithConfig(c => c
                .WithSection(log, s => s
                    .With(Op.Rotate, 2)
                    .With(Op.Daily)
                    .With(Op.Create, "0644")
                    .With(Op.Su, $"{Environment.UserName} {TestGroupName}"))
                .Create())
            .WithForce()
            .RunAndCheck()
            .ExitCode.Should().Be(0, "rotation with su for the current account should succeed");

        Runner.Log.Should().NotMatch("will run under the current account",
            "scripts are effectively running under the 'su' account already");
    }

    [Fact]
    public void RotateLog_WithSuPasswd_ShouldNotWarnAboutCurrentAccount()
    {
        // A different su account with supasswd provided: scripts may run
        // impersonated, so the parse-time warning must be absent.
        var log = Runner.NewLog("test.log").Create();

        Runner
            .WithLog(log, l => l
                .ShouldBe())
            .WithConfig(c => c
                .WithSection(log, s => s
                    .With(Op.Rotate, 2)
                    .With(Op.Daily)
                    .With(Op.Create, "0644")
                    .With(Op.Su, $"SYSTEM {TestGroupName}")
                    .With(Op.SuPasswd, "not-a-real-password"))
                .Create())
            .WithForce()
            .RunAndCheck()
            .ExitCode.Should().Be(0, "rotation should proceed even if the logon fails");

        Runner.Log.Should().NotMatch("will run under the current account",
            "with supasswd the parse-time 'cannot switch users' warning must be suppressed");
    }

    [Fact]
    public void RotateLog_WithSupasswdButNoSu_ShouldWarnThatItIsIgnored()
    {
        var log = Runner.NewLog("test.log").Create();

        Runner
            .WithLog(log, l => l
                .ShouldBe())
            .WithConfig(c => c
                .WithSection(log, s => s
                    .With(Op.Rotate, 2)
                    .With(Op.Daily)
                    .With(Op.Create, "0644")
                    .With(Op.SuPasswd, "not-a-real-password"))
                .Create())
            .WithForce()
            .RunAndCheck()
            .ExitCode.Should().Be(0, "rotation should proceed without su");

        Runner.Log.Should().MatchRegex("supasswd is ignored because 'su' is not set",
            "a supasswd without su is meaningless and must be reported");
    }

    [Fact]
    public void RotateLog_WithSupasswdContainingSpecialChars_ShouldParseAndNotFail()
    {
        // The password token must keep '$', '^', ')', ';' etc. intact
        // (the value spans the rest of the line, leading blanks skipped).
        var log = Runner.NewLog("test.log").Create();

        Runner
            .WithLog(log, l => l
                .ShouldBe())
            .WithConfig(c => c
                .WithSection(log, s => s
                    .With(Op.Rotate, 2)
                    .With(Op.Daily)
                    .With(Op.Create, "0644")
                    .With(Op.Su, $"{Environment.UserName} {TestGroupName}")
                    .With(Op.SuPasswd, "sdKB6^9Xne)291;mM1"))
                .Create())
            .WithForce()
            .RunAndCheck()
            .ExitCode.Should().Be(0, "a specially-encoded password must not break parsing");

        Runner.Log.Should().NotContain("# error", "a specially-encoded password must not break parsing");
    }
}