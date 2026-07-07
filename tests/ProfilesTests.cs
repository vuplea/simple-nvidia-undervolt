using System.Runtime.CompilerServices;
using System.Text.Json;

namespace SimpleNvidiaUndervolt.Tests;

/// <summary>The release's profiles zip is built at publish time by <c>profiles/generate.ps1</c> from
/// <c>profiles/profiles.json</c> — the source of truth for the profile values, which an edit must
/// keep in step with PROFILES.md by hand. These pin the json: the matrix shape, and that each baked
/// command line parses.</summary>
public class ProfilesTests
{
    private static Dictionary<string, Dictionary<string, string>> LoadProfiles(
        [CallerFilePath] string thisFile = "")
    {
        string path = Path.GetFullPath(
            Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "profiles", "profiles.json"));
        return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(
            File.ReadAllText(path))!;
    }

    [Fact]
    public void EveryFolderShipsThreeFamiliesInFourTiersPlusReset()
    {
        var folders = LoadProfiles();
        Assert.Equal(12, folders.Count);
        foreach ((string folder, Dictionary<string, string> profiles) in folders)
        {
            // Folder and shortcut names become paths in generate.ps1; a '/' would silently nest.
            Assert.All(profiles.Keys.Append(folder),
                name => Assert.Equal(-1, name.IndexOfAny(Path.GetInvalidFileNameChars())));
            Assert.Equal(13, profiles.Count);
            Assert.Equal(4, profiles.Keys.Count(n => n.StartsWith("Perf boost")));
            Assert.Equal(4, profiles.Keys.Count(n => n.StartsWith("Power cut, same perf")));
            Assert.Equal(4, profiles.Keys.Count(n => n.StartsWith("Deep power cut")));
            Assert.Contains("Reset to stock", profiles.Keys);
        }
    }

    [Fact]
    public void EveryProfileIsAValidCommand()
    {
        foreach (Dictionary<string, string> profiles in LoadProfiles().Values)
        {
            foreach ((string name, string args) in profiles)
            {
                string[] argv = args.Split(' ');
                if (name == "Reset to stock")
                {
                    Assert.Equal(new[] { "clear" }, argv);
                }
                else
                {
                    // No command word baked in - a leading option implies 'tune', which the dispatcher
                    // prepends before parsing; mirror that here.
                    Assert.StartsWith("--", argv[0]);
                    TuneRequest.Parse(argv.Prepend("tune").ToArray()); // throws on an invalid line
                }
            }
        }
    }
}
