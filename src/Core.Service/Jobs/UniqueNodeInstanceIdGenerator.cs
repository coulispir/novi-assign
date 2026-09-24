using System;
using System.Threading;
using System.Threading.Tasks;
using Quartz.Extensibility;

namespace Core.Service.Jobs;

/// <summary>
/// Names each Quartz cluster node after its host plus a random suffix, so replicas sharing a hostname
/// (or a restarted node whose previous check-in hasn't expired yet) never collide on the same instance id.
/// </summary>
public class UniqueNodeInstanceIdGenerator : IInstanceIdGenerator
{
    public ValueTask<string> GenerateInstanceId(CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult($"{Environment.MachineName}-{Guid.NewGuid():N}");
    }
}
