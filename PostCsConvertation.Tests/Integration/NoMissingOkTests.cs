using FluentAssertions;
using PostCsConvertation.Tests.Integration.Base;
using PostCsConvertation.Tests.Integration.Wrappers;
using System;
using System.IO;
using System.Text;
using Xunit;
using Op = LogRotate.Consts.ConfigSectionDirectives;

namespace PostCsConvertation.Tests.Integration;


/// <summary>
/// 'nomissingok' config directive tests
/// </summary>
/// <remarks>
/// nomissingok by default is true
/// </remarks>
[Trait("Category", "Integration")]
public class NoMissingOkTests : NewWaveIntegrationTestBase
{
    public NoMissingOkTests(ITestOutputHelper output)
        : base(output)
    {
    }

    [Fact(DisplayName = $"{Op.NoMissingOk} causes error when file is missing")]
    public void RotateLog_WithNoMissingOkAndMissingFile_ShouldError()
    {
        var log = Runner.NewLog(); // deliberately don't create the logfile

        Runner
            .WithLog(log, l => l
                .ShouldNotBe()
                .ShouldNotBe(Ext(".1")))
            .WithConfig(c => c
                .WithSection(log, s => s
                    .With(Op.NoMissingOk)
                    .With(Op.Rotate, 3))
                .Create())
            .RunAndCheck()
            .ExitCode.Should().NotBe(0, "logrotate should fail when logfile missing");
    }

    [Fact(DisplayName = $"{Op.NoMissingOk} processes files that exist and skips those that don't")]
    public void RotateLog_WithNoMissingOkAndMultipleFiles_ShouldProcessExisting()
    {
        var missingLog = Runner.NewLog(); // deliberately don't create the logfile
        var existingLog = Runner.NewLog().Create(); 

        Runner
            .WithLog(missingLog, l => l
                .ShouldNotBe()
                .ShouldNotBe(Ext(".1")))
            .WithLog(existingLog, l => l
                .ShouldNotBe()
                .ShouldBe(Ext(".1")))
            .WithConfig(c => c
                .WithSection(missingLog, s => s
                    .With(Op.NoMissingOk)
                    .With(Op.Rotate, 3))
                .WithSection(existingLog, s => s
                    .With(Op.NoMissingOk)
                    .With(Op.Rotate, 3))
                .Create())
            .RunAndCheck()
            .ExitCode.Should().NotBe(0, "existing logs should be rotated, but still returns error");
    }
}
