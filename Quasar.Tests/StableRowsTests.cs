using Quasar.Components.Shared;
using Xunit;

namespace Quasar.Tests;

public sealed class StableRowsTests
{
    private sealed class Row
    {
        public string Id { get; init; } = "";
        public string Name { get; set; } = "";
    }

    [Fact]
    public void UnchangedCopiesReuseTheFirstInstances()
    {
        var rows = new StableRows<Row>(row => row.Id);
        var first = rows.Reuse([new() { Id = "a", Name = "A" }, new() { Id = "b", Name = "B" }]);

        var second = rows.Reuse([new() { Id = "b", Name = "B" }, new() { Id = "a", Name = "A" }]);

        Assert.Same(first[1], second[0]);
        Assert.Same(first[0], second[1]);
    }

    [Fact]
    public void ChangedRowsGetTheFreshInstance()
    {
        var rows = new StableRows<Row>(row => row.Id);
        var first = rows.Reuse([new() { Id = "a", Name = "A" }, new() { Id = "b", Name = "B" }]);
        var changed = new Row { Id = "a", Name = "A2" };

        var second = rows.Reuse([changed, new() { Id = "b", Name = "B" }]);

        Assert.Same(changed, second[0]);
        Assert.Same(first[1], second[1]);
        Assert.Same(changed, rows.Reuse([new() { Id = "a", Name = "A2" }])[0]);
    }

    [Fact]
    public void InstancesEditedInPlaceAreReplaced()
    {
        // An unsaved in-place edit, like a failed dev folder Debug toggle, must not outlive the next rebuild.
        var rows = new StableRows<Row>(row => row.Id);
        var first = rows.Reuse([new() { Id = "a", Name = "A" }]);
        first[0].Name = "edited";

        var second = rows.Reuse([new() { Id = "a", Name = "A" }]);

        Assert.Equal("A", second[0].Name);
    }

    [Fact]
    public void DuplicateIdsNeverShareAnInstance()
    {
        var rows = new StableRows<Row>(row => row.Id);
        rows.Reuse([new() { Id = "a", Name = "A" }]);

        var result = rows.Reuse([new() { Id = "a", Name = "A" }, new() { Id = "a", Name = "A" }]);

        Assert.NotSame(result[0], result[1]);
    }
}
