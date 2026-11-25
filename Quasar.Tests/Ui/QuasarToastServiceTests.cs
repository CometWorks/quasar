using Microsoft.Extensions.Time.Testing;
using Quasar.Client.Ui.Toasts;
using Quasar.Ui;
using Xunit;

namespace Quasar.Tests.Ui;

public sealed class QuasarToastServiceTests
{
    [Fact]
    public void AddRemoveAndClearUpdateSnapshot()
    {
        using var service = new QuasarToastService(new FakeTimeProvider(), new QuasarToastHostOptions());
        var id = service.Add("Hello", QuasarSeverity.Success);

        var toast = Assert.Single(service.Snapshot);
        Assert.Equal(id, toast.Id);
        Assert.Equal("Hello", toast.Message);
        Assert.Equal(QuasarSeverity.Success, toast.Severity);

        Assert.True(service.Remove(id));
        Assert.Empty(service.Snapshot);
        Assert.False(service.Remove(id));

        service.Add("One");
        service.Add("Two");
        service.Clear();
        Assert.Empty(service.Snapshot);
    }

    [Fact]
    public void VisibleToastsAreCappedToTheOldestRemoved()
    {
        using var service = new QuasarToastService(new FakeTimeProvider(), new QuasarToastHostOptions { MaxVisibleToasts = 2 });
        service.Add("One");
        service.Add("Two");
        service.Add("Three");

        Assert.Equal(["Two", "Three"], service.Snapshot.Select(toast => toast.Message));
    }

    [Fact]
    public async Task TimerRemovesToastUnlessInteractionIsRequired()
    {
        var time = new FakeTimeProvider();
        using var service = new QuasarToastService(time, new QuasarToastHostOptions());
        service.Add("Timed", QuasarSeverity.Info, options => options.Duration = TimeSpan.FromSeconds(5));
        service.Add("Sticky", QuasarSeverity.Warning, options => options.RequireInteraction = true);

        time.Advance(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => !service.Snapshot.Any(toast => toast.Message == "Timed"));

        Assert.DoesNotContain(service.Snapshot, toast => toast.Message == "Timed");
        Assert.Contains(service.Snapshot, toast => toast.Message == "Sticky");
    }

    [Fact]
    public async Task PauseAndResumeKeepTheRemainingTime()
    {
        var time = new FakeTimeProvider();
        using var service = new QuasarToastService(time, new QuasarToastHostOptions());
        var id = service.Add("Paused", QuasarSeverity.Normal, options => options.Duration = TimeSpan.FromSeconds(10));

        service.Pause(id);
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.Single(service.Snapshot);

        service.Resume(id);
        time.Advance(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => service.Snapshot.Count == 0);
        Assert.Empty(service.Snapshot);
    }

    [Fact]
    public async Task RepeatedResumeThenPauseKeepsToastVisible()
    {
        var time = new FakeTimeProvider();
        using var service = new QuasarToastService(time, new QuasarToastHostOptions());
        var id = service.Add("Paused", QuasarSeverity.Normal, options => options.Duration = TimeSpan.FromSeconds(10));

        service.Pause(id);
        service.Resume(id);
        service.Resume(id);
        service.Pause(id);

        time.Advance(TimeSpan.FromSeconds(10));
        await Task.Delay(50);

        Assert.Single(service.Snapshot);
    }

    [Fact]
    public void CapturedOptionsCannotChangeAnOpenToast()
    {
        using var service = new QuasarToastService(new FakeTimeProvider(), new QuasarToastHostOptions());
        QuasarToastOptions? captured = null;
        service.Add("Snapshot", QuasarSeverity.Normal, options =>
        {
            captured = options;
            options.Class = "before";
            options.Duration = TimeSpan.FromMinutes(1);
        });

        captured!.Class = "after";
        captured.Duration = TimeSpan.Zero;

        var toast = Assert.Single(service.Snapshot);
        Assert.Equal("before", toast.Options.Class);
        Assert.Equal(TimeSpan.FromMinutes(1), toast.Options.Duration);
    }

    [Fact]
    public void LongMessageIsTruncated()
    {
        using var service = new QuasarToastService(new FakeTimeProvider(), new QuasarToastHostOptions());

        service.Add(new string('x', QuasarToastService.MaxMessageLength + 100));

        var toast = Assert.Single(service.Snapshot);
        Assert.Equal(QuasarToastService.MaxMessageLength, toast.Message.Length);
        Assert.EndsWith("…", toast.Message);
    }

    [Fact]
    public void DurationIsClamped()
    {
        using var service = new QuasarToastService(new FakeTimeProvider(), new QuasarToastHostOptions());

        service.Add("Long", QuasarSeverity.Normal, options => options.Duration = TimeSpan.FromDays(100));

        var toast = Assert.Single(service.Snapshot);
        Assert.Equal(QuasarToastService.MaxTimerDuration, toast.Options.Duration);
    }

    [Fact]
    public void MissingMessageIsRejected()
    {
        using var service = new QuasarToastService(new FakeTimeProvider(), new QuasarToastHostOptions());

        Assert.Throws<ArgumentException>(() => service.Add(" "));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
            await Task.Delay(10);

        Assert.True(condition(), "Condition was not met within one second.");
    }
}
