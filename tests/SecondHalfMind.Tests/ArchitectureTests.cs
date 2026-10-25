using System.Reflection;
using SecondHalfMind.Agents;
using SecondHalfMind.Domain;

namespace SecondHalfMind.Tests;

/// <summary>
/// The core security guarantee, enforced in CI: agents can read match data but can never modify
/// or delete it.
/// </summary>
public class ArchitectureTests
{
    private static readonly Assembly AgentsAssembly = typeof(StatsAgent).Assembly;
    private static readonly Assembly DomainAssembly = typeof(MatchEvent).Assembly;

    [Fact]
    public void Agents_do_not_reference_the_data_layer()
    {
        var references = AgentsAssembly.GetReferencedAssemblies().Select(a => a.Name).ToList();

        Assert.DoesNotContain("SecondHalfMind.Data", references);
        Assert.DoesNotContain("SecondHalfMind.Api", references);
    }

    [Fact]
    public void No_agent_type_depends_on_a_write_capability()
    {
        var writeCapabilities = DomainAssembly.GetTypes()
            .Where(t => t.IsInterface && t.GetCustomAttribute<DataWriteCapabilityAttribute>() is not null)
            .ToHashSet();
        Assert.NotEmpty(writeCapabilities);

        const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var offenders = new List<string>();
        foreach (var type in AgentsAssembly.GetTypes())
        {
            var dependencies = type.GetConstructors(all).SelectMany(c => c.GetParameters()).Select(p => p.ParameterType)
                .Concat(type.GetFields(all).Select(f => f.FieldType))
                .Concat(type.GetProperties(all).Select(p => p.PropertyType));

            offenders.AddRange(dependencies
                .Where(d => writeCapabilities.Any(w => w.IsAssignableFrom(d)))
                .Select(d => $"{type.FullName} -> {d.Name}"));
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void Write_capabilities_are_append_only()
    {
        string[] forbidden = ["Update", "Delete", "Remove", "Clear", "Replace", "Overwrite", "Truncate"];
        var methods = DomainAssembly.GetTypes()
            .Where(t => t.IsInterface && t.GetCustomAttribute<DataWriteCapabilityAttribute>() is not null)
            .SelectMany(t => t.GetMethods().Select(m => $"{t.Name}.{m.Name}"))
            .ToList();

        Assert.NotEmpty(methods);
        Assert.DoesNotContain(methods, m => forbidden.Any(f => m.Contains(f, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Agent_output_channel_is_append_only()
    {
        var methods = typeof(IInsightSink).GetMethods().Select(m => m.Name).ToList();

        Assert.Equal(new[] { "Publish" }, methods);
    }

    [Fact]
    public void Read_models_expose_no_mutating_methods()
    {
        Type[] readModels = [typeof(IMatchReadModel), typeof(ILeagueReadModel), typeof(IInsightReadModel), typeof(IFanPredictionReadModel), typeof(ITrackingReadModel)];
        var methods = readModels.SelectMany(t => t.GetMethods()).Where(m => !m.IsSpecialName).Select(m => m.Name).ToList();

        Assert.All(methods, name => Assert.True(name.StartsWith("Get", StringComparison.Ordinal) || name.StartsWith("List", StringComparison.Ordinal), name));
    }
}
