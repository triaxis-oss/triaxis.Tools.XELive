using System;
using System.Collections.Generic;
using System.Linq;
using CommandLine;

namespace XELive
{
    class Profile
    {
        public string Name { get; set; }
        public bool IsDefault { get; set; }
        public string InheritFrom { get; set; }
        [Option("connection-string", HelpText = "Connection string override")]
        public string ConnectionString { get; set; }
        public bool? IndividualStatements { get; set; }

        [Option("only-database")]
        public IEnumerable<string> OnlyDatabases { get; set; } = Enumerable.Empty<string>();
        [Option("only-user")]
        public IEnumerable<string> OnlyUsers { get; set; } = Enumerable.Empty<string>();
        [Option("only-statement")]
        public IEnumerable<string> OnlyStatements { get; set; } = Enumerable.Empty<string>();
        [Option("only-prefix")]
        public IEnumerable<string> OnlyPrefixes { get; set; } = Enumerable.Empty<string>();

        [Option("ignore-database")]
        public IEnumerable<string> IgnoreDatabases { get; set; } = Enumerable.Empty<string>();
        [Option("ignore-user")]
        public IEnumerable<string> IgnoreUsers { get; set; } = Enumerable.Empty<string>();
        [Option("ignore-statement")]
        public IEnumerable<string> IgnoreStatements { get; set; } = Enumerable.Empty<string>();
        [Option("ignore-prefix")]
        public IEnumerable<string> IgnorePrefixes { get; set; } = Enumerable.Empty<string>();

        public static Profile Combine(Profile p1, Profile p2)
        {
            var res = new Profile
            {
                Name = p2.Name ?? p1.Name,
                IsDefault = p2.IsDefault,
                InheritFrom = p1.InheritFrom,
                ConnectionString = p2.ConnectionString ?? p1.ConnectionString,
                IndividualStatements = p2.IndividualStatements ?? p1.IndividualStatements,
            };

            res.OnlyDatabases = p1.OnlyDatabases.Concat(p2.OnlyDatabases);
            res.OnlyUsers = p1.OnlyUsers.Concat(p2.OnlyUsers);
            res.OnlyStatements = p1.OnlyStatements.Concat(p2.OnlyStatements);
            res.OnlyPrefixes = p1.OnlyPrefixes.Concat(p2.OnlyPrefixes);

            res.IgnoreDatabases = p1.IgnoreDatabases.Concat(p2.IgnoreDatabases);
            res.IgnoreUsers = p1.IgnoreUsers.Concat(p2.IgnoreUsers);
            res.IgnoreStatements = p1.IgnoreStatements.Concat(p2.IgnoreStatements);
            res.IgnorePrefixes = p1.IgnorePrefixes.Concat(p2.IgnorePrefixes);

            return res;
        }
    }
}
