using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PostCsConvertation.Tests.Integration.Wrappers;

internal class XConfig
{
    private readonly string _testDir;
    private readonly string _filename;
    private readonly List<XConfigSection> _sections = new List<XConfigSection>();

    public XConfig(string testDir, string filename)
    {
        _testDir = testDir;
        _filename = string.IsNullOrEmpty(filename) ? $"config-{Guid.NewGuid()}.conf" : filename;
    }
   
    public string Filepath
        => Path.Combine(_testDir, _filename);

    public XConfig WithSection(IEnumerable<string> patterns, Action<XConfigSection> init)
    {
        if (!patterns.Any())
            throw new ArgumentException($"patterns must not be empty in {nameof(XConfig)}.{nameof(WithSection)}");

        var section = new XConfigSection(patterns, _testDir);
        if (init != null)
            init(section);
        _sections.Add(section);
        return this;
    }

    public XConfig WithSection(string pattern, Action<XConfigSection> init) 
        => WithSection([pattern], init);

    public XConfig WithGlobalSection(Action<XConfigSection> init)
    {
        var section = new XConfigSection(Enumerable.Empty<XPattern>(), _testDir);
        if (init != null)
            init(section);
        _sections.Add(section);
        return this;
    }



    public XConfig Create(string? lineSeparator = null)
    {
        if (null == lineSeparator)
            lineSeparator = Environment.NewLine;
        var content = string.Join(lineSeparator, _sections.Select(s => s.ToString(lineSeparator)));
        File.WriteAllText(Filepath, content);
        return this;
    }

    public static implicit operator string(XConfig config) 
        => config.ToString();

    public override string ToString()
        => Filepath;
}
