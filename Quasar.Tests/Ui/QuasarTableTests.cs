using Bunit;
using Microsoft.AspNetCore.Components;
using Quasar.Ui.Components;
using Xunit;

namespace Quasar.Tests.Ui;

public sealed class QuasarTableTests
{
    private sealed record Row(string Name, int Port);

    private static readonly IReadOnlyList<Row> Rows =
    [
        new("Alpha", 27016),
        new("Bravo", 27017),
        new("Charlie", 27018),
    ];

    [Fact]
    public void RendersRowsAndPages()
    {
        using var context = new BunitContext();
        using var cut = RenderTable(context, Rows, rowsPerPage: 2);

        Assert.Equal(2, cut.FindAll("tbody tr").Count);
        Assert.Contains("of 3", cut.Markup, StringComparison.Ordinal);

        cut.Find("button[aria-label='Next page']").Click();

        var rows = cut.FindAll("tbody tr");
        Assert.Single(rows);
        Assert.Contains("Charlie", rows[0].TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void HeaderCellsSitInsideAHeaderRow()
    {
        using var context = new BunitContext();
        using var cut = RenderTable(context, Rows, rowsPerPage: 10);

        Assert.Single(cut.FindAll("thead > tr > th"));
    }

    [Fact]
    public void SortsByColumn()
    {
        using var context = new BunitContext();
        using var cut = RenderTable(context, Rows, rowsPerPage: 10);

        var sort = cut.Find("th[aria-sort='none'] .quasar-table-sort");
        sort.Click();

        Assert.Equal("ascending", cut.Find("th").GetAttribute("aria-sort"));
        Assert.Contains("Alpha", cut.FindAll("tbody tr")[0].TextContent, StringComparison.Ordinal);

        cut.Find("th .quasar-table-sort").Click();

        Assert.Equal("descending", cut.Find("th").GetAttribute("aria-sort"));
        Assert.Contains("Charlie", cut.FindAll("tbody tr")[0].TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void SortingNeverThrowsForValuesWithoutAnOrder()
    {
        var rows = new[] { new Row("b", 1), new Row("a", 2) };

        using var context = new BunitContext();
        using var cut = context.Render<QuasarTable<Row>>(parameters => parameters
            .Add(p => p.Items, rows)
            .Add(p => p.HeaderContent, builder =>
            {
                builder.OpenComponent<QuasarTableColumn<Row>>(0);
                builder.AddAttribute(1, "Title", "Mixed");
                // Rows return an object that is not comparable, then a number.
                builder.AddAttribute(2, "SortBy", (Func<Row, object?>)(row => row.Port == 1 ? new object() : row.Port));
                builder.CloseComponent();
            })
            .Add(p => p.RowTemplate, RowTemplate()));

        cut.Find("th .quasar-table-sort").Click();

        Assert.Equal(2, cut.FindAll("tbody tr").Count);
    }

    [Fact]
    public void NullsSortLastInBothDirections()
    {
        var rows = new[] { new Row("b", 2), new Row(null!, 0), new Row("a", 1) };

        using var context = new BunitContext();
        using var cut = RenderTable(context, rows, rowsPerPage: 10);

        cut.Find("th .quasar-table-sort").Click();
        Assert.Contains("a", cut.FindAll("tbody tr")[0].TextContent, StringComparison.Ordinal);
        Assert.Equal("0", cut.FindAll("tbody tr")[2].QuerySelectorAll("td")[1].TextContent);

        cut.Find("th .quasar-table-sort").Click();
        Assert.Equal("0", cut.FindAll("tbody tr")[2].QuerySelectorAll("td")[1].TextContent);
    }

    [Fact]
    public void HoverClassFollowsTheParameter()
    {
        using var context = new BunitContext();
        using var cut = context.Render<QuasarTable<Row>>(parameters => parameters
            .Add(p => p.Items, Rows)
            .Add(p => p.RowTemplate, RowTemplate()));

        Assert.DoesNotContain("quasar-table-hover", cut.Find("table").ClassList, StringComparer.Ordinal);

        cut.Render(parameters => parameters
            .Add(p => p.Items, Rows)
            .Add(p => p.Hover, true)
            .Add(p => p.RowTemplate, RowTemplate()));

        Assert.Contains("quasar-table-hover", cut.Find("table").ClassList, StringComparer.Ordinal);
    }

    [Fact]
    public void RowsThatCompareEqualDoNotShareAKey()
    {
        var rows = new[] { new Row("Same", 1), new Row("Same", 1) };

        using var context = new BunitContext();
        using var cut = context.Render<QuasarTable<Row>>(parameters => parameters
            .Add(p => p.Items, rows)
            .Add(p => p.RowTemplate, RowTemplate()));

        Assert.Equal(2, cut.FindAll("tbody tr").Count);
    }

    [Fact]
    public void RowKeyKeepsRowStateWithTheRowAcrossASort()
    {
        using var context = new BunitContext();
        using var cut = context.Render<QuasarTable<Row>>(parameters => parameters
            .Add(p => p.Items, Rows)
            .Add(p => p.RowKey, row => row.Name)
            .Add(p => p.HeaderContent, Header())
            .Add(p => p.RowTemplate, RowTemplate()));

        Assert.Equal(3, cut.FindAll("tbody tr").Count);
    }

    [Fact]
    public void NarrowLayoutDefaultsToScrollAndStackIsOptIn()
    {
        using var context = new BunitContext();
        using var cut = context.Render<QuasarTable<Row>>(parameters => parameters
            .Add(p => p.Items, Rows)
            .Add(p => p.RowTemplate, RowTemplate()));

        Assert.DoesNotContain("quasar-table-stack", cut.Find("table").ClassName, StringComparison.Ordinal);

        cut.Render(parameters => parameters
            .Add(p => p.Items, Rows)
            .Add(p => p.NarrowLayout, QuasarTableNarrowLayout.Stack)
            .Add(p => p.Breakpoint, QuasarBreakpoint.Medium)
            .Add(p => p.RowTemplate, RowTemplate()));

        var table = cut.Find("table");
        Assert.Contains("quasar-table-stack", table.ClassList, StringComparer.Ordinal);
        Assert.Contains("quasar-table-stack-md", table.ClassList, StringComparer.Ordinal);
    }

    [Fact]
    public void CellsCarryTheirLabelForTheStackedLayout()
    {
        using var context = new BunitContext();
        using var cut = context.Render<QuasarTable<Row>>(parameters => parameters
            .Add(p => p.Items, Rows)
            .Add(p => p.RowTemplate, row => builder =>
            {
                builder.OpenComponent<QuasarTableCell>(0);
                builder.AddAttribute(1, "Label", "Name");
                builder.AddAttribute(2, "ChildContent", (RenderFragment)(inner => inner.AddContent(0, row.Name)));
                builder.CloseComponent();
            }));

        Assert.Equal("Name", cut.FindAll("tbody td")[0].GetAttribute("data-label"));
    }

    [Fact]
    public void RowClickRaisesOnRowClickWithTheRow()
    {
        Row? clicked = null;

        using var context = new BunitContext();
        using var cut = context.Render<QuasarTable<Row>>(parameters => parameters
            .Add(p => p.Items, Rows)
            .Add(p => p.OnRowClick, EventCallback.Factory.Create<Row>(this, row => clicked = row))
            .Add(p => p.RowTemplate, RowTemplate()));

        cut.FindAll("tbody tr")[1].Click();

        Assert.Equal(Rows[1], clicked);
        Assert.Contains("quasar-table-row-clickable", cut.FindAll("tbody tr")[0].ClassList, StringComparer.Ordinal);
    }

    [Fact]
    public void RowsWithoutAClickHandlerRegisterNoEvent()
    {
        using var context = new BunitContext();
        using var cut = context.Render<QuasarTable<Row>>(parameters => parameters
            .Add(p => p.Items, Rows)
            .Add(p => p.RowTemplate, RowTemplate()));

        Assert.DoesNotContain("blazor:onclick", cut.Find("tbody").InnerHtml, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpandedRowsRenderTheirDetailBelowTheRow()
    {
        using var context = new BunitContext();
        using var cut = context.Render<QuasarTable<Row>>(parameters => parameters
            .Add(p => p.Items, Rows)
            .Add(p => p.HeaderContent, Header())
            .Add(p => p.IsExpanded, row => row.Name == "Bravo")
            .Add(p => p.DetailTemplate, row => builder => builder.AddContent(0, "Detail for " + row.Name))
            .Add(p => p.RowTemplate, RowTemplate()));

        var rows = cut.FindAll("tbody tr");
        Assert.Equal(4, rows.Count);
        Assert.Contains("quasar-table-detail", rows[2].ClassList, StringComparer.Ordinal);
        Assert.Contains("Detail for Bravo", rows[2].TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void ItemsAndLoadDataTogetherAreRejected()
    {
        using var context = new BunitContext();

        var exception = Assert.ThrowsAny<Exception>(() =>
            context.Render<QuasarTable<Row>>(parameters => parameters
                .Add(p => p.Items, Rows)
                .Add(p => p.LoadData, (_, _) => Task.FromResult(new QuasarTableData<Row>(Rows, Rows.Count)))));

        Assert.Contains("either Items or LoadData", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void LoadDataReceivesPageState()
    {
        QuasarTableState? received = null;

        using var context = new BunitContext();
        using var cut = context.Render<QuasarTable<Row>>(parameters => parameters
            .Add(p => p.RowsPerPage, 5)
            .Add(p => p.LoadData, (state, _) =>
            {
                received = state;
                return Task.FromResult(new QuasarTableData<Row>(Rows, Rows.Count));
            })
            .Add(p => p.HeaderContent, Header())
            .Add(p => p.RowTemplate, RowTemplate()));

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("tbody tr").Count));
        Assert.NotNull(received);
        Assert.Equal(1, received!.Value.Page);
        Assert.Equal(5, received.Value.PageSize);
    }

    [Fact]
    public void LoadDataShowsALoadingStateInsteadOfTheEmptyMessage()
    {
        var pending = new TaskCompletionSource<QuasarTableData<Row>>();

        using var context = new BunitContext();
        using var cut = context.Render<QuasarTable<Row>>(parameters => parameters
            .Add(p => p.LoadData, (_, _) => pending.Task)
            .Add(p => p.EmptyContent, builder => builder.AddContent(0, "Nothing here."))
            .Add(p => p.RowTemplate, RowTemplate()));

        Assert.Equal("true", cut.Find(".quasar-table-wrap").GetAttribute("aria-busy"));
        Assert.NotNull(cut.Find(".quasar-table-progress"));
        Assert.DoesNotContain("Nothing here.", cut.Markup, StringComparison.Ordinal);

        pending.SetResult(new QuasarTableData<Row>([], 0));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Nothing here.", cut.Markup, StringComparison.Ordinal);
            Assert.Null(cut.Find(".quasar-table-wrap").GetAttribute("aria-busy"));
        });
    }

    [Fact]
    public void LoadFailureShowsAnAlertAndNoRows()
    {
        using var context = new BunitContext();
        using var cut = context.Render<QuasarTable<Row>>(parameters => parameters
            .Add(p => p.LoadData, (_, _) => throw new InvalidOperationException("boom"))
            .Add(p => p.RowTemplate, RowTemplate()));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Could not load the table.", cut.Find("[role=alert]").TextContent, StringComparison.Ordinal);
            Assert.Empty(cut.FindAll("tbody tr"));
        });
    }

    [Fact]
    public void StaleLoadIsDiscarded()
    {
        var first = new TaskCompletionSource<QuasarTableData<Row>>();
        var calls = 0;

        using var context = new BunitContext();
        using var cut = context.Render<QuasarTable<Row>>(parameters => parameters
            .Add(p => p.RowsPerPage, 1)
            .Add(p => p.HeaderContent, Header())
            .Add(p => p.RowTemplate, RowTemplate())
            .Add(p => p.LoadData, (state, _) => ++calls == 1
                ? first.Task
                : Task.FromResult(new QuasarTableData<Row>([Rows[2]], Rows.Count))));

        cut.InvokeAsync(() => cut.Instance.ReloadAsync());
        cut.WaitForAssertion(() => Assert.Contains("Charlie", cut.Markup, StringComparison.Ordinal));

        first.SetResult(new QuasarTableData<Row>([Rows[0]], Rows.Count));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Charlie", cut.Markup, StringComparison.Ordinal);
            Assert.DoesNotContain("Alpha", cut.Markup, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void ChangingRowsPerPageReloadsFromTheFirstPage()
    {
        var states = new List<QuasarTableState>();

        using var context = new BunitContext();
        using var cut = context.Render<QuasarTable<Row>>(parameters => parameters
            .Add(p => p.RowsPerPage, 2)
            .Add(p => p.RowTemplate, RowTemplate())
            .Add(p => p.LoadData, (state, _) =>
            {
                states.Add(state);
                return Task.FromResult(new QuasarTableData<Row>(Rows, Rows.Count));
            }));

        cut.WaitForAssertion(() => Assert.Single(states));

        cut.Render(parameters => parameters
            .Add(p => p.RowsPerPage, 3)
            .Add(p => p.RowTemplate, RowTemplate())
            .Add(p => p.LoadData, (state, _) =>
            {
                states.Add(state);
                return Task.FromResult(new QuasarTableData<Row>(Rows, Rows.Count));
            }));

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(2, states.Count);
            Assert.Equal(3, states[1].PageSize);
            Assert.Equal(1, states[1].Page);
        });
    }

    [Fact]
    public void LoadDataDoesNotReloadOnAnUnrelatedParentRender()
    {
        var calls = 0;

        using var context = new BunitContext();
        using var cut = context.Render<QuasarTable<Row>>(parameters => parameters
            .Add(p => p.RowTemplate, RowTemplate())
            .Add(p => p.LoadData, (_, _) =>
            {
                calls++;
                return Task.FromResult(new QuasarTableData<Row>(Rows, Rows.Count));
            }));

        cut.WaitForAssertion(() => Assert.Equal(1, calls));

        cut.Render(parameters => parameters
            .Add(p => p.Dense, true)
            .Add(p => p.RowTemplate, RowTemplate())
            .Add(p => p.LoadData, (_, _) =>
            {
                calls++;
                return Task.FromResult(new QuasarTableData<Row>(Rows, Rows.Count));
            }));

        Assert.Equal(1, calls);
    }

    private static IRenderedComponent<QuasarTable<Row>> RenderTable(
        BunitContext context,
        IReadOnlyList<Row> rows,
        int rowsPerPage) =>
        context.Render<QuasarTable<Row>>(parameters => parameters
            .Add(p => p.Items, rows)
            .Add(p => p.RowsPerPage, rowsPerPage)
            .Add(p => p.HeaderContent, Header())
            .Add(p => p.RowTemplate, RowTemplate()));

    private static RenderFragment Header() => builder =>
    {
        builder.OpenComponent<QuasarTableColumn<Row>>(0);
        builder.AddAttribute(1, "Title", "Name");
        builder.AddAttribute(2, "SortBy", (Func<Row, object?>)(row => row.Name));
        builder.CloseComponent();
    };

    private static RenderFragment<Row> RowTemplate() => row => builder =>
    {
        builder.OpenElement(0, "td");
        builder.AddContent(1, row.Name);
        builder.CloseElement();
        builder.OpenElement(2, "td");
        builder.AddContent(3, row.Port);
        builder.CloseElement();
    };
}
