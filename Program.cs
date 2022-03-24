using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommandLine;
using Crayon;

namespace XELive
{
    class Program
    {
        class StartupOptions : Profile
        {
            [Option('p', "profile", Required = false, HelpText = "Name of the base profile to use (configured in ~/.xelive.json)")]
            public string Profile { get; set; }
            [Option('q', "query", Required = false, HelpText = "Filtering expression (WHERE in CREATE EVENT SESSION ADD EVENT)")]
            public string Query { get; set; }

            [Option("procedure-statements", HelpText = "Include individual stored procedure statements in the output")]
            public new bool IndividualStatements { get; set; }
        }

        static Task Main(string[] args)
            =>  Parser.Default.ParseArguments<StartupOptions>(args)
                    .WithParsedAsync(Run);

        static async Task Run(StartupOptions options)
        {
            var cts = new CancellationTokenSource();
            XEStreamer xes = null;
            Task task = null;

            try
            {
                var profiles = ReadConfigurations(
                    Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".xelive.json")
                );

                Profile nameDefault = null, cfgDefault = null, selected = null;
                var profileMap = new Dictionary<string, Profile>(StringComparer.OrdinalIgnoreCase);

                await foreach (var p in profiles)
                {
                    if (p.IsDefault)
                        cfgDefault = p;
                    if (options.Profile != null && p.Name == options.Profile)
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

                if (options.IndividualStatements)
                {
                    ((Profile)options).IndividualStatements = true;
                }
                profile = Profile.Combine(profile, options);

                xes = new XEStreamer(profile);
                task = xes.Run(options.Query, cts.Token);
            }
            catch (Exception err)
            {
                FatalError(err);
            }

            try
            {
                _ = task.ContinueWith(t =>
                {
                    if (t.Exception != null)
                    {
                        FatalError(t.Exception);
                    }
                });

                while (!cts.IsCancellationRequested)
                {
                    var key = Console.ReadKey(true);
                    switch (key.Key)
                    {
                        case ConsoleKey.Q:
                        case ConsoleKey.Escape:
                            cts.Cancel();
                            break;
                    }
                }

                await task;
            }
            catch
            {
                // error is already logged in task continuation
            }
        }

        private static void Fail(IEnumerable<Error> errors)
        {
            foreach (var err in errors)
            {
                Console.WriteLine($"Argument parsing error: {Output.BrightRed(err.ToString())}");
            }
        }

        private static void FatalError(Exception err)
        {
            Console.WriteLine($"ERROR: {Output.BrightRed(err.Message)} ({err.GetType()})");
            Console.WriteLine(Output.Red(err.StackTrace));
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
