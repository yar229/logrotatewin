using FluentAssertions;
using LogRotate;
using System;
using System.IO;
using System.Security.Principal;
using Xunit;

namespace logrotate.Tests.Integration
{
    /// <summary>
    /// Integration tests for the su directive.
    ///
    /// Su mirrors the switch_user() behavior of the reference: the account
    /// given in 'su owner group' is used as the owner/group of re-created log
    /// files (create) and of directories created by createolddir, unless
    /// create/createolddir specifies an explicit owner/group.
    /// </summary>
    public class SuDirectiveTests : IntegrationTestBase
    {
        public SuDirectiveTests(ITestOutputHelper output) : base(output)
        {
        }

        private static readonly SecurityIdentifier UsersSid =
            (SecurityIdentifier)new NTAccount("BUILTIN\\Users").Translate(typeof(SecurityIdentifier));

        [Fact]
        public void RotateLog_WithSuAndCreate_ShouldApplySuGroupToNewLogDacl()
        {
            // Arrange
            string logFile = Path.Combine(TestDir, "test.log");
            File.WriteAllText(logFile, "Log content\n");

            string stateFile = Path.Combine(TestDir, "state.txt");
            string configContent = $@"
""{logFile}"" {{
    rotate 2
    daily
    create 0644
    su {Environment.UserName} Users
}}
";
            string configFile = TestHelpers.CreateTempConfigFile(configContent);

            try
            {
                // Act
                int exitCode = RunLogRotate("-s", stateFile, "-f", configFile);

                // Assert - the new log file is acknowledged by the su group
                exitCode.Should().Be(0, "rotation with su should succeed");
                File.Exists(logFile).Should().BeTrue("the log file should be re-created");
                AclApi.DefinesAccessAce(logFile, UsersSid).Should().BeTrue(
                    "the DACL of the re-created log must grant the su group (owner/group buckets)");
            }
            finally
            {
                TestHelpers.CleanupPath(configFile);
            }
        }

        [Fact]
        public void CreateOldDir_WithSu_ShouldApplySuGroupToCreatedOldDir()
        {
            // Arrange
            string logFile = Path.Combine(TestDir, "test.log");
            File.WriteAllText(logFile, "Log content\n");

            string oldDir = Path.Combine(TestDir, "rotated");
            string stateFile = Path.Combine(TestDir, "state.txt");
            string configContent = $@"
""{logFile}"" {{
    rotate 2
    daily
    create
    olddir {oldDir}
    createolddir 0770
    su {Environment.UserName} Users
}}
";
            string configFile = TestHelpers.CreateTempConfigFile(configContent);

            try
            {
                // Act
                int exitCode = RunLogRotate("-s", stateFile, "-f", configFile);

                // Assert
                exitCode.Should().Be(0, "rotation with su and createolddir should succeed");
                Directory.Exists(oldDir).Should().BeTrue("createolddir should create the olddir");
                File.Exists(Path.Combine(oldDir, "test.log.1")).Should().BeTrue("rotated file should go into olddir");
                AclApi.DefinesAccessAce(oldDir, UsersSid).Should().BeTrue(
                    "the DACL of the created olddir must grant the su group");
            }
            finally
            {
                TestHelpers.CleanupPath(configFile);
                TestHelpers.CleanupPath(oldDir);
            }
        }

        [Fact]
        public void RotateLog_WithSuForAnotherUser_ShouldWarnScriptsRunUnderCurrentAccount()
        {
            // Arrange
            string logFile = Path.Combine(TestDir, "test.log");
            File.WriteAllText(logFile, "Log content\n");

            string stateFile = Path.Combine(TestDir, "state.txt");
            string configContent = $@"
""{logFile}"" {{
    rotate 2
    daily
    create 0644
    su SYSTEM Users
}}
";
            string configFile = TestHelpers.CreateTempConfigFile(configContent);

            try
            {
                // Act
                int exitCode = RunLogRotate("-s", stateFile, "-f", configFile);

                // Assert - a warning explaining the Windows limitation is logged
                exitCode.Should().Be(0, "rotation should proceed despite the su mismatch");
                Log.Should().MatchRegex("cannot switch users on Windows",
                    "a warning about scripts running under the current account must be logged "
                    + "when 'su' names a different user");
                Log.Should().MatchRegex("will run under the current account",
                    "the warning must name the scripts affected by the su limitation");
            }
            finally
            {
                TestHelpers.CleanupPath(configFile);
            }
        }

        [Fact]
        public void RotateLog_WithSuForCurrentUser_ShouldNotWarn()
        {
            // Arrange
            string logFile = Path.Combine(TestDir, "test.log");
            File.WriteAllText(logFile, "Log content\n");

            string stateFile = Path.Combine(TestDir, "state.txt");
            string configContent = $@"
""{logFile}"" {{
    rotate 2
    daily
    create 0644
    su {Environment.UserName} Users
}}
";
            string configFile = TestHelpers.CreateTempConfigFile(configContent);

            try
            {
                // Act
                int exitCode = RunLogRotate("-s", stateFile, "-f", configFile);

                // Assert - no warning when the process already runs as the su user
                exitCode.Should().Be(0);
                Log.Should().NotMatch("will run under the current account",
                    "scripts are effectively running under the 'su' account already");
            }
            finally
            {
                TestHelpers.CleanupPath(configFile);
            }
        }
    }
}