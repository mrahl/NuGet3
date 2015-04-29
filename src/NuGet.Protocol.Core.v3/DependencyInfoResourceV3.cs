using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NuGet.Configuration;
using NuGet.Packaging.Core;
using NuGet.Protocol.Core.Types;
using NuGet.Protocol.Core.v3.MetadataClient;
using NuGet.Versioning;

namespace NuGet.Protocol.Core.v3
{
    /// <summary>
    /// Retrieves all packages and dependencies from a V3 source.
    /// </summary>
    public sealed class DependencyInfoResourceV3 : DependencyInfoResource
    {
        private readonly HttpClient _client;
        private readonly RegistrationResourceV3 _regResource;
        private readonly PackageSource _source;

        /// <summary>
        /// Dependency info resource
        /// </summary>
        /// <param name="client">Http client</param>
        /// <param name="regResource">Registration blob resource</param>
        public DependencyInfoResourceV3(HttpClient client, RegistrationResourceV3 regResource, PackageSource source)
        {
            if (client == null)
            {
                throw new ArgumentNullException(nameof(client));
            }

            if (regResource == null)
            {
                throw new ArgumentNullException(nameof(regResource));
            }

            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            _client = client;
            _regResource = regResource;
            _source = source;
        }

        /// <summary>
        /// Retrieve dependency info for a single package.
        /// </summary>
        /// <param name="package">package id and version</param>
        /// <param name="token">cancellation token</param>
        /// <returns>Returns dependency info for the given package if it exists. If the package is not found null is returned.</returns>
        public override async Task<DependencyInfo> ResolvePackage(PackageIdentity package, CancellationToken token)
        {
            try
            {
                // Construct the registration index url
                Uri uri = _regResource.GetUri(package.Id);

                var cache = new ConcurrentDictionary<Uri, JObject>();

                // Retrieve the registration blob
                var singleVersion = new VersionRange(minVersion: package.Version, includeMinVersion: true, maxVersion: package.Version, includeMaxVersion: true);
                var packages = await ResolverMetadataClient.GetRegistrationInfo(_client, uri, singleVersion, cache);

                // regInfo is empty if the server returns a 404 for the package to indicate that it does not exist
                return packages.FirstOrDefault();
            }
            catch (Exception ex)
            {
                // Wrap exceptions coming from the server with a user friendly message
                string error = String.Format(CultureInfo.CurrentUICulture, Strings.Protocol_PackageMetadataError, package, _source.Source);

                throw new NuGetProtocolException(error, ex);
            }
        }

        /// <summary>
        /// Retrieve the available packages and their dependencies.
        /// </summary>
        /// <remarks>Includes prerelease packages</remarks>
        /// <param name="packageId">package Id to search</param>
        /// <param name="projectFramework">project target framework. This is used for finding the dependency group</param>
        /// <param name="token">cancellation token</param>
        /// <returns>available packages and their dependencies</returns>
        public override async Task<IEnumerable<DependencyInfo>> ResolvePackages(string packageId, CancellationToken token)
        {
            try
            {
                // Construct the registration index url
                Uri uri = _regResource.GetUri(packageId);

                var cache = new ConcurrentDictionary<Uri, JObject>();

                // Retrieve the registration blob
                return await ResolverMetadataClient.GetRegistrationInfo(_client, uri, VersionRange.All, cache);
            }
            catch (Exception ex)
            {
                // Wrap exceptions coming from the server with a user friendly message
                string error = String.Format(CultureInfo.CurrentUICulture, Strings.Protocol_PackageMetadataError, packageId, _source.Source);

                throw new NuGetProtocolException(error, ex);
            }
        }
    }
}
