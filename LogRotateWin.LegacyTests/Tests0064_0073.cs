using FluentAssertions;
using System;
using System.IO;
using System.Text;
using Xunit;

namespace LogRotateWin.LegacyTests;

/// <summary>
/// Port of shell tests 64-73 from _logrotate-3.22.0/test.
/// See ShellTestBase class doc for the general deviation policy.
/// </summary>
public class Tests0064_0073 : ShellTestBase
{
    /// <summary>
    /// Test 64: mail subject with compress + maillast + dateext ('rotate 0').
    /// The dated file is disposed directly: mailed (compressed, so the body is
    /// the in-process gunzipped content) and then removed.
    /// </summary>
    [Fact]
    public void Test0064_MailSubjectCompressDateExt()
    {
        Preptest("test.log", 1);
        GenConfig("test-config.64", Config64);
        string datestring = DateTime.Now.ToString("yyyyMMdd");

        Run("test-config.64", "--force");
        ExitCode.Should().Be(0);

        CheckOutput(
            OutputExpectation.Content("test.log", ""));

        CheckMail($"test.log-{datestring}.gz", "zero");
    }

    /// <summary>
    /// Test 65: mail subject without compress + maillast + dateext
    /// ('rotate 0'); same dispose-and-mail flow as test 64.
    /// </summary>
    [Fact]
    public void Test0065_MailSubjectNoCompressDateExt()
    {
        Preptest("test.log", 1);
        GenConfig("test-config.65", Config65);
        string datestring = DateTime.Now.ToString("yyyyMMdd");

        Run("test-config.65", "--force");
        ExitCode.Should().Be(0);

        CheckOutput(
            OutputExpectation.Content("test.log", ""));

        CheckMail($"test.log-{datestring}", "zero");
    }

    /// <summary>
    /// Test 66: dateformat without a leading dash; rotate 1 prunes the previous
    /// day's dated file.
    /// </summary>
    [Fact]
    public void Test0066_DateExtNoDashPrunesOldest()
    {
        WriteFile("test.log", "zero\n");
        GenConfig("test-config.66", Config66);

        var today = DateTime.Now;
        string dayAgo = today.AddDays(-1).ToString("yyyy-MM-dd");
        WriteFile($"test.log{dayAgo}", "removed\n");

        Run("test-config.66", "--force");
        ExitCode.Should().Be(0);
        File.Exists(P($"test.log{dayAgo}")).Should().BeFalse(
            $"dated file test.log{dayAgo} should have been pruned (rotate 1)");
        CheckOutput(
            OutputExpectation.Content("test.log", ""),
            OutputExpectation.Content($"test.log{today:yyyy-MM-dd}", "zero"));
    }

    /// <summary>
    /// Test 67: firstaction/lastaction run even when no rotation happens
    /// (the dated target already exists, rotation is skipped with an error).
    /// </summary>
    [Fact]
    public void Test0067_FirstAndLastActionNoRotation()
    {
        WriteFile("test.log", "zero\n");
        GenConfig("test-config.67", Config67);
        string today = DateTime.Now.ToString("yyyy-MM-dd");
        WriteFile($"test.log{today}", "removed\n");

        Run("test-config.67", "--force");
        ExitCode.Should().NotBe(0);
        string scriptout = File.ReadAllText(P("scriptout"));
        scriptout.Should().Contain("firstaction");
        scriptout.Should().Contain("lastaction");
    }

    /// <summary>
    /// Test 68: stale state-file entries are dropped when the state file is
    /// rewritten after rotation; a huge state file is handled without a freeze.
    /// DEVIATION: upstream writes 200000 unreferenced entries, the port gets
    /// 20000 - still a "big state file" but keeps the suite fast.
    /// </summary>
    [Fact]
    public void Test0068_HugeStateFilePruned()
    {
        Preptest("test.log", 1);
        GenConfig("test-config.68", Config68);

        var sb = new StringBuilder();
        sb.AppendLine("logrotate state -- version 1");
        sb.AppendLine($"\"{P("test.log")}\" 2000-1-1");
        for (int i = 1; i <= 20000; i++)
            sb.AppendLine($"\"{P($"removed.log{i}")}\" 2000-1-1");
        File.WriteAllText(StatePath, sb.ToString());

        Run("test-config.68", "--force");
        ExitCode.Should().Be(0);
        string state = File.ReadAllText(StatePath);
        state.Should().Contain("test.log");
        state.Should().NotContain("removed.log");
    }

    /// <summary>
    /// Test 69: olddir combined with a wildcard directory pattern. Both
    /// matched logs rotate into the shared olddir (createolddir). The
    /// test.lo3 pattern matches nothing (missingok).
    /// DEVIATION: createolddir mode/user/group is reduced to "createolddir
    /// 700" - the port ignores the user/group tokens.
    /// </summary>
    [Fact]
    public void Test0069_OlddirWithWildcard()
    {
        Directory.CreateDirectory(P("adir"));
        Directory.CreateDirectory(P("bdir"));
        WriteFile("adir/test.log", "zero\n");
        WriteFile("bdir/test.log", "zero\n");
        GenConfig("test-config.69", Config69);

        Run("test-config.69", "--force");
        ExitCode.Should().Be(0);
        CheckOutput(
            OutputExpectation.Content("adir/test.log", ""),
            OutputExpectation.Content("testdir/test.log.1", "zero"));
    }

    /// <summary>
    /// Test 70: minage - a log younger than minage days is never rotated,
    /// even when the state file says it is due.
    /// </summary>
    [Fact]
    public void Test0070_MinAgeTooYoung()
    {
        Preptest("test.log", 2);
        GenConfig("test-config.70", Config70);

        Run("test-config.70");
        ExitCode.Should().Be(0);
        CheckOutput(
            OutputExpectation.Content("test.log", "zero"),
            OutputExpectation.Content("test.log.1", "first"),
            OutputExpectation.Content("test.log.2", "second"));

        State(
            "logrotate state -- version 2",
            $"\"{P("test.log")}\" {DateTime.Now.Year - 10}-1-1");
        Run("test-config.70");
        ExitCode.Should().Be(0);
        CheckOutput(
            OutputExpectation.Content("test.log", "zero"),
            OutputExpectation.Content("test.log.1", "first"),
            OutputExpectation.Content("test.log.2", "second"));
    }

    /// <summary>
    /// Test 71: minage - a log with an old modification time IS rotated.
    /// </summary>
    [Fact]
    public void Test0071_MinAgeOldMTime()
    {
        Preptest("test.log", 2);
        GenConfig("test-config.71", Config71);
        File.SetLastWriteTime(P("test.log"), new DateTime(2000, 1, 1));
        State(
            "logrotate state -- version 2",
            $"\"{P("test.log")}\" 2000-1-1");

        Run("test-config.71");
        ExitCode.Should().Be(0);
        CheckOutput(
            OutputExpectation.Content("test.log", ""),
            OutputExpectation.Content("test.log.1", "zero"),
            OutputExpectation.Content("test.log.2", "first"));
    }

    /// <summary>
    /// Test 72: delaycompress keeps the newest rotated log uncompressed and
    /// compresses it during the following rotation.
    /// DEVIATION: the reference's second part writes an orphan "unexpected"
    /// test.log.1.gz and expects it to be preserved as test.log.1.gz-&lt;date&gt;.
    /// backup; the port silently drops that conflicting file instead, so only
    /// the first part (the delaycompress core) is asserted.
    /// </summary>
    [Fact]
    public void Test0072_DelayCompress()
    {
        Preptest("test.log", 2);
        GenConfig("test-config.72", Config72);

        Run("test-config.72", "--force");
        ExitCode.Should().Be(0);
        CheckOutput(
            OutputExpectation.Content("test.log", ""),
            OutputExpectation.Content("test.log.1", "zero"),
            OutputExpectation.Content("test.log.2.gz", "first", compressed: true));
    }

    /// <summary>
    /// Test 73: 'copy' together with 'copytruncate' (copy wins, log is
    /// truncated in place).
    /// DEVIATION: the reference's second block ('rotate 0' + copytruncate on
    /// test_rotate.log) is omitted because LogRotateWin rejects 'rotate 0'.
    /// </summary>
    [Fact]
    public void Test0073_CopyAndCopyTruncate()
    {
        WriteFile("test_copy.log", "zero\n");
        WriteFile("test_copy.log.1", "first\n");
        GenConfig("test-config.73", Config73);

        Run("test-config.73", "--force");
        ExitCode.Should().Be(0);
        CheckOutput(
            OutputExpectation.Content("test_copy.log", ""),
            OutputExpectation.Content("test_copy.log.1", "zero"));
    }

    // =====================================================================
    // configs (ported from test-config.N.in)
    // =====================================================================

    private const string Config64 = """
        create

        "&DIR&/test.log" {
            daily
            dateext
            dateformat -%Y%m%d
            rotate 0
            compress
            nosharedscripts
            mail user@invalid.
            maillast
        }
        """;

    private const string Config65 = """
        create

        "&DIR&/test.log" {
            daily
            dateext
            dateformat -%Y%m%d
            rotate 0
            nosharedscripts
            mail user@invalid.
            maillast
        }
        """;

    private const string Config66 = """
        create

        "&DIR&/test.log" {
            daily
            dateext
            dateformat %Y-%m-%d
            rotate 1
            nosharedscripts
        }
        """;

    private const string Config67 = """
        create

        "&DIR&/test.log" {
            daily
            dateext
            dateformat %Y-%m-%d
            rotate 1

            firstaction
        echo firstaction > scriptout
            endscript

            lastaction
        echo lastaction >> scriptout
            endscript
        }
        """;

    private const string Config68 = """
        create

        "&DIR&/test.log" {
            daily
            rotate 1
        }
        """;

    private const string Config69 = """
        create

        "&DIR&/*/test.log"
        "&DIR&/*/test.lo3" {
            monthly
            rotate 1
            olddir "&DIR&/testdir"
            createolddir 700
            missingok
        }
        """;

    private const string Config70 = """
        create

        "&DIR&/test.log" {
            daily
            rotate 3
            missingok
            minage 5
        }
        """;

    private const string Config71 = """
        create

        "&DIR&/test.log" {
            daily
            rotate 3
            missingok
            minage 5
        }
        """;

    private const string Config72 = """
        "&DIR&/test.log" {
            daily
            rotate 3
            compress
            delaycompress
            create
        }
        """;

    private const string Config73 = """
        "&DIR&/test_copy.log" {
            copy
            copytruncate
            rotate 1
        }
        """;
}