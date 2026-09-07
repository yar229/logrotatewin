using FluentAssertions;
using System;
using System.IO;
using Xunit;

namespace LogRotateWin.LegacyTests;

/// <summary>
/// Port of shell tests 1-16 from _logrotate-3.22.0/test.
/// </summary>
public class Tests0001_0016 : ShellTestBase
{
    /// <summary>
    /// Windows counterpart of the "append foo to scriptout" shell snippet used
    /// by configs 3-9 and 11:
    ///   touch scriptout; echo $(cat scriptout) foo > foo; mv foo scriptout
    /// Appends "foo" (space-separated) to scriptout.
    /// </summary>
    private const string AppendFooScript = "\n" + """
        setlocal EnableDelayedExpansion
        if exist scriptout (set /p CONTENT=<scriptout) else (set CONTENT=)
        if "!CONTENT!"=="" (echo foo>tmp.out) else (echo !CONTENT! foo>tmp.out)
        move /y tmp.out scriptout >nul
        endlocal
        """ + "\n";

    [Fact]
    public void Test0001_NoRotationWithoutState()
    {
        Preptest("test.log", 2);
        GenConfig("test-config.1", Config1);
        SeedStateNow("test.log");
        Run("test-config.1");
        ExitCode.Should().Be(0);
        CheckOutput(
            OutputExpectation.Content("test.log", "zero"),
            OutputExpectation.Content("test.log.1", "first"));

        State("logrotate state -- version 1", $"\"{P("test.log")}\" 2000-1-1");
        Run("test-config.1");
        ExitCode.Should().Be(0);
        CheckOutput(
            OutputExpectation.Content("test.log", ""),
            OutputExpectation.Content("test.log.1", "zero"),
            OutputExpectation.Content("test.log.2", "first"));

        Run("test-config.1");
        ExitCode.Should().Be(0);
        CheckOutput(
            OutputExpectation.Exist("test.log"),
            OutputExpectation.Content("test.log.1", "zero"),
            OutputExpectation.Content("test.log.2", "first"));
    }

    [Fact]
    public void Test0002_RotateToOldAndMailLast()
    {
        Preptest("test.log", 3);
        GenConfig("test-config.2", Config2);
        Run("test-config.2", "--force");
        ExitCode.Should().Be(0);

        CheckOutput(
            OutputExpectation.Content("test.log.1", "zero"),
            OutputExpectation.Content("test.log.2", "first"),
            OutputExpectation.NotExist("test.log"));

        CheckMail("test.log.3", "second");
    }

    [Fact]
    public void Test0003_PostrotateNonSharedScripts()
    {
        Preptest("test.log", 1);
        GenConfig("test-config.3", Config3);
        Run("test-config.3", "--force");
        ExitCode.Should().Be(0);
        CheckOutput(
            OutputExpectation.Content("test.log", ""),
            OutputExpectation.Content("test.log.1", "zero"),
            OutputExpectation.Content("scriptout", "foo"));

        Cleanup();
        Preptest("test.log", 1);
        Preptest("test2.log", 1);
        Run("test-config.3", "--force");
        ExitCode.Should().Be(0);
        CheckOutput(
            OutputExpectation.Content("test.log", ""),
            OutputExpectation.Content("test.log.1", "zero"),
            OutputExpectation.Content("test2.log", ""),
            OutputExpectation.Content("test2.log.1", "zero"),
            OutputExpectation.Content("scriptout", "foo foo"));
    }

    [Fact]
    public void Test0004_PostrotateSharedScripts()
    {
        Preptest("test.log", 1);
        Preptest("test2.log", 1);
        GenConfig("test-config.4", Config4);
        Run("test-config.4", "--force");
        ExitCode.Should().Be(0);
        CheckOutput(
            OutputExpectation.Content("test.log", ""),
            OutputExpectation.Content("test.log.1", "zero"),
            OutputExpectation.Content("test2.log", ""),
            OutputExpectation.Content("test2.log.1", "zero"),
            OutputExpectation.Content("scriptout", "foo"));
    }

    [Fact]
    public void Test0005_SharedScriptsMultipleFilesExplicit()
    {
        Preptest("test.log", 1);
        Preptest("anothertest.log", 1);
        GenConfig("test-config.5", Config5);
        Run("test-config.5", "--force");
        ExitCode.Should().Be(0);
        CheckOutput(
            OutputExpectation.Content("test.log", ""),
            OutputExpectation.Content("test.log.1", "zero"),
            OutputExpectation.Content("anothertest.log", ""),
            OutputExpectation.Content("anothertest.log.1", "zero"),
            OutputExpectation.Content("scriptout", "foo"));
    }

    [Fact]
    public void Test0006_StartZeroSingleFile()
    {
        Preptest("test.log", 1);
        Preptest("anothertest.log", 1);
        GenConfig("test-config.6", Config6);
        Run("test-config.6", "--force");
        ExitCode.Should().Be(0);
        CheckOutput(
            OutputExpectation.Content("test.log", ""),
            OutputExpectation.Content("test.log.0", "zero"),
            OutputExpectation.Content("anothertest.log", ""),
            OutputExpectation.Content("anothertest.log.0", "zero"),
            OutputExpectation.Content("scriptout", "foo"));
    }

    [Fact]
    public void Test0007_StartSixRotateThree()
    {
        Preptest("test.log", 1);
        Preptest("anothertest.log", 1);
        GenConfig("test-config.7", Config7);
        Run("test-config.7", "--force");
        ExitCode.Should().Be(0);
        CheckOutput(
            OutputExpectation.Content("test.log", ""),
            OutputExpectation.Content("test.log.6", "zero"),
            OutputExpectation.Content("anothertest.log", ""),
            OutputExpectation.Content("anothertest.log.6", "zero"),
            OutputExpectation.Content("scriptout", "foo"));
    }

    [Fact]
    public void Test0008_CompressMailFirst()
    {
        Preptest("test.log", 1);
        GenConfig("test-config.8", Config8);
        Run("test-config.8", "--force");
        ExitCode.Should().Be(0);
        CheckOutput(
            OutputExpectation.Content("test.log", ""),
            OutputExpectation.Content("test.log.1.gz", "zero", compressed: true),
            OutputExpectation.Content("scriptout", "foo"));

        CheckMail("test.log", "zero");
    }

    [Fact]
    public void Test0009_CompressRotateZeroMailFirst()
    {
        Preptest("test.log", 1);
        GenConfig("test-config.9", Config9);
        Run("test-config.9", "--force");
        ExitCode.Should().Be(0);
        CheckOutput(
            OutputExpectation.Content("test.log", ""),
            OutputExpectation.Content("scriptout", "foo"));

        CheckMail("test.log", "zero");
    }

    [Fact]
    public void Test0010_DelayCompressMailFirst()
    {
        Preptest("test.log", 1);
        GenConfig("test-config.10", Config10);
        Run("test-config.10", "--force");
        ExitCode.Should().Be(0);
        CheckOutput(
            OutputExpectation.Content("test.log", ""),
            OutputExpectation.Content("test.log.1", "zero"));

        WriteFile("test.log", "newfile\n");
        Run("test-config.10", "--force");
        ExitCode.Should().Be(0);
        CheckOutput(
            OutputExpectation.Content("test.log", ""),
            OutputExpectation.Content("test.log.1", "newfile"),
            OutputExpectation.Content("test.log.2.gz", "zero", compressed: true));

        CheckMail("test.log.1", "newfile");
    }

    [Fact]
    public void Test0011_CompressRotateOneMailLast()
    {
        /* reference: preptest test.log 11 2 1 - the base stays plain, only the
         * backups are pre-compressed (Preptest(compressed) would gzip the base
         * too). */
        WriteFile("test.log", "zero\n");
        WriteFile("test.log.1", "first\n");
        GzipCompress(P("test.log.1"));
        WriteFile("test.log.2", "second\n");
        GzipCompress(P("test.log.2"));
        GenConfig("test-config.11", Config11);
        Run("test-config.11", "--force");
        ExitCode.Should().Be(0);
        CheckOutput(
            OutputExpectation.Content("test.log", ""),
            OutputExpectation.Content("scriptout", "foo"));

        CheckMail("test.log.2.gz", "first");
    }

    [Fact]
    public void Test0012_OldDirRelativeMissingDirectory()
    {
        Preptest("test.log", 1);
        GenConfig("test-config.12", Config12);
        if (Directory.Exists(P("testdir")))
            Directory.Delete(P("testdir"), true);

        Run("test-config.12", "--force");
        ExitCode.Should().NotBe(0, "rotation into a missing olddir must fail");

        Directory.CreateDirectory(P("testdir"));
        Run("test-config.12", "--force");
        ExitCode.Should().Be(0);
        CheckOutput(
            OutputExpectation.Content("test.log", ""),
            OutputExpectation.Content("testdir" + Path.DirectorySeparatorChar + "test.log.1", "zero"));
    }

    [Fact]
    public void Test0013_OldDirAbsoluteCreateOldDir()
    {
        Preptest("test.log", 1);
        GenConfig("test-config.13", Config13);
        if (Directory.Exists(P("testdir")))
            Directory.Delete(P("testdir"), true);

        Run("test-config.13", "--force");
        ExitCode.Should().Be(0);
        Directory.Exists(P("testdir")).Should().BeTrue("olddir should be created by createolddir");
        CheckOutput(
            OutputExpectation.Content("test.log", ""),
            OutputExpectation.Content("testdir" + Path.DirectorySeparatorChar + "test.log.1", "zero"));

        // Run again: existing dir must be reused and the old content rotated out
        WriteFile("test.log", "first\n");
        Run("test-config.13", "--force");
        ExitCode.Should().Be(0);
        CheckOutput(
            OutputExpectation.Content("test.log", ""),
            OutputExpectation.Content("testdir" + Path.DirectorySeparatorChar + "test.log.1", "first"));
    }

    [Fact]
    public void Test0014_DateExtDateFormat()
    {
        Preptest("test.log", 1);
        GenConfig("test-config.14", Config14);
        Run("test-config.14", "--force");
        ExitCode.Should().Be(0);

        string dateString = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        CheckOutput(
            OutputExpectation.Content("test.log", ""),
            OutputExpectation.Content("test.log." + dateString, "zero"));
    }

    [Fact]
    public void Test0015_Shred()
    {
        Preptest("test.log", 1);
        GenConfig("test-config.15", Config15);
        Run("test-config.15", "--force");
        ExitCode.Should().Be(0);

        Run("test-config.15", "--force");
        ExitCode.Should().Be(0);

        CheckOutput(
            OutputExpectation.Exist("test.log"),
            OutputExpectation.Content("test.log.1", ""));
    }

    [Fact]
    public void Test0016_MaxSize()
    {
        Preptest("test.log", 1);
        GenConfig("test-config.16", Config16);

        WriteFile("test.log", "a\n");
        File.Delete(P("test.log.1"));
        SeedStateNow("test.log");
        Run("test-config.16");
        ExitCode.Should().Be(0);
        File.Exists(P("test.log.1")).Should().BeFalse("log with 1 byte should not be rotated");

        WriteFile("test.log", "zero\n");
        Run("test-config.16");
        ExitCode.Should().Be(0);
        CheckOutput(
            OutputExpectation.Content("test.log", ""),
            OutputExpectation.Content("test.log.1", "zero"));
    }

    // =====================================================================
    // configs (ported from test-config.N.in)
    // =====================================================================

    private const string Config1 = """
        create

        "&DIR&/test.log" {
            daily
            rotate 2
            mail user@invalid.
            maillast
        }
        """;

    private const string Config2 = """
        "&DIR&/test.log" {
            monthly
            rotate 2
            mail user@invalid.
            maillast
        }
        """;

    private const string Config3 = """
        create

        "&DIR&/test*.log" {
            monthly
            rotate 1
            mail user@invalid.
            maillast

            postrotate
        """ + AppendFooScript + """
            endscript
        }
        """;

    private const string Config4 = """
        create

        "&DIR&/test*.log" {
            monthly
            rotate 1
            mail user@invalid.
            maillast
            sharedscripts

            postrotate
        """ + AppendFooScript + """
            endscript
        }
        """;

    private const string Config5 = """
        create

        "&DIR&/test.log" "&DIR&/anothertest.log" {
            monthly
            rotate 1
            mail user@invalid.
            maillast
            sharedscripts

            postrotate
        """ + AppendFooScript + """
            endscript
        }
        """;

    private const string Config6 = """
        create

        "&DIR&/test.log" "&DIR&/anothertest.log" {
            monthly
            rotate 1
            start 0
            mail user@invalid.
            maillast
            sharedscripts

            postrotate
        """ + AppendFooScript + """
            endscript
        }
        """;

    private const string Config7 = """
        create

        "&DIR&/test.log" "&DIR&/anothertest.log" {
            monthly
            rotate 3
            start 6
            mail user@invalid.
            maillast
            sharedscripts

            postrotate
        """ + AppendFooScript + """
            endscript
        }
        """;

    private const string Config8 = """
        create

        compress

        "&DIR&/test.log" {
            monthly
            rotate 3
            mail user@invalid.
            mailfirst
            sharedscripts

            postrotate
        """ + AppendFooScript + """
            endscript
        }
        """;

    private const string Config9 = """
        create

        compress

        "&DIR&/test.log" {
            monthly
            rotate 0
            mail user@invalid.
            mailfirst
            sharedscripts

            postrotate
        """ + AppendFooScript + """
            endscript
        }
        """;

    private const string Config10 = """
        create

        "&DIR&/test.log" {
            daily
            rotate 3
            compress
            delaycompress
            create
            mailfirst
            mail user@invalid.
        }
        """;

    private const string Config11 = """
        create

        compress

        "&DIR&/test.log" {
            monthly
            rotate 1
            mail user@invalid.
            maillast
            sharedscripts

            postrotate
        """ + AppendFooScript + """
            endscript
        }
        """;

    private const string Config12 = """
        create

        "&DIR&/test.log" {
            monthly
            rotate 1
            olddir "&DIR&/testdir"
            nocreateolddir
        }
        """;

    private const string Config13 = """
        create

        "&DIR&/test.log" {
            monthly
            rotate 1
            olddir "&DIR&/testdir"
            createolddir 700 root root
        }
        """;

    private const string Config14 = """
        create

        "&DIR&/test.log" {
            daily
            dateext
            dateformat .%Y-%m-%d
            rotate 1
        }
        """;

    private const string Config15 = """
        create

        "&DIR&/test.log" {
            daily
            shred
            shredcycles 20
            rotate 1
        }
        """;

    private const string Config16 = """
        create

        "&DIR&/test.log" {
            weekly
            maxsize 4
            rotate 1
        }
        """;
}