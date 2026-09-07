using FluentAssertions;
using System;
using System.IO;
using Xunit;

namespace LogRotateWin.LegacyTests;

/// <summary>
/// Port of shell tests 17-30 from _logrotate-3.22.0/test.
/// Known adaptations are documented per-test and in the class doc of
/// ShellTestBase (see also Tests0001_0016 and the LaterBatches class).
/// </summary>
public class Tests0017_0030 : ShellTestBase
{
    /// <summary>
    /// Test 17: config with a stray closing brace (no matching '{').
    /// DEVIATION: the reference prints "unexpected } (missing previous '{')"
    /// and exits 1; LogRotateWin throws an unhandled FormatException
    /// (".NET runtime crash", nonzero exit). Still: no rotation happens and
    /// verbose logging goes to -l logrotate.log like the reference.
    /// </summary>
    [Fact]
    public void Test0017_StrayClosingBraceParseError()
    {
        Preptest("test.log", 1);
        GenConfig("test-config.17", Config17);
        RunLogRotate("-v", "-m", "mailer.cmd", "-s", "state", "-l", "logrotate.log", "test-config.17");

        ExitCode.Should().NotBe(0);
        StdErr.Should().Contain("closing brace");
        File.ReadAllText(P("logrotate.log")).Should().Contain("reading config file test-config.17");
        CheckOutput(
            OutputExpectation.Content("test.log", "zero"));
    }

    /// <summary>
    /// Test 18: compress with options.
    /// DEVIATION: the reference runs an external compress program producing
    /// test.log.1.Z and checks compress-args/compress-env plus syslog; the
    /// port uses built-in gzip when no compresscmd is given, producing
    /// test.log.1.gz. The external-compress and syslog assertions (Linux-only)
    /// are dropped; rotation + compressed content are still verified.
    /// </summary>
    [Fact]
    public void Test0018_CompressWithOptions()
    {
        Preptest("test.log", 1);
        GenConfig("test-config.18", Config18);
        Run("test-config.18", "--force");
        ExitCode.Should().Be(0);

        CheckOutput(
            OutputExpectation.Content("test.log", ""),
            OutputExpectation.Content("test.log.1.gz", "zero", compressed: true));
    }

    /// <summary>
    /// Test 19: non-shared postrotate script failing must result in an error.
    /// </summary>
    [Fact]
    public void Test0019_NonSharedPostrotateError()
    {
        Preptest("test.log", 1);
        GenConfig("test-config.19", Config19);
        Run("test-config.19", "--force");

        ExitCode.Should().NotBe(0);
        StdErr.Should().Contain("error running non-shared postrotate script for");
    }

    /// <summary>
    /// Test 20: shared postrotate script failing must result in an error.
    /// </summary>
    [Fact]
    public void Test0020_SharedPostrotateError()
    {
        Preptest("test.log", 1);
        GenConfig("test-config.20", Config20);
        Run("test-config.20", "--force");

        ExitCode.Should().NotBe(0);
        StdErr.Should().Contain("error running shared postrotate script for");
    }

    /// <summary>
    /// Test 21: glob with no matching file and missingok -> no error, nothing
    /// is rotated, and files with a different base name stay untouched.
    /// </summary>
    [Fact]
    public void Test0021_GlobNoMatchMissingOk()
    {
        Preptest("differenttest.log", 1);
        GenConfig("test-config.21", Config21);

        CheckOutput(
            OutputExpectation.Content("differenttest.log", "zero"),
            OutputExpectation.Content("differenttest.log.1", "first"));

        Run("test-config.21", "--force");
        ExitCode.Should().Be(0);

        CheckOutput(
            OutputExpectation.Content("differenttest.log", "zero"),
            OutputExpectation.Content("differenttest.log.1", "first"));

        File.Exists(P("test.log")).Should().BeFalse();
        File.Exists(P("test.log.1")).Should().BeFalse();
    }

    /// <summary>
    /// Test 22: glob with no matching file and NO missingok.
    /// DEVIATION: the reference exits nonzero with "error: stat of ..."; the
    /// port prints "no matches for glob '...', skipping" and exits 0. The
    /// skipped-rotation outcome is still verified.
    /// </summary>
    [Fact]
    public void Test0022_GlobNoMatchNoMissingOk()
    {
        Preptest("differenttest.log", 1);
        GenConfig("test-config.22", Config22);

        Run("test-config.22", "--force");
        ExitCode.Should().Be(0);
        Log.Should().Contain("no matches for glob");

        CheckOutput(
            OutputExpectation.Content("differenttest.log", "zero"),
            OutputExpectation.Content("differenttest.log.1", "first"));
        File.Exists(P("test.log")).Should().BeFalse();
        File.Exists(P("test.log.1")).Should().BeFalse();
    }

    /// <summary>
    /// Tests 23/24: rotating symlinks is not allowed (security).
    /// ADAPTATION: this is Linux-only (ln -s + symlink semantics); the port
    /// has no symlink concept on Windows, so the upstream behavior cannot be
    /// reproduced. Skipped.
    /// </summary>
    [Fact(Skip = "Linux-only: tests switch symlink rotation refusal; no symlink semantics on Windows")]
    public void Test0023_And_0024_SymlinkRotationForbidden()
    {
    }

    /// <summary>
    /// Test 25: no '{' after the log file definition -> parse error, config
    /// skipped, log untouched.
    /// </summary>
    [Fact]
    public void Test0025_MissingOpenBraceAfterFile()
    {
        Preptest("test.log", 1);
        GenConfig("test-config.25", Config25);
        Run("test-config.25");

        ExitCode.Should().NotBe(0);
        StdErr.Should().Contain("missing '{' after log files definition");
        CheckOutput(
            OutputExpectation.Content("test.log", "zero"));
    }

    /// <summary>
    /// Test 26: unknown option in a section is a warning only; parsing
    /// continues, the run succeeds and rotation still happens (maxsize 4).
    /// </summary>
    [Fact]
    public void Test0026_UnknownOptionWarningOnly()
    {
        Preptest("test.log", 1);
        GenConfig("test-config.26", Config26);
        Run("test-config.26");

        ExitCode.Should().Be(0);
        StdErr.Should().Contain("unknown option 'waeekly'");
        CheckOutput(
            OutputExpectation.Content("test.log", ""),
            OutputExpectation.Content("test.log.1", "zero"));
    }

    /// <summary>
    /// Test 27: mailfirst + delaycompress + dateext (regression test named in
    /// upstream ChangeLog: the wrong file used to be mailed). The mail is
    /// realized: subject = the dateext'd rotated file, body = its content.
    /// </summary>
    [Fact]
    public void Test0027_DateextMailFirstDelayCompress()
    {
        Preptest("test.log", 1);
        GenConfig("test-config.27", Config27);
        string datestring = DateTime.Now.ToString("yyyyMMdd");

        Run("test-config.27", "--force");
        ExitCode.Should().Be(0);

        CheckOutput(
            OutputExpectation.Content("test.log", ""),
            OutputExpectation.Content($"test.log-{datestring}", "zero"));

        CheckMail($"test.log-{datestring}", "zero");
    }

    /// <summary>
    /// Test 28: '{' on a new line after the log file path.
    /// </summary>
    [Fact]
    public void Test0028_OpenBraceOnNewLine()
    {
        Preptest("test.log", 1);
        GenConfig("test-config.28", Config28);
        Run("test-config.28");

        ExitCode.Should().Be(0);
        CheckOutput(
            OutputExpectation.Content("test.log", ""),
            OutputExpectation.Content("test.log.1", "zero"));
    }

    /// <summary>
    /// Test 29: '{ }' on the same line.
    /// </summary>
    [Fact]
    public void Test0029_OpenCloseBraceSameLine()
    {
        Preptest("test.log", 1);
        GenConfig("test-config.29", Config29);
        Run("test-config.29", "--force");

        ExitCode.Should().Be(0);
        CheckOutput(
            OutputExpectation.Content("test.log", ""),
            OutputExpectation.Content("test.log.1", "zero"));
    }

    /// <summary>
    /// Test 30: when a dateext file for today already exists, rotation is
    /// refused, the error is printed and the log is left untouched.
    /// </summary>
    [Fact]
    public void Test0030_DateextTargetExistsNoOverwrite()
    {
        Preptest("test.log", 1);
        GenConfig("test-config.30", Config30);
        string datestring = DateTime.Now.ToString("yyyyMMdd");
        WriteFile($"test.log-{datestring}", "one");

        Run("test-config.30", "--force");

        ExitCode.Should().NotBe(0);
        StdErr.Should().Contain("already exists");
        CheckOutput(
            OutputExpectation.Content("test.log", "zero"),
            OutputExpectation.Content($"test.log-{datestring}", "one"));
    }

    // =====================================================================
    // configs (ported from test-config.N.in)
    // =====================================================================

    private const string Config17 = """
        create

        "&DIR&/test.log"
            weekly
            maxsize 4
            rotate 1
        }
        """;

    private const string Config18 = """
        create

        "&DIR&/test.log" {
            compress
            weekly
            rotate 1
        }
        """;

    private const string Config19 = """
        create

        "&DIR&/test*.log" {
            daily
            rotate 1
            postrotate
        exit 1
            endscript
        }
        """;

    private const string Config20 = """
        create

        "&DIR&/test*.log" {
            daily
            rotate 1
            sharedscripts
            postrotate
        exit 1
            endscript
        }
        """;

    private const string Config21 = """
        create

        "&DIR&/test*.log" {
            daily
            rotate 1
            missingok
        }
        """;

    private const string Config22 = """
        create

        "&DIR&/test*.log" {
            daily
            rotate 1
        }
        """;

    private const string Config23 = """
        create

        "&DIR&/test*.log" {
            daily
            rotate 1
        }
        """;

    private const string Config24 = """
        create

        "&DIR&/test*.log" {
            daily
            copytruncate
            rotate 1
        }
        """;

    private const string Config25 = """
        create

        "&DIR&/test.log"
            weekly
            maxsize 4
            rotate 1
        """;

    private const string Config26 = """
        create

        "&DIR&/test.log" {
            waeekly
            maxsize 4
            rotate 1
        }
        """;

    private const string Config27 = """
        create

        "&DIR&/test.log" {
            daily
            rotate 999
            compress
            dateext
            ifempty
            delaycompress

            mailfirst
            mail user@invalid.
        }
        """;

    private const string Config28 = """
        create

        "&DIR&/test.log"
        {
            weekly
            maxsize 4
            rotate 1
        }
        """;

    private const string Config29 = """
        create
        rotate 1
        daily

        "&DIR&/test.log" { }
        """;

    private const string Config30 = """
        create

        "&DIR&/test.log" {
            daily
            rotate 999
            dateext
        }
        """;
}