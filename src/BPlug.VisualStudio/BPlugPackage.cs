using System;
using System.ComponentModel.Design;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace BPlug.VisualStudio
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("BPlug", "Analyzes loaded .NET projects for TFM, Nullable, and ImplicitUsings disagreements.", "0.1")]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideAutoLoad(UIContextGuids80.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)]
    [Guid(BPlugPackage.PackageGuidString)]
    public sealed class BPlugPackage : AsyncPackage, IVsSolutionEvents
    {
        public const string PackageGuidString = "8f3e2c1a-9b47-4d6e-a1c0-7e5f4b2d9a11";
        public static readonly Guid CommandSet = new Guid("3c7a91e2-4b18-4f6d-9e20-a8c4d15e7b02");
        public const int AnalyzeLoadedProjectsCommandId = 0x0100;
        private static readonly Guid OutputPaneGuid = new Guid("c4e0d4a8-2f71-4b6a-9d3e-8a1b5c7e9012");

        private BPlugErrorList? _errorList;
        private IVsSolution? _solution;
        private uint _solutionCookie;
        private OleMenuCommand? _analyzeCommand;

        protected override async Task InitializeAsync(
            CancellationToken cancellationToken,
            IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            _errorList = new BPlugErrorList(this);

            var commandService = await GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commandService != null)
            {
                var commandId = new CommandID(CommandSet, AnalyzeLoadedProjectsCommandId);
                _analyzeCommand = new OleMenuCommand(OnAnalyzeClicked, commandId);
                _analyzeCommand.BeforeQueryStatus += OnBeforeQueryStatus;
                commandService.AddCommand(_analyzeCommand);
            }

            _solution = await GetServiceAsync(typeof(SVsSolution)) as IVsSolution;
            if (_solution != null)
            {
                ErrorHandler.ThrowOnFailure(_solution.AdviseSolutionEvents(this, out _solutionCookie));
            }
        }

        protected override void Dispose(bool disposing)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (disposing)
            {
                if (_solution != null && _solutionCookie != 0)
                {
                    _solution.UnadviseSolutionEvents(_solutionCookie);
                    _solutionCookie = 0;
                }

                _errorList?.Dispose();
            }

            base.Dispose(disposing);
        }

        private void OnBeforeQueryStatus(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var command = (OleMenuCommand)sender;
            var dte = GetDte();
            var enabled = dte?.Solution?.IsOpen == true;
            command.Enabled = enabled;
            command.Visible = true;
        }

        private void OnAnalyzeClicked(object sender, EventArgs e)
        {
            _ = JoinableTaskFactory.RunAsync(AnalyzeLoadedProjectsAsync);
        }

        private async Task AnalyzeLoadedProjectsAsync()
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var dte = GetDte();
            if (dte?.Solution?.IsOpen != true)
            {
                await WriteOutputAsync("Open a solution first.");
                return;
            }

            var solutionLabel = dte.Solution.FullName ?? string.Empty;
            var projects = LoadedProjects.GetMsBuildProjectPaths(dte);
            await WriteOutputAsync("Analyzing " + projects.Count + " loaded project(s).");

            if (projects.Count == 0)
            {
                _errorList?.Clear();
                await WriteOutputAsync("No .csproj / .fsproj / .vbproj files are loaded.");
                return;
            }

            BPlugServerResult? result;
            try
            {
                result = await BPlugServerClient.AnalyzeProjectsAsync(solutionLabel, projects, DisposalToken)
                    .ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                await WriteOutputAsync(ex.Message);
                _errorList?.Show(
                    new[]
                    {
                        new BPlugFinding("BP0000", solutionLabel, ex.Message),
                    });
                return;
            }

            await JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!string.IsNullOrWhiteSpace(result.Log))
            {
                await WriteOutputAsync(result.Log.TrimEnd());
            }

            _errorList?.Show(result.Findings);
            await WriteOutputAsync(
                result.Findings.Count == 0
                    ? "No BPlug findings."
                    : result.Findings.Count + " BPlug finding(s) sent to the Error List.");
        }

        private DTE2? GetDte()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return GetService(typeof(DTE)) as DTE2;
        }

        private async Task WriteOutputAsync(string message)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync();
            var output = await GetServiceAsync(typeof(SVsOutputWindow)) as IVsOutputWindow;
            if (output == null)
            {
                return;
            }

            var paneGuid = OutputPaneGuid;
            output.CreatePane(ref paneGuid, "BPlug", 1, 1);
            output.GetPane(ref paneGuid, out var pane);
            pane?.OutputStringThreadSafe(message + Environment.NewLine);
            pane?.Activate();
        }

        public int OnAfterOpenProject(IVsHierarchy pHierarchy, int fAdded) => VSConstants.S_OK;

        public int OnQueryCloseProject(IVsHierarchy pHierarchy, int fRemoving, ref int pfCancel) => VSConstants.S_OK;

        public int OnBeforeCloseProject(IVsHierarchy pHierarchy, int fRemoved) => VSConstants.S_OK;

        public int OnAfterLoadProject(IVsHierarchy pStubHierarchy, IVsHierarchy pRealHierarchy) => VSConstants.S_OK;

        public int OnQueryUnloadProject(IVsHierarchy pRealHierarchy, ref int pfCancel) => VSConstants.S_OK;

        public int OnBeforeUnloadProject(IVsHierarchy pRealHierarchy, IVsHierarchy pStubHierarchy) => VSConstants.S_OK;

        public int OnAfterOpenSolution(object pUnkReserved, int fNewSolution)
        {
            _ = JoinableTaskFactory.RunAsync(AnalyzeLoadedProjectsAsync);
            return VSConstants.S_OK;
        }

        public int OnQueryCloseSolution(object pUnkReserved, ref int pfCancel) => VSConstants.S_OK;

        public int OnBeforeCloseSolution(object pUnkReserved) => VSConstants.S_OK;

        public int OnAfterCloseSolution(object pUnkReserved)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _errorList?.Clear();
            return VSConstants.S_OK;
        }
    }
}
