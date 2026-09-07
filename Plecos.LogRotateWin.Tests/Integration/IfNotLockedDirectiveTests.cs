using System;
using System.IO;
using FluentAssertions;
using Xunit;

namespace logrotate.Tests.Integration
{
    [Trait("Category", "Integration")]
    public class IfNotLockedDirectiveTests : IntegrationTestBase
    {
        public IfNotLockedDirectiveTests(ITestOutputHelper output) : base(output)
        {
        }

        [Fact]
        public void RotateLog_WhenFileIsLocked_ShouldSkipRotation()
        {
            // Arrange
            string logFile = Path.Combine(TestDir, "test.log");
            File.WriteAllText(logFile, "Test log content\n");

            string stateFile = Path.Combine(TestDir, "state.txt");
            string configContent = $@"
""{logFile}"" {{
    rotate 2
    daily
    ifnotlocked
}}
";
            string configFile = TestHelpers.CreateTempConfigFile(configContent);

            try
            {
                // Act - hold the file open exclusively by another process
                using (var fs = new FileStream(logFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    int exitCode = RunLogRotate("-s", stateFile, "-f", configFile, "-v");

                    // Assert
                    exitCode.Should().Be(0, "locked log should be skipped, not an error");
                    File.Exists($"{logFile}.1").Should().BeFalse("locked log file must not be rotated");
                    Log.Should().MatchRegex("skipping rotation", "warning about the locked file must appear");
                }
            }
            finally
            {
                TestHelpers.CleanupPath(configFile);
            }
        }

        [Fact]
        public void RotateLog_WhenFileIsUnlocked_ShouldRotate()
        {
            // Arrange
            string logFile = Path.Combine(TestDir, "test.log");
            File.WriteAllText(logFile, "Test log content\n");

            string stateFile = Path.Combine(TestDir, "state.txt");
            string configContent = $@"
""{logFile}"" {{
    rotate 2
    daily
    ifnotlocked
}}
";
            string configFile = TestHelpers.CreateTempConfigFile(configContent);

            try
            {
                // Act - no other process holds the file
                int exitCode = RunLogRotate("-s", stateFile, "-f", configFile);

                // Assert
                exitCode.Should().Be(0, "unlocked log should rotate successfully");
                File.Exists($"{logFile}.1").Should().BeTrue("unlocked log file should be rotated");
            }
            finally
            {
                TestHelpers.CleanupPath(configFile);
            }
        }

        [Fact]
        public void RotateLockedLog_AfterRelease_ShouldRotateOnNextRun()
        {
            // Arrange
            string logFile = Path.Combine(TestDir, "test.log");
            File.WriteAllText(logFile, "Test log content\n");

            string stateFile = Path.Combine(TestDir, "state.txt");
            string configContent = $@"
""{logFile}"" {{
    rotate 2
    daily
    ifnotlocked
}}
";
            string configFile = TestHelpers.CreateTempConfigFile(configContent);

            try
            {
                // Act 1 - locked: skip
                using (var fs = new FileStream(logFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    RunLogRotate("-s", stateFile, "-f", configFile);
                }

                File.Exists($"{logFile}.1").Should().BeFalse("locked log must not be rotated while locked");

                // Act 2 - released: rotate
                RunLogRotate("-s", stateFile, "-f", configFile);

                // Assert
                File.Exists($"{logFile}.1").Should().BeTrue("log should be rotated after the lock is released");
            }
            finally
            {
                TestHelpers.CleanupPath(configFile);
            }
        }
    }
}