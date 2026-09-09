using PostCsConvertation.Tests.Integration.Base;
using PostCsConvertation.Tests.Integration.Wrappers;
using Xunit;
using Op = LogRotate.Consts.ConfigSectionDirectives;

namespace PostCsConvertation.Tests.Integration;

[Trait("Category", "Integration")]
public class PrePostRotateTests : NewWaveIntegrationTestBase
{
    public PrePostRotateTests(ITestOutputHelper output)
        :base(output)
    {
    }

    [Fact]
    public void SimpleLogFileWithPrePostRotate_ShoudBeRotated()
    {
        var markerPre = Runner.NewFile("marker-pre.txt");
        var markerPost = Runner.NewFile("marker-post.txt");

        Runner
            .WithLog("log-a.log", l => l.Create()
                .ShouldNotBe()
                .ShouldBe(Ext(".1")))
            .WithFiles([markerPre, markerPost], l => l
                .ShouldBe())
            .WithConfig(c => c
                .WithSection(XPattern.AllLogs, s => s
                    .With(Op.Rotate, 2)
                    .With(Op.Monthly)
                    .WithEcho(Op.PreRotate, markerPre)
                    .WithEcho(Op.PostRotate, markerPost))
                .Create())
            .RunAndCheck();
    }

    [Fact]
    public void PrePostRotate_PatternShouldBePassed()
    {
        var log = Runner.NewLog("log-a.log").Create();
        var markerPre = Runner.NewFile("marker-pre.txt");
        var markerPost = Runner.NewFile("marker-post.txt");

        Runner
            .WithLog(log, l => l
                .ShouldNotBe()
                .ShouldBe(Ext(".1")))
            .WithFiles([markerPre, markerPost], l => l
                .ShouldBe()
                .ShouldContain(XPattern.AllLogs)
                .ShouldNotContain(log))
            .WithConfig(c => c
                .WithSection(XPattern.AllLogs, s => s
                    .With(Op.Rotate, 2)
                    .With(Op.Monthly)
                    .With(Op.SharedScripts)
                    .WithScript(Op.PreRotate, $"echo prerotate for %1 >> {markerPre}")
                    .WithScript(Op.PostRotate, $"echo postrotate for %1 >> {markerPost}"))
                .Create())
            .RunAndCheck();
    }

    [Fact]
    public void PrePostRotate_ShoudExecuteForEachLog()
    {
        var logA = Runner.NewLog("log-a.log").Create();
        var logB = Runner.NewLog("log-b.log").Create();
        var markerPre = Runner.NewFile("marker-pre.txt");
        var markerPost = Runner.NewFile("marker-post.txt");

        Runner
            .WithLogs([logA, logB], l => l
                .ShouldNotBe()
                .ShouldBe(Ext(".1")))
            .WithFiles([markerPre, markerPost], l => l
                .ShouldBe()
                .ShouldContain(logA, logB))
            .WithConfig(c => c
                .WithSection(XPattern.AllLogs, s => s
                    .With(Op.Rotate, 2)
                    .With(Op.Monthly)
                    .WithScript(Op.PreRotate, $"echo prerotate for %1 >> {markerPre}")
                    .WithScript(Op.PostRotate, $"echo postrotate for %1 >> {markerPost}"))
                .Create())
            .RunAndCheck();
    }

    [Fact]
    public void PrePostRotate_ShouldExecuteOnlyOnce()
    {
        var logA = Runner.NewLog("log-a.log").Create();
        var logB = Runner.NewLog("log-b.log").Create();
        var pattern = Runner.NewPattern(XPattern.AllLogs);
        var markerPre = Runner.NewFile("marker-pre.txt");
        var markerPost = Runner.NewFile("marker-post.txt");

        Runner
            .WithLogs([logA, logB], l => l
                .ShouldNotBe()
                .ShouldBe(Ext(".1")))
            .WithFiles([markerPre, markerPost], l => l
                .ShouldBe()
                .ShouldContain(pattern)
                .ShouldNotContain(logA, logB))
            .WithConfig(c => c
                .WithSection(pattern, s => s
                    .With(Op.Rotate, 2)
                    .With(Op.Monthly)
                    .With(Op.SharedScripts)
                    .WithScript(Op.PreRotate, $"echo prerotate for %1 >> {markerPre}")
                    .WithScript(Op.PostRotate, $"echo postrotate for %1 >> {markerPost}"))
                .Create())
            .RunAndCheck();
    }

    [Fact]
    public void PrePostRotateWithNoLogs_ShouldNotExecute()
    {
        var markerPre = Runner.NewFile("marker-pre.txt");
        var markerPost = Runner.NewFile("marker-post.txt");

        Runner
            .WithFiles([markerPre, markerPost], l => l
                .ShouldNotBe())
            .WithConfig(c => c
                .WithSection(XPattern.AllLogs, s => s
                    .With(Op.Rotate, 2)
                    .With(Op.Monthly)
                    .With(Op.SharedScripts)
                    .WithScript(Op.PreRotate, $"echo prerotate for %1 >> {markerPre}")
                    .WithScript(Op.PostRotate, $"echo postrotate for %1 >> {markerPost}"))
                .Create())
            .RunAndCheck();
    }

    [Fact]
    public void PrePostRotateWithNoRotatedLogs_ShouldNotExecute()
    {
        var log = Runner.NewLog("log-a.log").Create();
        var markerPre = Runner.NewFile("marker-pre.txt");
        var markerPost = Runner.NewFile("marker-post.txt");

        Runner
            .WithLog(log, l => l
                .ShouldBe()
                .ShouldNotBe(Ext(".1")))
            .WithFiles([markerPre, markerPost], l => l
                .ShouldNotBe())
            .WithState(s => s
                .WithProcessed(log)
                .Create())
            .WithConfig(c => c
                .WithSection(XPattern.AllLogs, s => s
                    .With(Op.Rotate, 2)
                    .With(Op.Monthly)
                    .With(Op.SharedScripts)
                    .WithScript(Op.PreRotate, $"echo prerotate for %1 >> {markerPre}")
                    .WithScript(Op.PostRotate, $"echo postrotate for %1 >> {markerPost}"))
                .Create())
            .RunAndCheck();
    }

    [Fact]
    public void PreRotateCheckScriptEncoding_ShouldBe()
    {
        var ruText = "Провера кириллицы";
        var log = Runner.NewLog("log-a.log").Create();
        var markerPre = Runner.NewFile("marker-pre.txt");

        Runner
            .WithLog(log, l => l
                .ShouldBe(Ext(".1")))
            .WithFile(markerPre, l => l
                .ShouldBe()
                .ShouldContain(ruText))
            .WithConfig(c => c
                .WithSection(XPattern.AllLogs, s => s
                    .With(Op.Rotate, 2)
                    .WithScript(Op.PreRotate, $"echo {ruText}, for %1 >> {markerPre}"))
                .Create())
            .RunAndCheck();
    }
}
