using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Crayon;

namespace XELive
{
    class Program
    {
        static async Task Main(string[] args)
        {
            try
            {
                var profiles = ReadConfigurations(
                    Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".xelive.json")
                );

                var selname = args.Length > 0 ? args[0] : null;
                Profile nameDefault = null, cfgDefault = null, selected = null;
                var profileMap = new Dictionary<string, Profile>(StringComparer.OrdinalIgnoreCase);

                await foreach (var p in profiles)
                {
                    if (p.IsDefault)
                        cfgDefault = p;
                    if (selname != null && p.Name == selname)
                        selected = p;
                    if (p.Name == "default")
                        nameDefault = p;
                    if (p.Name != null)
                        profileMap[p.Name] = p;
                }

                var profile = selected ?? cfgDefault ?? nameDefault;
                while (!string.IsNullOrEmpty(profile.InheritFrom) && profileMap.TryGetValue(profile.InheritFrom, out var baseProfile))
                {
                    profileMap.Remove(profile.InheritFrom);
                    profile = Profile.Combine(baseProfile, profile);
                }

                await new XEStreamer(profile)
                    .Run(string.Join(" ", args), default);
            }
            catch (Exception err)
            {
                Console.WriteLine($"ERROR: {Output.BrightRed(err.Message)} ({err.GetType()})");
                Console.WriteLine(Output.Red(err.StackTrace));
            }
        }

        static async IAsyncEnumerable<Profile> ReadConfigurations(params string[] paths)
        {
            var opts = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            };

            foreach (string path in paths)
            {
                if (File.Exists(path))
                {
                    using (var stream = File.OpenRead(path))
                    {
                        var cfg = await JsonSerializer.DeserializeAsync<Configuration>(stream, opts);
                        foreach (var profile in cfg.Profiles ?? Array.Empty<Profile>())
                        {
                            yield return profile;
                        }
                    }
                }
                else
                {
                    Console.WriteLine($"Warning: {Output.BrightYellow($"Settings file {Output.Bold(path)} not found")}");
                }
            }
        }
    }
}
