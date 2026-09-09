using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace PostCsConvertation.Tests.Integration.Wrappers;

internal abstract class XBaseFile
{
    public XBaseFile(string testDir)
    {
        _testDir = testDir;
    }

    protected readonly string _testDir;
    protected string _content = string.Empty;

    public abstract string Type { get; }

    public string Filename { get; protected set; }

    public string Filepath
        => Path.Combine(_testDir, Filename);


    public XBaseFile Create()
    {
        File.WriteAllText(Filepath, string.IsNullOrEmpty(_content) 
            ? $"content of {Filename}" 
            : _content);

        if (CreationTime != null)
            File.SetCreationTime(Filepath, CreationTime.Value);

        if (LastWriteTime != null)
            File.SetLastWriteTime(Filepath, LastWriteTime.Value);

        return this;
    }

    public bool Exists()
        => File.Exists(Filepath);

    public bool Exists(string append)
        => File.Exists($"{Filepath}{append}");


    public XBaseFile WithContent(string content)
    {
        _content = content;
        return this;
    }

    public DateTime? LastWriteTime { get; private set; }
    public XBaseFile WithLastWriteTime(DateTime dateTime)
    {
        LastWriteTime = dateTime;
        return this;
    }

    public DateTime? CreationTime { get; private set; }
    public XBaseFile WithCreationTime(DateTime dateTime)
    {
        CreationTime = dateTime;
        return this;
    }

    public byte[] UnGZip()
    {
        using var inFile = File.OpenRead(Filepath);
        using var gzStream = new GZipStream(inFile, CompressionMode.Decompress);
        using var extracted = new MemoryStream();
        gzStream.CopyTo(extracted);

        return extracted.ToArray();
    }

    #region ShouldBe ========================================================================
    public readonly List<KeyValuePair<string, string>> ShouldBeList = new();
 

    public XBaseFile ShouldBe(XExtension[] postfixes, string message)
    {
        if (null == postfixes || postfixes.Length == 0)
        {
            ShouldBeList.Add(new KeyValuePair<string, string>(string.Empty, message));
            return this;
        }

        ShouldBeList.AddRange(postfixes.Select(p => new KeyValuePair<string, string>(p.Ext, message)));
        return this;
    }

    public XBaseFile ShouldBe(XExtension postfix, string message) 
        => ShouldBe([postfix], message);

    public XBaseFile ShouldBe(params XExtension[] postfixes) 
        => ShouldBe(postfixes, string.Empty);

    public XBaseFile ShouldBe(string message)
        => ShouldBe([], message);
    

    public readonly List<KeyValuePair<string, string>> ShouldNotBeList = new();
    public XBaseFile ShouldNotBe(XExtension[] postfixes, string message)
    {
        if (null == postfixes || postfixes.Length == 0)
        {
            ShouldNotBeList.Add(new KeyValuePair<string, string>(string.Empty, message));
            return this;
        }

        ShouldNotBeList.AddRange(postfixes.Select(p => new KeyValuePair<string, string>(p.Ext, message)));
        return this;
    }

    public XBaseFile ShouldNotBe(XExtension postfix, string message)
        => ShouldNotBe([postfix], message);

    public XBaseFile ShouldNotBe(params XExtension[] postfixes)
        => ShouldNotBe(postfixes, string.Empty);

    public XBaseFile ShouldNotBe(string message)
        => ShouldNotBe([], message);

    #endregion ShouldBe =====================================================================

    #region ShouldContain ===================================================================
    public List<string> ShouldNotContainList = new();
    public XBaseFile ShouldNotContain(params string[] values)
    {
        ShouldNotContainList.AddRange(values);
        return this;
    }

    public List<string> ShouldContainList = new();
    public XBaseFile ShouldContain(params string[] values)
    {
        ShouldContainList.AddRange(values);
        return this;
    }

    #endregion ShouldContain ================================================================




    public static implicit operator string(XBaseFile stateFile)
        => stateFile.ToString();

    public override string ToString()
        => Filepath;
}
