using Hangfire;
using Hangfire.InMemory;
using Hangfire.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AsyncScheduler.Tests;

public class ConfiguredJobsReconcileTests
{
    private static IConfiguration Config(params (string id, string cron)[] jobs) =>
        new ConfigurationBuilder().AddInMemoryCollection(jobs.SelectMany((j, i) => new[]
        {
            KeyValuePair.Create<string, string?>($"Jobs:{i}:Id", j.id),
            KeyValuePair.Create<string, string?>($"Jobs:{i}:Cron", j.cron),
            KeyValuePair.Create<string, string?>($"Jobs:{i}:Url", "http://x/" + j.id),
        })).Build();

    private static string[] Ids(JobStorage s)
    {
        using var c = s.GetConnection();
        return c.GetRecurringJobs().Select(j => j.Id).OrderBy(x => x).ToArray();
    }

    private static void Apply(JobStorage s, IConfiguration cfg, bool reconcile = true) =>
        ConfiguredJobs.Apply(cfg, new RecurringJobManager(s), s, NullLogger.Instance, reconcile);

    [Fact]
    public void A_job_removed_from_config_is_removed_from_the_store_on_next_start()
    {
        var storage = new InMemoryStorage();
        Apply(storage, Config(("keep", "* * * * *"), ("drop", "* * * * *")));
        Assert.Equal(["drop", "keep"], Ids(storage));

        Apply(storage, Config(("keep", "* * * * *")));
        Assert.Equal(["keep"], Ids(storage));
    }

    [Fact]
    public void Jobs_created_through_the_api_are_never_reconciled_away()
    {
        var storage = new InMemoryStorage();
        var manager = new RecurringJobManager(storage);
        manager.AddOrUpdate<HttpCallbackSender>("from-api", s => s.Send(new ApiCall(null, "http://x", new(), "GET"), default), "* * * * *");

        Apply(storage, Config(("cfg", "* * * * *")));
        Apply(storage, Config());   // config now empty

        Assert.Equal(["from-api"], Ids(storage));
    }

    [Fact]
    public void A_changed_cron_is_upserted_not_duplicated()
    {
        var storage = new InMemoryStorage();
        Apply(storage, Config(("j", "* * * * *")));
        Apply(storage, Config(("j", "0 * * * *")));

        using var c = storage.GetConnection();
        var job = Assert.Single(c.GetRecurringJobs());
        Assert.Equal("0 * * * *", job.Cron);
    }

    [Fact]
    public void Reconcile_can_be_switched_off()
    {
        var storage = new InMemoryStorage();
        Apply(storage, Config(("a", "* * * * *")));
        Apply(storage, Config(), reconcile: false);
        Assert.Equal(["a"], Ids(storage));
    }
}
