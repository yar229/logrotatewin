using logrotate.Tests.Integration;
using PostCsConvertation.Tests.Integration.Wrappers;
using Xunit;

namespace PostCsConvertation.Tests.Integration.Base;

public abstract class NewWaveIntegrationTestBase : IntegrationTestBase, INewWaveTests
{
    internal XRunner Runner { get; private set; }

    public NewWaveIntegrationTestBase(ITestOutputHelper output)
        : base(output)
    {
        Runner = new XRunner(this);
    }

    protected XExtension Ext(string ext) 
        => new XExtension(ext);
}
