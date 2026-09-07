using LogRotate.Consts;
using PostCsConvertation.Tests.Integration.Base;
using PostCsConvertation.Tests.Integration.Wrappers;
using Xunit;
using Op = LogRotate.Consts.ConfigSectionDirectives;

namespace PostCsConvertation.Tests.Integration;

[Trait("Category", "Integration")]
public class MailScriptTests : NewWaveIntegrationTestBase
{
    public MailScriptTests(ITestOutputHelper output)
        : base(output)
    {
    }

    private const string DefaultEmail = "yar229@home.loc";

    [Fact]
    public void MailScriptWithInplaceCmdParams_ShouldBePassed()
    {
        var log = Runner.NewLog("log-a.log").Create();
        var markerMail = Runner.NewFile("marker-mail.txt");

        Runner
            .WithLog(log, l => l
                .ShouldNotBe()
                .ShouldBe(Ext(".1")))
            .WithFile(markerMail, l => l
                .ShouldBe()
                .ShouldContain(DefaultEmail)
                .ShouldContain($"{log}.1")
                .ShouldNotContain(XPattern.AllLogs))
            .WithConfig(c => c
                .WithSection(XPattern.AllLogs, s => s
                    .With(Op.Rotate, 1)
                    .With(Op.MailFirst)
                    .With(Op.Mail, DefaultEmail)
                    .WithScript(Op.MailScript, $"echo mail file %1 for %2 >> {markerMail}"))
                .Create())
            .RunAndCheck();
    }

    [Fact]
    public void MailScriptWithEnviromentCmdParams_ShouldBePassed()
    {
        var log = Runner.NewLog("log-a.log").Create();
        var markerMail = Runner.NewFile("marker-mail.txt");

        Runner
            .WithLog(log, l => l
                .ShouldNotBe()
                .ShouldBe(Ext(".1")))
            .WithFile(markerMail, l => l
                .ShouldBe()
                .ShouldContain(DefaultEmail)
                .ShouldContain($"{log}.1")
                .ShouldNotContain(XPattern.AllLogs))
            .WithConfig(c => c
                .WithSection(XPattern.AllLogs, s => s
                    .With(Op.Rotate, 1)
                    .With(Op.MailFirst)
                    .With(Op.Mail, DefaultEmail)
                    .WithScript(Op.MailScript, $"echo mail file %{ScriptEnviromentVariables.Log}% for %{ScriptEnviromentVariables.MailTo}% >> {markerMail}"))
                .Create())
            .RunAndCheck();
    }

    [Fact]
    public void GlobalMailScriptWithInplaceCmdParams_ShouldWorkForAll()
    {
        var log = Runner.NewLog("log-a.log").Create();
        var markerMail = Runner.NewFile("marker-mail.txt");

        Runner
            .WithLog(log, l => l
                .ShouldNotBe()
                .ShouldBe(Ext(".1")))
            .WithFile(markerMail, l => l
                .ShouldBe()
                .ShouldContain(DefaultEmail)
                .ShouldContain($"{log}.1")
                .ShouldNotContain(XPattern.AllLogs))
            .WithConfig(c => c
                .WithGlobalSection(s => s
                    .WithScript(Op.MailScript, $"echo mail file %1 for %2 >> {markerMail}"))
                .WithSection(XPattern.AllLogs, s => s
                    .With(Op.Rotate, 1)
                    .With(Op.MailFirst)
                    .With(Op.Mail, DefaultEmail)
                    .WithScript(Op.MailScript, $"echo mail file %1 for %2 >> {markerMail}"))
                .Create())
            .RunAndCheck();
    }
}
