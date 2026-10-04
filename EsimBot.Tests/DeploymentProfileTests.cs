using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace EsimBot.Tests;

public sealed class DeploymentProfileTests
{
    [Fact]
    public void Web_project_builds_a_portable_server_without_desktop_frameworks()
    {
        var project = Project();
        Assert.Equal("Microsoft.NET.Sdk.Web", project.Root!.Attribute("Sdk")?.Value);
        Assert.Equal("false", Property(project, "UseAppHost"));
        Assert.Equal("InProcess", Property(project, "AspNetCoreHostingModel"));
        Assert.NotEqual("Library", Property(project, "OutputType"));
        Assert.NotEqual("true", Property(project, "UseWindowsForms"));
        Assert.NotEqual("true", Property(project, "UseWPF"));
    }

    [Fact]
    public void Every_publish_includes_the_main_settings_and_excludes_backups_and_examples()
    {
        var project = Project();
        foreach (var metadata in new[] { "CopyToOutputDirectory", "CopyToPublishDirectory" })
        {
            Assert.Equal("PreserveNewest", Content(project, "appsettings.json").Attribute(metadata)?.Value);
            Assert.Equal("Never", Content(project, "appsettings.Local.json").Attribute(metadata)?.Value);
            Assert.Equal("Never", Content(project, "appsettings.Example.json").Attribute(metadata)?.Value);
        }
        var profile = XDocument.Load(Path.Combine(Root, "Properties", "PublishProfiles", "Server.pubxml"));
        Assert.Equal("FileSystem", Property(profile, "WebPublishMethod"));
        Assert.Equal("false", Property(profile, "SelfContained"));
        Assert.DoesNotContain(profile.Descendants().Where(element => element.Name.LocalName == "Copy"),
            element => element.Attribute("SourceFiles")?.Value.Contains("appsettings.Local.json", StringComparison.Ordinal) == true);
        var program = File.ReadAllText(Path.Combine(Root, "Program.cs"));
        Assert.DoesNotMatch(new Regex("AddJsonFile\\s*\\(\\s*\"appsettings\\.Local\\.json\""), program);
    }

    [Fact]
    public void Ide_launch_profiles_open_the_web_application_over_http_and_https()
    {
        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "Properties", "launchSettings.json")));
        var profiles = settings.RootElement.GetProperty("profiles").EnumerateObject()
            .Select(profile => profile.Value)
            .Where(profile => profile.GetProperty("commandName").GetString() == "Project").ToArray();
        Assert.NotEmpty(profiles);
        var schemes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in profiles)
        {
            Assert.True(profile.GetProperty("launchBrowser").GetBoolean());
            Assert.Equal("home/index", profile.GetProperty("launchUrl").GetString()?.TrimStart('/'));
            foreach (var address in profile.GetProperty("applicationUrl").GetString()!.Split(';'))
            {
                var url = new Uri(address, UriKind.Absolute);
                Assert.True(url.IsLoopback);
                Assert.Contains(url.Scheme, new[] { "http", "https" });
                schemes.Add(url.Scheme);
            }
        }
        Assert.Contains("http", schemes);
        Assert.Contains("https", schemes);
    }

    private static XDocument Project() => XDocument.Load(Path.Combine(Root, "EsimBot.csproj"));
    private static XElement Content(XDocument project, string name) => Assert.Single(project.Descendants(),
        element => element.Name.LocalName == "Content" && element.Attribute("Update")?.Value == name);
    private static string? Property(XDocument project, string name) => project.Descendants()
        .LastOrDefault(element => element.Name.LocalName == name)?.Value;
    private static string Root => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Source())!, "..", "EsimBot"));
    private static string Source([CallerFilePath] string path = "") => path;
}
