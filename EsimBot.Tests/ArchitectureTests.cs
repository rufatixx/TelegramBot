using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using EsimBot.BLL.Services;
using Microsoft.AspNetCore.Mvc;

namespace EsimBot.Tests;

public sealed class ArchitectureTests
{
    [Fact]
    public void Each_business_service_has_its_matching_interface()
    {
        var services = typeof(ShopService).Assembly.GetTypes().Where(type => type.IsPublic && type.IsClass
            && !type.IsAbstract && type.Namespace == "EsimBot.BLL.Services").ToArray();
        Assert.NotEmpty(services);
        foreach (var service in services)
            Assert.Contains(service.GetInterfaces(), contract => contract.Name == "I" + service.Name);
    }

    [Fact]
    public void Each_controller_depends_on_exactly_one_matching_business_service()
    {
        var controllers = typeof(ShopService).Assembly.GetTypes().Where(type => !type.IsAbstract
            && typeof(ControllerBase).IsAssignableFrom(type)).ToArray();
        Assert.Equal(3, controllers.Length);
        var contracts = new HashSet<Type>();
        foreach (var controller in controllers)
        {
            var constructor = Assert.Single(controller.GetConstructors());
            var parameter = Assert.Single(constructor.GetParameters());
            Assert.True(parameter.ParameterType.IsInterface);
            Assert.Equal("EsimBot.BLL.Services", parameter.ParameterType.Namespace);
            Assert.Equal("I" + controller.Name.Replace("Controller", "Service", StringComparison.Ordinal),
                parameter.ParameterType.Name);
            Assert.True(contracts.Add(parameter.ParameterType), "Each controller must have its own BLL service.");
        }
    }

    [Fact]
    public void Exactly_four_table_repositories_have_matching_interfaces()
    {
        var repositories = typeof(ShopService).Assembly.GetTypes().Where(type => type.IsPublic
            && type.IsClass && !type.IsAbstract && type.Namespace == "EsimBot.DAL.Repos"
            && type.Name.EndsWith("Repository", StringComparison.Ordinal)).ToArray();
        Assert.Equal(new[] { "AppStateRepository", "EsimsRepository", "OrdersRepository", "PaymentsRepository" },
            repositories.Select(type => type.Name).Order());
        foreach (var repository in repositories)
            Assert.Contains(repository.GetInterfaces(), contract => contract.Name == "I" + repository.Name);
    }

    [Fact]
    public void Controller_action_bodies_have_only_a_service_call_and_return()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(SourceFile())!, "..", "EsimBot"));
        var files = Directory.EnumerateFiles(Path.Combine(root, "View", "ApiControllers"), "*Controller.cs");
        var actions = 0;
        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            var matches = Regex.Matches(source, @"public\s+(?:async\s+)?(?:Task<IActionResult>|IActionResult)\s+\w+\([^)]*\)\s*\{([^{}]*)\}");
            foreach (Match match in matches)
            {
                var statements = match.Groups[1].Value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                Assert.Equal(2, statements.Length);
                Assert.StartsWith("var result = ", statements[0]);
                Assert.Contains("service.", statements[0]);
                if (Path.GetFileName(file) == "HomeController.cs")
                    Assert.Matches("^return (View|Content)\\(.*result.*\\);$", statements[1]);
                else
                    Assert.Equal("return StatusCode(result.StatusCode, result.Payload);", statements[1]);
                actions++;
            }
        }
        Assert.Equal(7, actions);
    }

    private static string SourceFile([CallerFilePath] string file = "") => file;
}
