using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;

namespace BPlug.VisualStudio
{
    internal static class LoadedProjects
    {
        internal static IReadOnlyList<string> GetMsBuildProjectPaths(DTE2 dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var paths = new List<string>();
            if (dte?.Solution?.Projects == null)
            {
                return paths;
            }

            foreach (Project project in dte.Solution.Projects)
            {
                Collect(project, paths);
            }

            var distinct = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
            {
                if (seen.Add(path))
                {
                    distinct.Add(path);
                }
            }

            return distinct;
        }

        private static void Collect(Project project, List<string> paths)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (project == null)
            {
                return;
            }

            try
            {
                if (string.Equals(project.Kind, ProjectKinds.vsProjectKindSolutionFolder, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(project.Kind, "{66A26720-8FB5-11D2-AA7E-00C04F688DDE}", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(project.Kind, "{66A26722-8FB5-11D2-AA7E-00C04F688DDE}", StringComparison.OrdinalIgnoreCase))
                {
                    if (project.ProjectItems == null)
                    {
                        return;
                    }

                    foreach (ProjectItem item in project.ProjectItems)
                    {
                        if (item?.SubProject != null)
                        {
                            Collect(item.SubProject, paths);
                        }
                    }

                    return;
                }

                var fullName = project.FullName;
                if (string.IsNullOrWhiteSpace(fullName) || !File.Exists(fullName))
                {
                    return;
                }

                var extension = Path.GetExtension(fullName);
                if (extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".fsproj", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".vbproj", StringComparison.OrdinalIgnoreCase))
                {
                    paths.Add(Path.GetFullPath(fullName));
                }
            }
            catch (COMException)
            {
            }
            catch (NotImplementedException)
            {
            }
        }
    }
}
