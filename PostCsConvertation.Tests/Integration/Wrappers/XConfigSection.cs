using LogRotate.Consts;
using PostCsConvertation.Tests.Integration.Base;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

namespace PostCsConvertation.Tests.Integration.Wrappers
{
    internal class XConfigSection
    {
        private readonly List<XPattern> _filePatterns = new List<XPattern>();
        private readonly List<string> _directives = new List<string>();
        private readonly string _testDir;

        public XConfigSection(IEnumerable<XPattern> filePatterns, string testDir)
        {
            _filePatterns = filePatterns.ToList();
            _testDir = testDir;
        }

        public XConfigSection(IEnumerable<string> filePatterns, string testDir)
            : this(filePatterns
                .Select(fp => new XPattern(testDir, fp))
                .ToList(), testDir)
        {
        }

        public XConfigSection With(string key)
        {
            _directives.Add(key);
            return this;
        }

        public XConfigSection With(string key, string value)
        {
            _directives.Add($"{key} {value}");
            return this;
        }

        public XConfigSection With(string key, int value)
        {
            return With(key, value.ToString());
        }

        public XConfigSection WithQuoted(string key, string value)
        {
            _directives.Add($"{key} \"{value}\"");
            return this;
        }

        public XConfigSection WithScript(string key, string value)
        {
            _directives.Add($"{key}\r\n\t\t{value}\r\n\tendscript");
            return this;
        }

        public XConfigSection WithEcho(string key, string filepath, string content = "")
        {
            if (string.IsNullOrEmpty(content))
                content = $"\t\techo content of {filepath} > {filepath}";
            _directives.Add($"{key}\r\n{content}\r\n\tendscript");
            return this;
        }

        public override string ToString() 
            => ToString(Environment.NewLine);

        public string ToString(string lineSeparator)
        {
            var sb = new StringBuilder();

            if (!_filePatterns.Any())  //global section
            { 
                foreach (var directive in _directives)
                    sb.Append(directive + lineSeparator);
                return sb.ToString();
            }

            sb.Append(string.Join(" ", _filePatterns.Select(fp =>
            {
                return fp.ToString()
                    .Any(ch => char.IsWhiteSpace(ch))
                        ? TestHelpersNewWave.Quote(fp)
                        : fp;
            })) + " {" + lineSeparator);
            foreach (var str in _directives)
                sb.Append($"\t{str}{lineSeparator}");
            sb.Append("}");

            return sb.ToString();
        }
    }
}
