using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace BPlug.VisualStudio
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("BPlug", "Starts BPlug.Server and shows findings in Visual Studio.", "0.1")]
    [Guid(BPlugPackage.PackageGuidString)]
    public sealed class BPlugPackage : AsyncPackage
    {
        public const string PackageGuidString = "8f3e2c1a-9b47-4d6e-a1c0-7e5f4b2d9a11";

        protected override async Task InitializeAsync(
            CancellationToken cancellationToken,
            IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        }
    }
}
