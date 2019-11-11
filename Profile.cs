using System;
using System.Collections.Generic;
using System.Linq;

namespace XELive
{
    class Profile
    {
        public string Name { get; set; }
        public bool IsDefault { get; set; }
        public string InheritFrom { get; set; }
        public string ConnectionString { get; set; }

        public IEnumerable<string> IgnoreDatabases { get; set; } = Enumerable.Empty<string>();
        public IEnumerable<string> IgnoreUsers { get; set; } = Enumerable.Empty<string>();
        public IEnumerable<string> IgnoreStatements { get; set; } = Enumerable.Empty<string>();
        public IEnumerable<string> IgnorePrefixes { get; set; } = Enumerable.Empty<string>();

        public static Profile Combine(Profile p1, Profile p2)
        {
            var res = new Profile
            {
                Name = p2.Name,
                IsDefault = p2.IsDefault,
                InheritFrom = p1.InheritFrom,
                ConnectionString = p2.ConnectionString,
            };

            res.IgnoreDatabases = p1.IgnoreDatabases.Concat(p2.IgnoreDatabases);
            res.IgnoreUsers = p1.IgnoreUsers.Concat(p2.IgnoreUsers);
            res.IgnoreStatements = p1.IgnoreStatements.Concat(p2.IgnoreStatements);
            res.IgnorePrefixes = p1.IgnorePrefixes.Concat(p2.IgnorePrefixes);

            return res;
        }
    }
}
