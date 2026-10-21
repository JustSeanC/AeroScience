using AeroScience.Engine;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
namespace AeroScience.Tests;

// Deferred TestServer disposal can return before the application provider releases
// its exclusive directory. Observe actual ownership, never delete-retry around a writer.
internal static class ApiHostLifetime
{
    internal static async Task Stop(WebApplicationFactory<Program> factory,HttpClient client)
    {
        var services=factory.Services;var owner=services.GetRequiredService<DataDirectoryLease>();var stopped=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration=services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopped.Register(()=>stopped.TrySetResult());
        client.Dispose();await factory.DisposeAsync();await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if(services is IAsyncDisposable provider)await provider.DisposeAsync();
        await owner.Released.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
