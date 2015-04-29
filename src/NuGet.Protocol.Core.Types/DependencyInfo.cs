using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using NuGet.Packaging;
using NuGet.Packaging.Core;

namespace NuGet.Protocol.Core.Types
{
    /// <summary>
    /// A collection of package dependency groups.
    /// </summary>
    public class DependencyInfo : IEquatable<DependencyInfo>
    {
        private readonly PackageIdentity _identity;
        private readonly IReadOnlyList<PackageDependencyGroup> _dependencyGroups;

        /// <summary>
        /// DependencyInfo
        /// </summary>
        /// <param name="identity">package identity</param>
        /// <param name="dependencyGroups">package dependency groups</param>
        public DependencyInfo(PackageIdentity identity, IEnumerable<PackageDependencyGroup> dependencyGroups)
        {
            if (identity == null)
            {
                throw new ArgumentNullException(nameof(identity));
            }

            if (dependencyGroups == null)
            {
                throw new ArgumentNullException(nameof(dependencyGroups));
            }

            _identity = identity;
            _dependencyGroups = dependencyGroups.ToList().AsReadOnly();
        }

        /// <summary>
        /// Package identity
        /// </summary>
        public PackageIdentity Identity
        {
            get
            {
                return _identity;
            }
        }

        /// <summary>
        /// Package dependency groups
        /// </summary>
        public IEnumerable<PackageDependencyGroup> DependencyGroups
        {
            get
            {
                return _dependencyGroups;
            }
        }

        public bool Equals(DependencyInfo other) => other != null && Identity.Equals(other.Identity)
            && new HashSet<PackageDependencyGroup>(DependencyGroups).SetEquals(other.DependencyGroups);

        public override bool Equals(object obj) => Equals(obj as DependencyInfo);

        public override int GetHashCode()
        {
            var combiner = HashCodeCombiner.Start();

            combiner.AddObject(Identity);

            foreach (int hash in DependencyGroups.Select(group => group.GetHashCode()).OrderBy(x => x))
            {
                combiner.AddInt32(hash);
            }

            return combiner.CombinedHash;
        }

        public override string ToString()
        {
            return String.Format(CultureInfo.InvariantCulture, "{0} : {1}", Identity, String.Join(" ,", DependencyGroups));
        }
    }
}
