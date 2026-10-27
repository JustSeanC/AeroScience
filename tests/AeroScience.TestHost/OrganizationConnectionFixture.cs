using System.Collections.Concurrent;
using AeroScience.Engine;
namespace AeroScience.TestHost;
// Test executable only: disposable in-memory vault lets real-process browser tests run on Linux.
// Production has no plaintext/in-memory credential fallback.
public sealed class OrganizationConnectionFixture:IOrganizationCredentials
{
    readonly ConcurrentDictionary<Guid,string> values=new();
    public bool Available=>true;
    public string? Read(Guid id)=>values.GetValueOrDefault(id);
    public void Save(Guid id,string value)=>values[id]=value;
    public void Remove(Guid id)=>values.TryRemove(id,out _);
}
