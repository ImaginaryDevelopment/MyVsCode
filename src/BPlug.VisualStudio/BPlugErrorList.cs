using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace BPlug.VisualStudio
{
    internal sealed class BPlugErrorList : IDisposable
    {
        private readonly ErrorListProvider _provider;

        internal BPlugErrorList(IServiceProvider serviceProvider)
        {
            _provider = new ErrorListProvider(serviceProvider)
            {
                ProviderName = "BPlug",
                ProviderGuid = new Guid("9d2f6a11-0c44-4a7e-b3d1-5e8f9c2a7014"),
            };
        }

        internal void Show(IReadOnlyList<BPlugFinding> findings)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _provider.Tasks.Clear();

            foreach (var finding in findings)
            {
                var task = new ErrorTask
                {
                    Category = TaskCategory.BuildCompile,
                    ErrorCategory = TaskErrorCategory.Warning,
                    Text = finding.Id + ": " + finding.Message,
                    Document = finding.FilePath,
                    Line = 0,
                    Column = 0,
                    CanDelete = false,
                };
                task.Navigate += (_, __) =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    _provider.Navigate(task, Guid.Parse(EnvDTE.Constants.vsViewKindTextView));
                };
                _provider.Tasks.Add(task);
            }

            if (findings.Count > 0)
            {
                _provider.Show();
            }
        }

        internal void Clear()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _provider.Tasks.Clear();
        }

        public void Dispose() => _provider.Dispose();
    }
}
