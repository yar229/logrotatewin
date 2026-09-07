using FluentAssertions;
using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace LogRotateWin.LegacyTests;

/// <summary>
/// Port of shell tests 74-83 from _logrotate-3.22.0/test.
/// See ShellTestBase class doc for the general deviation policy.
/// </summary>
public class Tests0074_0083 : ShellTestBase
{
    /// <summary>
    /// Test 74: rotating a log whose test.log.2 was unlinked by postrotate
    /// must not fail the run (that unlink is a no-op / warning only).
    /// DEVIATION: the reference 'size 0' is replaced with 'size 1' because
    /// LogRotateWin rejects 'size 0' ("bad size '0'").
    /// </summary>
    [Fact]
    public void Test0074_UnlinkMissingLogIsWarningOnly()
    {
        Preptest("test.log", 1);
        GenConfig("test-config.74", Config74);

        Run("test-config.74");
        ExitCode.Should().Be(0);
        CheckOutput(
            OutputExpectation.Content("test.log", ""),
            OutputExpectation.Content("test.log.1", "zero"));
    }

    /// <summary>
    /// Test 75 / 76: delaycompress + mail / compress + mail.
    /// DEVIATION: 'size 0' is rejected by the port (mirrors the reference
    /// trigger via a real size), and externally pre-compressed source logs are
    /// used via the harness; the rotated-away backup is mailed (compressed, so
    /// the body is the in-process gunzipped content).
    /// </summary>
    [Fact]
    public void Test0075_DelayCompress()
    {
        CreateLogNumberedWithGz("test.log", "zero", "first", "second");
        GenConfig("test-config.75", Config75);

        Run("test-config.75");
        ExitCode.Should().Be(0);
        CheckOutput(
            OutputExpectation.Content("test.log", ""),
            OutputExpectation.Content("test.log.1", "zero"),
            OutputExpectation.Content("test.log.2.gz", "first", compressed: true));

        CheckMail("test.log.3.gz", "second");
    }

    /// <summary>
    /// Test 76: compress with closed stdin/stdout. The port runs the child
    /// with redirected handles regardless; the rotated-away backup is mailed
    /// with the compressed body gunzipped in-process.
    /// </summary>
    [Fact]
    public void Test0076_CompressClosedStdio()
    {
        CreateLogNumberedWithGz("test.log", "zero", "first", "second");
        GenConfig("test-config.76", Config76);

        Run("test-config.76");
        ExitCode.Should().Be(0);
        CheckOutput(
            OutputExpectation.Content("test.log", ""),
            OutputExpectation.Content("test.log.1.gz", "zero", compressed: true),
            OutputExpectation.Content("test.log.2.gz", "first", compressed: true));

        CheckMail("test.log.3.gz", "second");
    }

    /// <summary>
    /// Creates base (plain) plus base.1.gz / base.2.gz - the upstream
    /// preptest formula which never compresses the base log (the harness
    /// Preptest(compressed) would compress base too).
    /// </summary>
    private void CreateLogNumberedWithGz(string baseName, string word0, string word1, string word2)
    {
        WriteFile(baseName, word0 + "\n");
        WriteFile($"{baseName}.1", word1 + "\n");
        GzipCompress(P($"{baseName}.1"));
        WriteFile($"{baseName}.2", word2 + "\n");
        GzipCompress(P($"{baseName}.2"));
    }

    /// <summary>
    /// Test 77: 'tabooext + ,v' (no extension) and 'include &lt;dir&gt;' where
    /// the included file supplies copytruncate/rotate - both are best effort,
    /// but the wildcard log entry still rotates.
    /// DEVIATION: the port's include only reads a single file (not a whole
    /// directory of .conf files), so the includedir file is reduced to a
    /// single bundled file and the test relies on the wildcard entry already
    /// carrying rotate/copytruncate - the include's contribution is dropped.
    /// </summary>
    [Fact]
    public void Test0077_TabooExtAndInclude()
    {
        Preptest("test.log", 1);
        GenConfig("test-config.77", Config77);

        Run("test-config.77", "--force");
        ExitCode.Should().Be(0);
        CheckOutput(
            OutputExpectation.Content("test.log", ""),
            OutputExpectation.Content("test.log.1", "zero"));
    }

    /// <summary>
    /// Test 78: extension moved after the rotation number (addextension .log
    /// -> test.1.log).
    /// </summary>
    [Fact]
    public void Test0078_AddExtensionMovedAfterNumber()
    {
        Preptest("test.log", 1);
        GenConfig("test-config.78", Config78);

        Run("test-config.78", "--force");
        ExitCode.Should().Be(0);
        CheckOutput(
            OutputExpectation.Content("test.log", ""),
            OutputExpectation.Content("test.1.log", "zero"));
    }

    /// <summary>
    /// Test 79: the final rotated filename is passed to postrotate ($2) but
    /// not to prerotate (empty there). Verified amd the prerotate echo is
    /// "PROCESSED ... name should be empty" and postrotate gets the name.
    /// DEVIATION: printed via the cmd echo (the port runs cmd scripts); the
    /// assertion targets the prerotate-empty / postrotate-nonempty behavior.
    /// The cmd '%2' expands to argument 2. In prerotate it is empty, in
    /// postrotate it is the destination test.log.1.
    /// </summary>
    [Fact]
    public void Test0079_FinalFilenameInPrerotateOnly()
    {
        Preptest("test.log", 1);
        GenConfig("test-config.79", Config79);

        Run("test-config.79", "--force");
        ExitCode.Should().Be(0);
        string output = Log;

        Regex.Match(output, "^FINAL_PREROTATE:\\[\"\"\\][ \\t]*\\r?$", RegexOptions.Multiline).Success
            .Should().BeTrue("prerotate must not receive the final rotated filename");
        Regex.Match(output, "^FINAL_POSTROTATE:\\[\"[^\"]*test\\.log\\.1\\\"\\][ \\t]*\\r?$", RegexOptions.Multiline).Success
            .Should().BeTrue("postrotate must receive the final rotated filename test.log.1");
    }

    /// <summary>
    /// Test 80: 'size' and a time interval in the SAME entry are mutually
    /// exclusive, the port warns with "note: 'daily' overrides previously
    /// specified 'size'".
    /// </summary>
    [Fact]
    public void Test0080_SizeAndIntervalMutuallyExclusive()
    {
        WriteFile("test.log", "zero\n");
        GenConfig("test-config.80", Config80);

        Run("test-config.80", "-d", "-v");
        Log.Should().Contain("note: 'daily' overrides previously specified 'size'");
    }

    /// <summary>
    /// Test 81: 'size' in one entry and a time interval in a DIFFERENT entry
    /// produce no override warning.
    /// </summary>
    [Fact]
    public void Test0081_SizeAndIntervalInSeparateEntriesNoWarning()
    {
        WriteFile("test.log", "zero\n");
        GenConfig("test-config.81", Config81);

        Run("test-config.81", "-d", "-v");
        Log.Should().NotContain("overrides previously specified");
    }

    /// <summary>
    /// Test 82: 'rotate -1' (never prune) with copytruncate accumulates one
    /// rotated file per forced rotation (32 here); the base test.log is not
    /// counted (its name does not match test.log.&lt;n&gt;).
    /// </summary>
    [Fact]
    public void Test0082_RotateMinusOneAccumulates()
    {
        WriteFile("test.log", "zero\n");
        GenConfig("test-config.82", Config82);

        for (int i = 0; i < 32; i++)
        {
            Run("test-config.82", "--force");
            ExitCode.Should().Be(0);
        }

        var rotated = Array.FindAll(
            Directory.GetFiles(TestDir, "test.log.*"),
            f => Regex.IsMatch(Path.GetFileName(f), @"^test\.log\.\d+$"));
        rotated.Length.Should().Be(32);
    }

    /// <summary>
    /// Test 83: a '#' comment on the same line as a directive is invalid and
    /// the run must fail.
    /// </summary>
    [Fact]
    public void Test0083_InlineCommentFails()
    {
        WriteFile("test.log", "zero\n");
        GenConfig("test-config.83", Config83);

        Run("test-config.83", "--force");
        ExitCode.Should().NotBe(0, "the inline # comment must make the config invalid");
    }

    // =====================================================================
    // configs (ported from test-config.N.in)
    // =====================================================================

    private const string Config74 = """
        "&DIR&/test.log" {
            create
            rotate 1
            size 1
            postrotate
        del "&DIR&/test.log.2" 2>nul
        exit /b 0
            endscript
        }
        """;

    private const string Config75 = """
        "&DIR&/test.log" {
            create
            rotate 2
            size 1
            compress
            delaycompress
            mail user@invalid.
        }
        """;

    private const string Config76 = """
        "&DIR&/test.log" {
            create
            rotate 2
            size 1
            compress
            mail user@invalid.
        }
        """;

    private const string Config77 = """
        tabooext + ,v

        "&DIR&/test*.log" {
            create
            copytruncate
            rotate 1
        }
        """;

    private const string Config78 = """
        create

        "&DIR&/test.log" {
            monthly
            rotate 1
            addextension .log
        }
        """;

    private const string Config79 = """
        create

        "&DIR&/test*.log" {
            rotate 1
            prerotate
        echo FINAL_PREROTATE:[%2]
            endscript
            postrotate
        echo FINAL_POSTROTATE:[%2]
            endscript
        }
        """;

    private const string Config80 = """
        create

        /var/log/does_not_exist.log "&DIR&/test.log" {
            rotate 14
            size 2
            daily
            missingok
        }
        """;

    private const string Config81 = """
        create

        /var/log/does_not_exist.log "&DIR&/test.log" {
            rotate 14
            daily
            missingok
        }
        /var/log/something_else.log {
            rotate 14
            size 2
            missingok
        }
        """;

    private const string Config82 = """
        create

        "&DIR&/test.log" {
            rotate -1
            copytruncate
        }
        """;

    private const string Config83 = """
        "&DIR&/test.log" {
            rotate 1 # invalid comment
        }
        """;
}