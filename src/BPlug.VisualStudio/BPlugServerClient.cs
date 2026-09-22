using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BPlug.VisualStudio
{
    internal sealed class BPlugFinding
    {
        public BPlugFinding(string id, string filePath, string message)
        {
            Id = id;
            FilePath = filePath;
            Message = message;
        }

        public string Id { get; }
        public string FilePath { get; }
        public string Message { get; }
    }

    internal sealed class BPlugServerResult
    {
        public BPlugServerResult(IReadOnlyList<BPlugFinding> findings, string log)
        {
            Findings = findings;
            Log = log;
        }

        public IReadOnlyList<BPlugFinding> Findings { get; }
        public string Log { get; }
    }

    internal static class BPlugServerClient
    {
        internal static string LocateServerExe()
        {
            var vsixDir = Path.GetDirectoryName(typeof(BPlugPackage).Assembly.Location);
            if (string.IsNullOrEmpty(vsixDir))
            {
                return string.Empty;
            }

            return Path.Combine(vsixDir, "Server", "BPlug.Server.exe");
        }

        internal static async Task<BPlugServerResult> AnalyzeProjectsAsync(
            string solutionLabel,
            IReadOnlyList<string> projectPaths,
            CancellationToken cancellationToken)
        {
            var serverExe = LocateServerExe();
            if (!File.Exists(serverExe))
            {
                throw new FileNotFoundException(
                    "BPlug.Server.exe was not found next to the VSIX. Build BPlug.Server before debugging the extension.",
                    serverExe);
            }

            var arguments = new StringBuilder("--analyze-projects");
            if (!string.IsNullOrWhiteSpace(solutionLabel))
            {
                arguments.Append(' ').Append(Quote(solutionLabel));
            }

            foreach (var path in projectPaths)
            {
                arguments.Append(' ').Append(Quote(path));
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = serverExe,
                Arguments = arguments.ToString(),
                WorkingDirectory = Path.GetDirectoryName(serverExe) ?? AppDomain.CurrentDomain.BaseDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            var log = new StringBuilder();
            var findings = new List<BPlugFinding>();

            using (var process = new Process { StartInfo = startInfo })
            {
                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data == null)
                    {
                        return;
                    }

                    lock (log)
                    {
                        log.AppendLine(e.Data);
                    }

                    if (TryParseFinding(e.Data, out var finding) && finding != null)
                    {
                        lock (findings)
                        {
                            findings.Add(finding);
                        }
                    }
                };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data == null)
                    {
                        return;
                    }

                    lock (log)
                    {
                        log.AppendLine(e.Data);
                    }
                };

                if (!process.Start())
                {
                    throw new InvalidOperationException("BPlug.Server failed to start.");
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                while (!process.HasExited)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                }
            }

            return new BPlugServerResult(findings, log.ToString());
        }

        private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

        private static bool TryParseFinding(string line, out BPlugFinding? finding)
        {
            finding = null!;
            const string prefix = "FINDING\t";
            if (line == null || !line.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }

            var parts = line.Substring(prefix.Length).Split(new[] { '\t' }, 3);
            if (parts.Length < 3)
            {
                return false;
            }

            finding = new BPlugFinding(parts[0], parts[1], parts[2]);
            return true;
        }
    }
}
