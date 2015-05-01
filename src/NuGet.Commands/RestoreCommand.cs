using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.Framework.Logging;
using NuGet.ContentModel;
using NuGet.DependencyResolver;
using NuGet.Frameworks;
using NuGet.LibraryModel;
using NuGet.ProjectModel;
using NuGet.RuntimeModel;
using NuGet.Versioning;
using ILogger = Microsoft.Framework.Logging.ILogger;

namespace NuGet.Commands
{
    public class RestoreCommand
    {
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger _log;

        public RestoreCommand(ILoggerFactory loggerFactory)
        {
            _loggerFactory = loggerFactory;
            _log = loggerFactory.CreateLogger<RestoreCommand>();
        }

        public async Task<RestoreResult> ExecuteAsync(RestoreRequest request)
        {
            if (request.Project.TargetFrameworks.Count == 0)
            {
                _log.LogError("The project does not specify any target frameworks!");
                return new RestoreResult(success: false, restoreGraphs: Enumerable.Empty<RestoreTargetGraph>());
            }

            _log.LogInformation($"Restoring packages for '{request.Project.FilePath}'");

            _log.LogWarning("TODO: Read and use lock file");

            // Load repositories
            var context = request.CreateWalkContext(_loggerFactory);

            var remoteWalker = new RemoteDependencyWalker(context);

            var projectRange = new LibraryRange()
            {
                Name = request.Project.Name,
                VersionRange = new VersionRange(request.Project.Version),
                TypeConstraint = LibraryTypes.Project
            };

            // Resolve dependency graphs
            var frameworks = request.Project.TargetFrameworks.Select(f => f.FrameworkName).ToList();
            var graphs = new List<RestoreTargetGraph>();
            foreach (var framework in frameworks)
            {
                graphs.Add(await WalkDependencies(projectRange, framework, remoteWalker, context));
            }

            if (graphs.Any(g => g.InConflict))
            {
                _log.LogError("Failed to resolve conflicts");
                return new RestoreResult(success: false, restoreGraphs: graphs);
            }

            // Install the runtime-agnostic packages
            var allInstalledPackages = new HashSet<LibraryIdentity>();
            await InstallPackages(graphs, request.PackagesDirectory, allInstalledPackages, request.MaxDegreeOfConcurrency);

            // Resolve runtime dependencies
            var runtimeGraphs = new List<RestoreTargetGraph>();
            if (request.Project.RuntimeGraph.Runtimes.Count > 0)
            {
                foreach (var graph in graphs)
                {
                    var runtimeSpecificGraphs = await WalkRuntimeDependencies(projectRange, graph, request.Project.RuntimeGraph, remoteWalker, context, request.PackagesDirectory);
                    runtimeGraphs.AddRange(runtimeSpecificGraphs);
                }

                graphs.AddRange(runtimeGraphs);

                if (runtimeGraphs.Any(g => g.InConflict))
                {
                    _log.LogError("Failed to resolve conflicts");
                    return new RestoreResult(success: false, restoreGraphs: graphs);
                }

                // Install runtime-specific packages
                await InstallPackages(runtimeGraphs, request.PackagesDirectory, allInstalledPackages, request.MaxDegreeOfConcurrency);
            }
            else
            {
                _log.LogVerbose("Skipping runtime dependency walk, no runtimes defined in project.json");
            }

            // Build the lock file
            var lockFile = CreateLockFile(request.Project, graphs, request.PackagesDirectory);
            var lockFileFormat = new LockFileFormat();

            if (!string.IsNullOrEmpty(request.LockFilePath))
            {
                lockFileFormat.Write(request.LockFilePath, lockFile);
            }

            return new RestoreResult(true, graphs, lockFile);
        }


        private LockFile CreateLockFile(PackageSpec project, List<RestoreTargetGraph> targetGraphs, PackagesDirectory packagesDirectory)
        {
            var lockFile = new LockFile();

            using (var sha512 = SHA512.Create())
            {
                foreach (var item in targetGraphs.SelectMany(g => g.Flattened).Distinct().OrderBy(x => x.Data.Match.Library))
                {
                    var library = item.Data.Match.Library;
                    var packageContent = packagesDirectory.ReadPackage(library.Name, library.Version, sha512);

                    if (packageContent == null)
                    {
                        continue;
                    }

                    var lockFileLib = CreateLockFileLibrary(
                        packageContent,
                        library.Name,
                        library.Version);

                    lockFile.Libraries.Add(lockFileLib);
                }

                // Use empty string as the key of dependencies shared by all frameworks
                lockFile.ProjectFileDependencyGroups.Add(new ProjectFileDependencyGroup(
                    string.Empty,
                    project.Dependencies.Select(x => x.LibraryRange.ToString())));

                foreach (var frameworkInfo in project.TargetFrameworks)
                {
                    lockFile.ProjectFileDependencyGroups.Add(new ProjectFileDependencyGroup(
                        frameworkInfo.FrameworkName.ToString(),
                        frameworkInfo.Dependencies.Select(x => x.LibraryRange.ToString())));
                }

                // Add the targets
                foreach (var targetGraph in targetGraphs)
                {
                    var target = new LockFileTarget();
                    target.TargetFramework = targetGraph.Framework;
                    target.RuntimeIdentifier = targetGraph.RuntimeIdentifier;

                    foreach (var library in targetGraph.Flattened.Select(g => g.Key).OrderBy(x => x))
                    {
                        var packageContent = packagesDirectory.ReadPackage(library.Name, library.Version, sha512);

                        if (packageContent == null)
                        {
                            continue;
                        }

                        var targetLibrary = CreateLockFileTargetLibrary(
                            packageContent,
                            targetGraph,
                            library.Name,
                            library.Version);

                        target.Libraries.Add(targetLibrary);
                    }

                    lockFile.Targets.Add(target);
                }
            }

            return lockFile;
        }

        private LockFileLibrary CreateLockFileLibrary(LocalPackageContent package, string id, NuGetVersion version)
        {
            var lockFileLib = new LockFileLibrary();

            // package.Id is read from nuspec and it might be in wrong casing.
            // correctedPackageName should be the package name used by dependency graph and
            // it has the correct casing that runtime needs during dependency resolution.
            lockFileLib.Name = id;
            lockFileLib.Version = version;

            lockFileLib.Sha512 = package.Sha512;

            // Get package files, excluding directory entries
            lockFileLib.Files = package.Files.Where(x => !x.EndsWith("/")).ToList();

            return lockFileLib;
        }

        private LockFileTargetLibrary CreateLockFileTargetLibrary(LocalPackageContent package, RestoreTargetGraph targetGraph, string id, NuGetVersion version)
        {
            var lockFileLib = new LockFileTargetLibrary();

            var framework = targetGraph.Framework;
            var runtimeIdentifier = targetGraph.RuntimeIdentifier;

            // package.Id is read from nuspec and it might be in wrong casing.
            // correctedPackageName should be the package name used by dependency graph and
            // it has the correct casing that runtime needs during dependency resolution.
            lockFileLib.Name = id;
            lockFileLib.Version = version;

            IList<string> files = package.Files.Select(p => p.Replace(Path.DirectorySeparatorChar, '/')).ToList();
            var contentItems = new ContentItemCollection();
            HashSet<string> referenceFilter = null;

            contentItems.Load(files);

            var dependencySet = package.Dependencies.GetNearest(framework);
            if (dependencySet != null)
            {
                var set = dependencySet.Packages;

                if (set != null)
                {
                    lockFileLib.Dependencies = set.ToList();
                }
            }

            var referenceSet = package.References.GetNearest(framework);
            if (referenceSet != null)
            {
                referenceFilter = new HashSet<string>(referenceSet.Items, StringComparer.OrdinalIgnoreCase);
            }

            // TODO: Remove this when we do #596
            // ASP.NET Core isn't compatible with generic PCL profiles
            if (!string.Equals(framework.Framework, FrameworkConstants.FrameworkIdentifiers.AspNetCore, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(framework.Framework, FrameworkConstants.FrameworkIdentifiers.DnxCore, StringComparison.OrdinalIgnoreCase))
            {
                var frameworkAssemblies = package.FrameworkAssemblies.GetNearest(framework);
                if (frameworkAssemblies != null)
                {
                    foreach (var assemblyReference in frameworkAssemblies.Items)
                    {
                        lockFileLib.FrameworkAssemblies.Add(assemblyReference);
                    }
                }
            }

            var nativeCriteria = targetGraph.Conventions.Criteria.ForRuntime(targetGraph.RuntimeIdentifier);
            var managedCriteria = targetGraph.Conventions.Criteria.ForFrameworkAndRuntime(framework, targetGraph.RuntimeIdentifier);

            var compileGroup = contentItems.FindBestItemGroup(managedCriteria, targetGraph.Conventions.Patterns.CompileAssemblies, targetGraph.Conventions.Patterns.RuntimeAssemblies);

            if (compileGroup != null)
            {
                lockFileLib.CompileTimeAssemblies = compileGroup.Items.Select(t => t.Path).ToList();
            }

            var runtimeGroup = contentItems.FindBestItemGroup(managedCriteria, targetGraph.Conventions.Patterns.RuntimeAssemblies);
            if (runtimeGroup != null)
            {
                lockFileLib.RuntimeAssemblies = runtimeGroup.Items.Select(p => p.Path).ToList();
            }

            var nativeGroup = contentItems.FindBestItemGroup(nativeCriteria, targetGraph.Conventions.Patterns.NativeLibraries);
            if (nativeGroup != null)
            {
                lockFileLib.NativeLibraries = nativeGroup.Items.Select(p => p.Path).ToList();
            }

            // COMPAT: Support lib/contract so older packages can be consumed
            string contractPath = "lib/contract/" + id + ".dll";
            var hasContract = files.Any(path => path == contractPath);
            var hasLib = lockFileLib.RuntimeAssemblies.Any();

            if (hasContract && hasLib && !framework.IsDesktop())
            {
                lockFileLib.CompileTimeAssemblies.Clear();
                lockFileLib.CompileTimeAssemblies.Add(contractPath);
            }

            // Apply filters from the <references> node in the nuspec
            if (referenceFilter != null)
            {
                // Remove anything that starts with "lib/" and is NOT specified in the reference filter.
                // runtimes/* is unaffected (it doesn't start with lib/)
                lockFileLib.RuntimeAssemblies = lockFileLib.RuntimeAssemblies.Where(p => !p.StartsWith("lib/") || referenceFilter.Contains(p)).ToList();
                lockFileLib.CompileTimeAssemblies = lockFileLib.CompileTimeAssemblies.Where(p => !p.StartsWith("lib/") || referenceFilter.Contains(p)).ToList();
            }

            return lockFileLib;
        }

        private Task<RestoreTargetGraph> WalkDependencies(LibraryRange projectRange, NuGetFramework framework, RemoteDependencyWalker walker, RemoteWalkContext context)
        {
            return WalkDependencies(projectRange, framework, null, RuntimeGraph.Empty, walker, context);
        }

        private async Task<RestoreTargetGraph> WalkDependencies(LibraryRange projectRange, NuGetFramework framework, string runtimeIdentifier, RuntimeGraph runtimeGraph, RemoteDependencyWalker walker, RemoteWalkContext context)
        {
            _log.LogInformation($"Restoring packages for {framework}");
            var graph = await walker.Walk(
                projectRange,
                framework,
                runtimeIdentifier,
                runtimeGraph);

            // Resolve conflicts
            _log.LogVerbose($"Resolving Conflicts for {framework}");
            bool inConflict = !graph.TryResolveConflicts();

            // Flatten and create the RestoreTargetGraph to hold the packages
            return RestoreTargetGraph.Create(inConflict, framework, runtimeIdentifier, runtimeGraph, graph, context, _loggerFactory);
        }

        private async Task<List<RestoreTargetGraph>> WalkRuntimeDependencies(LibraryRange projectRange, RestoreTargetGraph graph, RuntimeGraph projectRuntimeGraph, RemoteDependencyWalker walker, RemoteWalkContext context, PackagesDirectory packagesDirectory)
        {
            // Load runtime specs
            _log.LogVerbose("Scanning packages for runtime.json files...");
            var runtimeGraph = projectRuntimeGraph;
            graph.Graph.ForEach(node =>
            {
                var match = node?.Item?.Data?.Match;
                if (match == null) { return; }

                // Locate the package in the local repository
                var nextGraph = packagesDirectory.LoadRuntimeGraph(match.Library.Name, match.Library.Version);
                if (nextGraph != null)
                {
                    _log.LogVerbose($"Merging in runtimes defined in {match.Library}");
                    runtimeGraph = RuntimeGraph.Merge(runtimeGraph, nextGraph);
                }
            });

            var resultGraphs = new List<RestoreTargetGraph>();
            foreach (var runtimeName in projectRuntimeGraph.Runtimes.Keys)
            {
                _log.LogInformation($"Restoring packages for {graph.Framework} on {runtimeName}");
                resultGraphs.Add(await WalkDependencies(projectRange, graph.Framework, runtimeName, runtimeGraph, walker, context));
            }
            return resultGraphs;
        }

        private async Task InstallPackages(IEnumerable<RestoreTargetGraph> graphs, PackagesDirectory packagesDirectory, HashSet<LibraryIdentity> allInstalledPackages, int maxDegreeOfConcurrency)
        {
            var packagesToInstall = graphs.SelectMany(g => g.Install.Where(match => allInstalledPackages.Add(match.Library)));
            if (maxDegreeOfConcurrency <= 1)
            {
                foreach (var match in packagesToInstall)
                {
                    _log.LogInformation($"Installing {match.Library.Name} {match.Library.Version}");
                    await packagesDirectory.InstallPackage(match);
                }
            }
            else
            {
                var bag = new ConcurrentBag<RemoteMatch>(packagesToInstall);
                var tasks = Enumerable.Range(0, maxDegreeOfConcurrency)
                    .Select(async _ =>
                    {
                        RemoteMatch match;
                        while (bag.TryTake(out match))
                        {
                            _log.LogInformation($"Installing {match.Library.Name} {match.Library.Version}");
                            await packagesDirectory.InstallPackage(match);
                        }
                    });
                await Task.WhenAll(tasks);
            }
        }

    }
}
