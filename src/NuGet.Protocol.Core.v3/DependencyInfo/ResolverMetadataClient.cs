using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace NuGet.Protocol.Core.v3.MetadataClient
{
    internal static class ResolverMetadataClient
    {
        /// <summary>
        /// Retrieve a registration blob
        /// </summary>
        /// <returns>Returns Null if the package does not exist</returns>
        public static async Task<IEnumerable<DependencyInfo>> GetRegistrationInfo(HttpClient httpClient, Uri registrationUri, VersionRange range, ConcurrentDictionary<Uri, JObject> sessionCache)
        {
            var results = new HashSet<DependencyInfo>();

            NuGetFrameworkFullComparer frameworkComparer = new NuGetFrameworkFullComparer();
            FrameworkReducer frameworkReducer = new FrameworkReducer();
            JObject index = await LoadResource(httpClient, registrationUri, sessionCache);

            if (index == null)
            {
                // The server returned a 404, the package does not exist
                return Enumerable.Empty<DependencyInfo>();
            }

            VersionRange preFilterRange = Utils.SetIncludePrerelease(range, true);

            IList<Task<JObject>> rangeTasks = new List<Task<JObject>>();

            foreach (JObject item in index["items"])
            {
                NuGetVersion lower = NuGetVersion.Parse(item["lower"].ToString());
                NuGetVersion upper = NuGetVersion.Parse(item["upper"].ToString());

                if (IsItemRangeRequired(preFilterRange, lower, upper))
                {
                    JToken items;
                    if (!item.TryGetValue("items", out items))
                    {
                        Uri rangeUri = item["@id"].ToObject<Uri>();

                        rangeTasks.Add(LoadResource(httpClient, rangeUri, sessionCache));
                    }
                    else
                    {
                        rangeTasks.Add(Task.FromResult(item));
                    }
                }
            }

            await Task.WhenAll(rangeTasks.ToArray());

            string id = string.Empty;

            foreach (JObject rangeObj in rangeTasks.Select((t) => t.Result))
            {
                if (rangeObj == null)
                {
                    throw new InvalidDataException(registrationUri.AbsoluteUri);
                }

                foreach (JObject packageObj in rangeObj["items"])
                {
                    JObject catalogEntry = (JObject)packageObj["catalogEntry"];

                    NuGetVersion packageVersion = NuGetVersion.Parse(catalogEntry["version"].ToString());

                    id = catalogEntry["id"].ToString();

                    int publishedDate = 0;
                    JToken publishedValue;

                    if (catalogEntry.TryGetValue("published", out publishedValue))
                    {
                        publishedDate = int.Parse(publishedValue.ToObject<DateTime>().ToString("yyyyMMdd"));
                    }

                    //publishedDate = 0 means the property doesn't exist in index.json
                    //publishedDate = 19000101 means the property exists but the package is unlisted
                    if (range.Satisfies(packageVersion) && (publishedDate != 19000101))
                    {
                        var identity = new PackageIdentity(id, packageVersion);
                        var dependencyGroups = new List<PackageDependencyGroup>();

                        JArray dependencyGroupsArray = (JArray)catalogEntry["dependencyGroups"];

                        if (dependencyGroupsArray != null)
                        {
                            foreach (JObject dependencyGroupObj in dependencyGroupsArray)
                            {
                                NuGetFramework currentFramework = GetFramework(dependencyGroupObj);

                                var groupDependencies = new List<PackageDependency>();

                                JToken dependenciesObj;

                                // Packages with no dependencies have 'dependencyGroups' but no 'dependencies'
                                if (dependencyGroupObj.TryGetValue("dependencies", out dependenciesObj))
                                {
                                    foreach (JObject dependencyObj in dependenciesObj)
                                    {
                                        var dependencyId = dependencyObj["id"].ToString();
                                        var dependencyRange = Utils.CreateVersionRange((string)dependencyObj["range"], range.IncludePrerelease);

                                        groupDependencies.Add(new PackageDependency(dependencyId, dependencyRange));
                                    }
                                }

                                dependencyGroups.Add(new PackageDependencyGroup(currentFramework, groupDependencies));
                            }
                        }

                        DependencyInfo dependencyInfo = new DependencyInfo(identity, dependencyGroups);

                        results.Add(dependencyInfo);
                    }
                }
            }

            return results;
        }

        /// <summary>
        /// Retrieve the target framework from a dependency group obj
        /// </summary>
        private static NuGetFramework GetFramework(JObject dependencyGroupObj)
        {
            NuGetFramework framework = NuGetFramework.AnyFramework;

            if (dependencyGroupObj["targetFramework"] != null)
            {
                framework = NuGetFramework.Parse(dependencyGroupObj["targetFramework"].ToString());
            }

            return framework;
        }

        private static bool IsItemRangeRequired(VersionRange dependencyRange, NuGetVersion catalogItemLower, NuGetVersion catalogItemUpper)
        {
            VersionRange catalogItemVersionRange = new VersionRange(minVersion: catalogItemLower, includeMinVersion: true,
                maxVersion: catalogItemUpper, includeMaxVersion: true, includePrerelease: true);

            if (dependencyRange.HasLowerAndUpperBounds) // Mainly to cover the '!dependencyRange.IsMaxInclusive && !dependencyRange.IsMinInclusive' case
            {
                return catalogItemVersionRange.Satisfies(dependencyRange.MinVersion) || catalogItemVersionRange.Satisfies(dependencyRange.MaxVersion);
            }
            else
            {
                return dependencyRange.Satisfies(catalogItemLower) || dependencyRange.Satisfies(catalogItemUpper);
            }
        }

        private static async Task<JObject> LoadResource(HttpClient httpClient, Uri uri, ConcurrentDictionary<Uri, JObject> sessionCache)
        {
            JObject obj;
            if (sessionCache != null && sessionCache.TryGetValue(uri, out obj))
            {
                return obj;
            }

            HttpResponseMessage response = await httpClient.GetAsync(uri);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();

            string json = await response.Content.ReadAsStringAsync();
            obj = JObject.Parse(json);

            if (sessionCache != null)
            {
                sessionCache.TryAdd(uri, obj);
            }

            return obj;
        }
    }
}
