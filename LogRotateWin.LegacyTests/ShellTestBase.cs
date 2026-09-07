using FluentAssertions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace LogRotateWin.LegacyTests;

/// <summary>
/// Base class for the tests ported from logrotate's shell test-suite
/// (_logrotate-3.22.0/test/test-*.sh).
/// It mirrors the helpers from test-common.sh (preptest, createlogs,
/// checkoutput, checkmail, genconfig).
/// </summary>
public abstract class ShellTestBase : IDisposable
{
    private const string BaseTestDir = "c:\\1";

    public string TestDir { get; }

    public string StatePath => Path.Combine(TestDir, "state");

    public string MailOutPath => Path.Combine(TestDir, "mail-out");

    public string ScriptOutPath => Path.Combine(TestDir, "scriptout");

    public string Log { get; private set; } = string.Empty;

    public string StdOut { get; private set; } = string.Empty;

    public string StdErr { get; private set; } = string.Empty;

    /// <summary>Exit code of the last logrotate run.</summary>
    public int ExitCode { get; private set; }

    private readonly string _exePath;

    /// <summary>Full path of the logrotate executable under test.</summary>
    protected string ExePath => _exePath;

    protected ShellTestBase()
    {
        if (!Directory.Exists(BaseTestDir))
            Directory.CreateDirectory(BaseTestDir);

        TestDir = Directory.CreateDirectory(Path.Combine(BaseTestDir, Guid.NewGuid().ToString())).FullName;
        _exePath = LocateLogRotateExe();

        File.WriteAllText(Path.Combine(TestDir, "mailer.cmd"), MailerScript);
        File.WriteAllText(Path.Combine(TestDir, "compress.cmd"), CompressScript);
        File.WriteAllText(Path.Combine(TestDir, "compress-error.cmd"), CompressErrorScript);
    }

    public virtual void Dispose()
    {
        try
        {
            if (Environment.GetEnvironmentVariable("LR_KEEP_TMP") == "1")
                return;
            if (Directory.Exists(TestDir))
                Directory.Delete(TestDir, true);
        }
        catch
        {
            // ignore
        }
    }

    // =====================================================================
    // Running logrotate
    // =====================================================================

    /// <summary>
    /// Equivalent of "$RLR" from test-common.sh:
    /// logrotate -v -m mailer -s state &lt;args&gt;
    /// </summary>
    public int Run(params string[] args)
    {
        var full = new List<string> { "-v", "-m", "mailer.cmd", "-s", "state" };
        full.AddRange(args);
        return RunLogRotate(full.ToArray());
    }

    /// <summary>
    /// Equivalent of "$RLR test-config... --force" etc. Arguments are passed
    /// on the command line. The working directory is TestDir.
    /// </summary>
    public int RunLogRotate(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _exePath,
            WorkingDirectory = TestDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var a in args)
            psi.ArgumentList.Add(a);

        var sbOut = new StringBuilder();
        var sbErr = new StringBuilder();

        using (var process = new Process())
        {
            process.StartInfo = psi;
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null)
                    sbOut.AppendLine(e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null)
                    sbErr.AppendLine(e.Data);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.WaitForExit();

            StdOut = sbOut.ToString();
            StdErr = sbErr.ToString();
            Log = StdOut + StdErr;
            ExitCode = process.ExitCode;
            return process.ExitCode;
        }
    }

    // =====================================================================
    // test-common.sh helpers
    // =====================================================================

    /// <summary>
    /// Well-known "Everyone" SID (S-1-1-0), the stand-in for the POSIX named
    /// user "nobody" in the ACL tests (32/33/35/48).
    /// </summary>
    protected static System.Security.Principal.SecurityIdentifier
        EveryoneSid { get; } =
        new(System.Security.Principal.WellKnownSidType.WorldSid, null);

    /// <summary>
    /// Stand-in for `setfacl -m u:nobody:rwx`: grants the Everyone SID Full
    /// Access on a file inside TestDir via icacls.
    /// </summary>
    protected void GrantEveryoneAccess(string relativePath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "icacls.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add(P(relativePath));
        psi.ArgumentList.Add("/grant");
        psi.ArgumentList.Add($"*{EveryoneSid.Value}:(F)");

        using var p = Process.Start(psi);
        p!.WaitForExit();
        p.ExitCode.Should().Be(0);
    }

    /// <summary>Maps log "number" to the word written by createlog().</summary>
    public static string Word(int num) => num switch
    {
        0 => "zero",
        1 => "first",
        2 => "second",
        3 => "third",
        4 => "fourth",
        5 => "fifth",
        6 => "sixth",
        7 => "seventh",
        8 => "eighth",
        9 => "ninth",
        _ => throw new ArgumentOutOfRangeException(nameof(num)),
    };

    /// <summary>Full path of a file inside TestDir.</summary>
    public string P(string relativePath)
        => Path.Combine(TestDir, relativePath);

    /// <summary>Writes a relative file (content + optional trailing newline).</summary>
    public void WriteFile(string relativePath, string content)
        => File.WriteAllText(P(relativePath), content);

    /// <summary>
    /// Equivalent of genconfig(): writes a config file into TestDir replacing
    /// the &amp;DIR&amp; placeholder with the test directory.
    /// </summary>
    public string GenConfig(string relativeName, string template)
    {
        string path = P(relativeName);
        string resolved = template.Replace("&DIR&", TestDir, StringComparison.Ordinal);
        File.WriteAllText(path, resolved);

        if (Environment.GetEnvironmentVariable("LR_DUMP_CONFIG") == "1")
        {
            File.WriteAllText(@"C:\Users\yar229\AppData\Local\Temp\opencode\dump-config.txt", resolved);
        }
        return path;
    }

    /// <summary>equivalent of `echo $what &gt; $file`.</summary>
    public void CreateLog(int num, string file, bool compressed = false)
    {
        File.WriteAllText(P(file), Word(num) + "\n");
        if (compressed)
            GzipCompress(P(file));
    }

    /// <summary>equivalent of createlogs(): removes base* and creates base + base.N..</summary>
    public void CreateLogs(string baseName, int numlogs, bool compressed = false)
    {
        foreach (var f in Directory.GetFiles(TestDir, baseName + "*"))
            File.Delete(f);

        CreateLog(0, baseName, compressed);
        for (int num = 1; num <= numlogs; num++)
            CreateLog(num, baseName + "." + num, compressed);
    }

    /// <summary>equivalent of preptest(): genconfig + createlogs + rm state.</summary>
    public void Preptest(string baseName, int numlogs, bool compressed = false)
    {
        foreach (var f in Directory.GetFiles(TestDir, baseName + "*"))
            File.Delete(f);
        if (File.Exists(StatePath))
            File.Delete(StatePath);

        CreateLogs(baseName, numlogs, compressed);
    }

    /// <summary>equivalent of cleanup() from test-common.sh.</summary>
    public void Cleanup()
    {
        foreach (var pattern in new[] { "test*.log*", "anothertest*.log*", "different*.log*" })
        {
            foreach (var f in Directory.GetFiles(TestDir, pattern))
                File.Delete(f);
        }

        foreach (var name in new[] { "state", "scriptout", "mail-out", "compress-args", "compress-env" })
        {
            if (File.Exists(P(name)))
                File.Delete(P(name));
        }
    }

    /// <summary>Writes the state file with the given raw lines.</summary>
    public void State(params string[] lines)
    {
        File.WriteAllLines(StatePath, lines);
    }

    /// <summary>
    /// Writes a state entry marking each file as rotated right now.
    /// Port of logrotate's newState()/"use mtime as last rotated when no state"
    /// behavior. LogRotateWin initializes a missing state entry to 1900 and
    /// would rotate the log on the very first run; seeding "rotated now"
    /// reproduces the reference suite's "no state -> no rotation" semantics.
    /// </summary>
    public void SeedStateNow(params string[] files)
    {
        var now = DateTime.Now;
        var lines = new List<string> { "logrotate state -- version 2" };
        foreach (var f in files)
        {
            string escaped = P(f).Replace("\\", "\\\\");
            lines.Add($"\"{escaped}\" {now.Year}-{now.Month}-{now.Day}-{now.Hour}:{now.Minute}:{now.Second}");
        }
        State(lines.ToArray());
    }

    /// <summary>Appends a line to the state file.</summary>
    public void AppendState(string line)
    {
        File.AppendAllText(StatePath, line + "\n");
    }

    /// <summary>
    /// Realized "checkmail()" from test-common.sh. The mailer.cmd helper writes
    /// the mail command line ("-s &lt;subject&gt; &lt;address&gt;") plus the piped
    /// log body into mail-out; this asserts the exact two lines, normalized for
    /// line endings, with the subject being the absolute path of the file the
    /// reference would use ("$PWD/&lt;mailFile&gt;").
    /// </summary>
    public void CheckMail(string mailFile, string contents)
    {
        File.Exists(MailOutPath).Should().BeTrue("no mail-out: mailer.cmd never ran");
        string expected =
            "-s " + P(mailFile) + " user@invalid.\n" + contents;
        string actual = File.ReadAllText(MailOutPath);
        Normalize(actual).Should().Be(expected, "mail-out does not contain the expected mail");
    }

    /// <summary>
    /// equivalent of checkoutput(): every item is (file, expected contents)
    /// with optional "compressed" flag and "just-exists" flag. NUL bytes are
    /// stripped like `cat | tr -d '\000'`.
    /// </summary>
    public void CheckOutput(params OutputExpectation[] items)
    {
        foreach (var item in items)
        {
            string full = P(item.File);
            if (item.MustNotExist)
            {
                File.Exists(full).Should().BeFalse($"file {item.File} should NOT exist");
                continue;
            }
            if (item.JustExists)
            {
                File.Exists(full).Should().BeTrue($"file {item.File} must exist");
                continue;
            }

            File.Exists(full).Should().BeTrue($"file {item.File} does not exist");
            string contents = item.Compressed
                ? GzipDecompress(full)
                : File.ReadAllText(full).Replace("\0", string.Empty);

            Normalize(contents).Should().Be(Normalize(item.Expected),
                $"file {item.File} does not contain expected results");
        }
    }

    /// <summary>Asserts a file exists (or not) and compares content.</summary>
    public void AssertFileContent(string relativePath, string expected, bool compressed = false)
    {
        string full = P(relativePath);
        File.Exists(full).Should().BeTrue($"file {relativePath} does not exist");
        string contents = compressed ? GzipDecompress(full) : File.ReadAllText(full);
        Normalize(contents).Should().Be(Normalize(expected), $"file {relativePath} content");
    }

    /// <summary>
    /// Gzip-compress the given file in place (used to build pre-compressed
    /// log files instead of external gzip).
    /// </summary>
    public static void GzipCompress(string filepath)
    {
        var gzPath = filepath + ".gz";
        using (var input = File.OpenRead(filepath))
        using (var output = File.Create(gzPath))
        using (var gz = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionLevel.Optimal))
        {
            input.CopyTo(gz);
        }
        File.Delete(filepath);
    }

    /// <summary>Decompresses a gzip file to a string.</summary>
    public static string GzipDecompress(string filepath)
    {
        using (var input = File.OpenRead(filepath))
        using (var gz = new System.IO.Compression.GZipStream(input, System.IO.Compression.CompressionMode.Decompress))
        using (var reader = new StreamReader(gz))
        {
            return reader.ReadToEnd();
        }
    }

    /// <summary>Normalizes line endings so Windows-produced output matches expected.</summary>
    private static string Normalize(string? s)
        => (s ?? string.Empty).Replace("\r\n", "\n").TrimEnd('\n');

    // =====================================================================
    // scripts required by the tests (Windows counterparts of ./mailer etc.)
    // =====================================================================

    /// <summary>
    /// Windows counterpart of the 'mailer' shell script:
    /// writes "$*" (i.e. "-s subject address") to mail-out, then appends the
    /// piped log body (stdin). 'findstr "^"' echoes the redirected stdin.
    /// </summary>
    protected virtual string MailerScript => """
        @echo off
        > mail-out echo %*
        >> mail-out findstr "^"
        """;

    /// <summary>Windows counterpart of the 'compress' shell script.</summary>
    protected virtual string CompressScript => """
        @echo off
        echo gzip %* > compress-args
        set > compress-env
        """;

    /// <summary>Windows counterpart of the 'compress-error' shell script.</summary>
    protected virtual string CompressErrorScript => """
        @echo off
        echo compression error 1>&2
        """;

    private static string LocateLogRotateExe()
    {
        string testAssemblyPath = typeof(ShellTestBase).Assembly.Location;
        string testBinDir = Path.GetDirectoryName(testAssemblyPath)!;

        string exePath = Path.GetFullPath(Path.Combine(testBinDir, "..", "..", "..", "..", "LogRotateWin", "bin", "Debug", "net10.0", "logrotate.exe"));
        if (!File.Exists(exePath))
            exePath = Path.GetFullPath(Path.Combine(testBinDir, "..", "..", "..", "..", "LogRotateWin", "bin", "Release", "net10.0", "logrotate.exe"));

        if (!File.Exists(exePath))
            throw new FileNotFoundException($"Could not find logrotate.exe, looked in: {exePath}");

        return exePath;
    }
}

/// <summary>One row of checkoutput().</summary>
public readonly record struct OutputExpectation(
    string File,
    string? Expected = null,
    bool Compressed = false,
    bool JustExists = false,
    bool MustNotExist = false)
{
    public static OutputExpectation Exist(string file)
        => new(file, JustExists: true);

    public static OutputExpectation NotExist(string file)
        => new(file, MustNotExist: true);

    public static OutputExpectation Content(string file, string expected, bool compressed = false)
        => new(file, expected, compressed);
}