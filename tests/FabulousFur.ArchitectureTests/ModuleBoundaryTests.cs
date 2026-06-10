using NetArchTest.Rules;
using System.Linq;
using System.Reflection;
using Xunit;

namespace FabulousFur.ArchitectureTests;

public class ModuleBoundaryTests
{
    private static readonly string[] ModuleAssemblyNames =
    {
        "FabulousFur.Modules.Tenants",
        "FabulousFur.Modules.Clients",
        "FabulousFur.Modules.Scheduling",
        "FabulousFur.Modules.Services",
    };

    [Fact]
    public void Modules_should_not_reference_each_other()
    {
        foreach (var moduleName in ModuleAssemblyNames)
        {
            var assembly = Assembly.Load(moduleName);
            var forbiddenModules = ModuleAssemblyNames
                .Where(m => m != moduleName)
                .ToArray();

            var result = Types.InAssembly(assembly)
                .Should()
                .NotHaveDependencyOnAny(forbiddenModules)
                .GetResult();

            Assert.True(
                result.IsSuccessful,
                $"{moduleName} has a forbidden dependency on another module. " +
                $"Failing types: {string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>())}");
        }
    }
}
