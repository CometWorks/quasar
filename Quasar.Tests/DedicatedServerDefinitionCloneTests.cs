using System.Reflection;
using Quasar.Models;
using Xunit;

namespace Quasar.Tests;

public sealed class DedicatedServerDefinitionCloneTests
{
    // Clone is the one copy of a server definition the catalog and the supervisor use, so
    // a property it misses never reaches the launch (SE1-0197 lost the implicit mod setting).
    [Fact]
    public void CloneCopiesEveryProperty()
    {
        var properties = typeof(DedicatedServerDefinition)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanRead && property.CanWrite)
            .ToArray();
        var source = new DedicatedServerDefinition();
        foreach (var property in properties)
            property.SetValue(
                source,
                ValueDifferentFrom(property.GetValue(source), property.PropertyType)
            );

        var clone = source.Clone();

        Assert.All(
            properties,
            property => Assert.Equal(property.GetValue(source), property.GetValue(clone))
        );
    }

    private static object ValueDifferentFrom(object? current, Type type)
    {
        if (type == typeof(string))
            return current + "-changed";
        if (type == typeof(bool))
            return !(bool)current!;
        if (type == typeof(int))
            return (int)current! + 7;
        if (type == typeof(float))
            return (float)current! + 0.5f;
        if (type == typeof(DateTimeOffset))
            return ((DateTimeOffset)current!).AddDays(-3);
        if (type.IsEnum)
        {
            var values = Enum.GetValues(type);
            foreach (var value in values)
            {
                if (!value.Equals(current))
                    return value;
            }
        }

        throw new NotSupportedException($"Add a changed value for {type.Name} properties.");
    }
}
