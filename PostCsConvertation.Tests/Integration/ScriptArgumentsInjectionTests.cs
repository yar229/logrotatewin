using PostCsConvertation.Tests.Integration.Base;
using PostCsConvertation.Tests.Integration.Wrappers;
using System;
using Xunit;
using Op = LogRotate.Consts.ConfigSectionDirectives;

namespace PostCsConvertation.Tests.Integration;

[Trait("Category", "Integration")]
public class ScriptArgumentsInjectionTests : NewWaveIntegrationTestBase
{
    public ScriptArgumentsInjectionTests(ITestOutputHelper output)
        : base(output)
    {
    }

    [Fact]
    public void PostRotate_ShellMetaCharsInLogName_ShouldPassThroughOriginalValue()
    {
        string envVar = Environment.GetEnvironmentVariable("ALLUSERSPROFILE")
            ?? throw new InvalidOperationException("test requires the ALLUSERSPROFILE env var");

        string fileName = "bad & name^file%ALLUSERSPROFILE%.log";
        var log = Runner.NewLog(fileName).Create();
        var marker = Runner.NewFile("marker-meta.txt");

        Runner
            .WithLog(log, l => l
                .ShouldNotBe()
                .ShouldBe(Ext(".1")))
            .WithFile(marker, l => l
                .ShouldBe()
                .ShouldContain(log.Filepath)
                .ShouldContain($"{log.Filepath}.1")
                .ShouldNotContain(envVar))
            .WithConfig(c => c
                .WithSection(XPattern.AllLogs, s => s
                    .With(Op.Rotate, 2)
                    .With(Op.Monthly)
                    .WithScript(Op.PostRotate, $"echo ARG=[%1] ROT=[%2] >> \"{marker}\""))
                .Create())
            .RunAndCheck();
    }
}