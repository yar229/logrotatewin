using FluentAssertions;
using LogRotate;
using PostCsConvertation.Tests.Integration.Base;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace PostCsConvertation.Tests.Integration.Wrappers;

internal class XRunner
{
    private readonly INewWaveTests _container;

    public XRunner(INewWaveTests container)
    {
        _container = container;
        State = new XStateFile(container.TestDir);
    }

    public List<XBaseFile> Files { get; internal set; } = new List<XBaseFile>();

    public XConfig Config { get; private set; }

    public XStateFile State { get; private set;}

    public bool DoForce { get; internal set; }

    internal XRunner WithForce(bool doForce = true)
    {
        DoForce = doForce;
        return this;
    }

    public XRunner Run()
    {
        _container.Output.WriteLine($"Config:\r\n {File.ReadAllText(Config.Filepath)}");
        _container.Output.WriteLine($"Files before run:\r\n {ListDirectory(_container.TestDir)}");

        ExitCode = _container.RunLogRotate(
            DoForce ? "-f" : string.Empty,
            "--verbose", "-s", State, Config);

        _container.Output.WriteLine($"Files after run:\r\n {ListDirectory(_container.TestDir)}");

        //_container.Output.WriteLine("Execution Log:");
        //_container.Output.WriteLine(_container.Log);
        return this;
    }
    public XRunner Check()
    {
        string reason;
        foreach (var file in Files)
        {
            foreach (var postfix in file.ShouldBeList)
            {
                reason = string.IsNullOrEmpty(postfix.Value)
                    ? string.Empty
                    : $"(reason: {postfix.Value})\r\n";
                file.Exists(postfix.Key).Should().BeTrue($"\r\n{file.Type} must exists: {file.Filename}{postfix.Key}\r\n{reason}");
            }
            foreach (var postfix in file.ShouldNotBeList)
            {
                reason = string.IsNullOrEmpty(postfix.Value)
                    ? string.Empty
                    : $"(reason: {postfix.Value})\r\n";
                file.Exists(postfix.Key).Should().BeFalse($"\r\n{file.Type} must NOT exists: {file.Filename}{postfix.Key}\r\n{reason}");
            }

            if (file.ShouldContainList.Any())
            {
                file.Exists().Should().BeTrue($"{file.Type} must exists: {file.Filename}");

                var content = File.ReadAllText(file.Filepath);
                foreach (var str in file.ShouldContainList)
                    content.Should().Contain(str, $"{file.Type} {file.Filename} must contain '{str}'");
                foreach (var str in file.ShouldNotContainList)
                    content.Should().NotContain(str, $"{file.Type} {file.Filename} must NOT contain '{str}'");
            }
        }

        return this;
    }

    public string Log => _container.Log;

    public int? ExitCode { get; private set; }

    public XRunner RunAndCheck()
    {
        Run();
        Check();
        return this;
    }

    public XRunner Should(Action<XRunner> action)
    {
        if (null != action)
            action(this);

        return this;
    }

    private static string ListDirectory(string path, int indentLevel = 0)
    {
        var sb = new StringBuilder();
        string indent = new string('\t', indentLevel);

        if (indentLevel > 0)
            sb.AppendLine($"{indent}{Path.GetFileName(path)}");

        foreach (var file in Directory.GetFiles(path))
            sb.AppendLine($"{indent}\t{Path.GetFileName(file)}");

        foreach (var dir in Directory.GetDirectories(path))
            sb.Append(ListDirectory(dir, indentLevel + 1));

        return sb.ToString();
    }

    public XRunner WithLogs(IEnumerable<XLogFile> logs, Action<XLogFile> init)
    {
        if (!logs.Any())
            throw new ArgumentException($"{nameof(logs)} must be not empty {nameof(XRunner)}.{nameof(WithLogs)}");

        foreach (var log in logs)
        {
            if (null != init)
                init(log);
            Files.Add(log);
        }
        return this;
    }

    public XRunner WithLogs(IEnumerable<string> lognames, Action<XLogFile> init) 
        => WithLogs(
            lognames.Select(l => new XLogFile(_container.TestDir, l)), 
            init);

    public XRunner WithLog(string logname, Action<XLogFile> init) 
        => WithLogs([logname], init);

    public XRunner WithLog(XLogFile log, Action<XLogFile> init)
        => WithLogs([log], init);

    public XRunner WithLog(Action<XLogFile> init)
        => WithLog(NewLog() , init);


    public XRunner WithState(string filename, Action<XStateFile> init)
    {
        var state = NewState(filename);
        if (null != init)
            init(state);
        State = state;

        return this;
    }

    public XRunner WithState(Action<XStateFile> init)
        => WithState(null, init);

    public XRunner WithFiles(IEnumerable<XFile> files, Action<XFile> init)
    {
        foreach (var file in files)
        {
            if (null != init)
                init(file);
            Files.Add(file);
        }
        return this;
    }

    public XRunner WithFiles(IEnumerable<string> filenames, Action<XFile> init)
        => WithFiles(
            filenames.Select(fn => NewFile(fn)),
            init);

    public XRunner WithFile(string filename, Action<XFile> init)
        => WithFiles([filename], init);

    public XRunner WithFile(XFile file, Action<XFile> init)
        => WithFiles([file], init);







    public XRunner WithConfig(string filename, Action<XConfig> init)
    {
        var config = new XConfig(_container.TestDir, filename);
        if (null != init)
            init(config);
        Config = config;
        return this;
    }

    public XRunner WithConfig(Action<XConfig> init)
    {
        var config = new XConfig(_container.TestDir, string.Empty);
        if (null != init)
            init(config);
        Config = config;
        return this;
    }

    public XLogFile NewLog(string logname) 
        => new XLogFile(_container.TestDir, logname);

    public XLogFile NewLog()
        => NewLog(string.Empty);

    public XFile NewFile(string filename)
        => new XFile(_container.TestDir, filename);

    public XPattern NewPattern(string pattern)
        => new XPattern(_container.TestDir, pattern);

    public XStateFile NewState(string? filename = null)
        => new XStateFile(_container.TestDir, filename);
}
