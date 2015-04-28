using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Frameworks;
using NuGet.Packaging.Core;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using V3PackageDependency = NuGet.Packaging.Core.PackageDependency;

namespace NuGet.Protocol.Core.v2
{
    /// <summary>
    /// A V2 dependency info gatherer.
    /// </summary>
    public class DependencyInfoResourceV2 : DependencyInfoResource
    {
        private readonly IPackageRepository V2Client;
        private readonly FrameworkReducer _frameworkReducer = new FrameworkReducer();

        public DependencyInfoResourceV2(IPackageRepository repo)
        {
            V2Client = repo;
        }

        public DependencyInfoResourceV2(V2Resource resource)
            : this(resource.V2Client)
        {

        }

        /// <summary>
        /// Retrieve dependency info for a single package.
        /// </summary>
        /// <param name="package">package id and version</param>
        /// <param name="projectFramework">project target framework. This is used for finding the dependency group</param>
        /// <param name="token">cancellation token</param>
        /// <returns>Returns dependency info for the given package if it exists. If the package is not found null is returned.</returns>
        public override async Task<PackageDependencyInfo> ResolvePackage(PackageIdentity package, NuGetFramework projectFramework, CancellationToken token)
        {
            if (package == null)
            {
                throw new ArgumentNullException(null, nameof(package));
            }

            if (projectFramework == null)
            {
                throw new ArgumentNullException(nameof(projectFramework));
            }

            PackageDependencyInfo result = null;

            SemanticVersion legacyVersion = null;

            // attempt to parse the semver into semver 1.0.0, if this fails then the v2 client would
            // not be able to find it anyways and we should return null
            if (SemanticVersion.TryParse(package.Version.ToString(), out legacyVersion))
            {
                try
                {
                    // Retrieve all packages
                    var repoPackage = V2Client.FindPackage(package.Id, legacyVersion);

                    if (repoPackage != null)
                    {
                        // convert to v3 type
                        result = CreateDependencyInfo(repoPackage, projectFramework);
                    }
                }
                catch (Exception ex)
                {
                    // Wrap exceptions coming from the server with a user friendly message
                    string error = String.Format(CultureInfo.CurrentUICulture, Strings.Protocol_PackageMetadataError, package, V2Client.Source);

                    throw new NuGetProtocolException(error, ex);
                }
            }

            return result;
        }

        /// <summary>
        /// Retrieve dependency info for a single package.
        /// </summary>
        /// <param name="package">package id and version</param>
        /// <param name="projectFramework">project target framework. This is used for finding the dependency group</param>
        /// <param name="token">cancellation token</param>
        /// <returns>Returns dependency info for the given package if it exists. If the package is not found null is returned.</returns>
        public override async Task<IEnumerable<PackageDependencyInfo>> ResolvePackages(string packageId, NuGetFramework projectFramework, CancellationToken token)
        {
            if (packageId == null)
            {
                throw new ArgumentNullException(nameof(packageId));
            }

            if (projectFramework == null)
            {
                throw new ArgumentNullException(nameof(projectFramework));
            }

            try
            {
                // Retrieve all packages
                var repoPackages = V2Client.FindPackagesById(packageId);

                // Convert from v2 to v3 types and enumerate the list to finish all server requests before returning
                return repoPackages.Select(p => CreateDependencyInfo(p, projectFramework)).ToArray();
            }
            catch (Exception ex)
            {
                // Wrap exceptions coming from the server with a user friendly message
                string error = String.Format(CultureInfo.CurrentUICulture, Strings.Protocol_PackageMetadataError, packageId, V2Client.Source);

                throw new NuGetProtocolException(error, ex);
            }
        }

        /// <summary>
        ///  Convert a V2 IPackage into a V3 PackageDependencyInfo
        /// </summary>
        private PackageDependencyInfo CreateDependencyInfo(IPackage packageVersion, NuGetFramework projectFramework)
        {
            IEnumerable<V3PackageDependency> deps = Enumerable.Empty<NuGet.Packaging.Core.PackageDependency>();

            PackageIdentity identity = new PackageIdentity(packageVersion.Id, NuGetVersion.Parse(packageVersion.Version.ToString()));
            if (packageVersion.DependencySets != null && packageVersion.DependencySets.Count() > 0)
            {
                // Take only the dependency group valid for the project TFM
                NuGetFramework nearestFramework = _frameworkReducer.GetNearest(projectFramework, packageVersion.DependencySets.Select(e => GetFramework(e)));

                if (nearestFramework != null)
                {
                    var matches = packageVersion.DependencySets.Where(e => (GetFramework(e).Equals(nearestFramework)));
                    IEnumerable<PackageDependency> dependencies = matches.First().Dependencies;
                    deps = dependencies.Select(item => GetPackageDependency(item));
                }
            }

            return new PackageDependencyInfo(identity, deps);
        }

        private static NuGetFramework GetFramework(PackageDependencySet dependencySet)
        {
            NuGetFramework fxName = NuGetFramework.AnyFramework;
            if (dependencySet.TargetFramework != null)
            {
                fxName = NuGetFramework.Parse(dependencySet.TargetFramework.FullName);
            }

            return fxName;
        }

        private static V3PackageDependency GetPackageDependency(PackageDependency dependency)
        {
            string id = dependency.Id;
            VersionRange versionRange = dependency.VersionSpec == null ? null : VersionRange.Parse(dependency.VersionSpec.ToString());
            return new V3PackageDependency(id, versionRange);
        }
    }
}