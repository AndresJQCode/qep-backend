using System.Data.Common;
using BuildingBlocks.Application;
using Modules.Automations.Application.Engine;

namespace Modules.Automations.UnitTests;

/// <summary>«Not yet» rather than «could not»: the one rule the engine's CRM effect and the Bootstrapper's customer resolver share.</summary>
public sealed class TransientFailuresTests
{
    [Fact]
    public void ACancellationAConflictAndATransientDatabaseFailurePropagate()
    {
        Assert.True(TransientFailures.MustPropagate(new OperationCanceledException()));
        Assert.True(TransientFailures.MustPropagate(new TaskCanceledException()));
        Assert.True(TransientFailures.MustPropagate(new RequestConcurrencyException("concurrency.conflict", "Changed.")));
        Assert.True(TransientFailures.MustPropagate(new FakeDbException(transient: true)));
        // Anywhere in the chain: EF wraps the provider's exception.
        Assert.True(TransientFailures.MustPropagate(new InvalidOperationException("Wrapped.", new FakeDbException(transient: true))));
    }

    [Fact]
    public void AnythingElseDoesNot()
    {
        Assert.False(TransientFailures.MustPropagate(new InvalidOperationException("Broken.")));
        Assert.False(TransientFailures.MustPropagate(new FakeDbException(transient: false)));
        Assert.False(TransientFailures.MustPropagate(new InvalidOperationException("Wrapped.", new FakeDbException(transient: false))));
    }

    private sealed class FakeDbException(bool transient) : DbException("A database failure.")
    {
        public override bool IsTransient => transient;
    }
}
