using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Frameworks;
using NuGet.Packaging.Core;
using NuGet.Versioning;

namespace NuGet.Protocol.Core.Types
{
    /// <summary>
    /// Provides methods for resolving a package and its dependencies. This might change based on the new dependency resolver.
    /// </summary>
    public abstract class DependencyInfoResource : INuGetResource
    {
        /// <summary>
        /// Retrieve dependency info for a single package.
        /// </summary>
        /// <param name="package">package id and version</param>
        /// <param name="token">cancellation token</param>
        /// <returns>Returns dependency info for the given package if it exists. If the package is not found null is returned.</returns>
        public abstract Task<DependencyInfo> ResolvePackage(PackageIdentity package, CancellationToken token);

        /// <summary>
        /// Retrieve the available packages and their dependencies.
        /// </summary>
        /// <remarks>Includes prerelease packages</remarks>
        /// <param name="packageId">package Id to search</param>
        /// <param name="token">cancellation token</param>
        /// <returns>available packages and their dependencies</returns>
        public abstract Task<IEnumerable<DependencyInfo>> ResolvePackages(string packageId, CancellationToken token);
    }
}
