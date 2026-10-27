using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroScience.Shared;
using Microsoft.EntityFrameworkCore;

namespace AeroScience.Organization;

internal static class OrganizationTime
{
    // PostgreSQL timestamps retain microseconds; return the same exact UTC after restart.
    internal static DateTimeOffset UtcNow=>new(DateTime.UtcNow.Ticks/10*10,TimeSpan.Zero);
}

public sealed class OrganizationConflict(string message):Exception(message);
public sealed class OrganizationNotFound:Exception;

public sealed class OrganizationManagement(OrganizationDatabase db)
{
    public const int PageSize=20;
    static string Hash(object value)=>Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value,WireJson.Options)));
    static string Text(string? value,int maximum,string label,bool required=true)
    {
        var text=value?.Trim()??"";
        if(text.Length>maximum||required&&text.Length==0||text.Any(c=>char.IsControl(c)&&c is not('\n' or '\r' or '\t')))
            throw new ArgumentException($"{label} must contain {(required?"1":"0")}–{maximum} readable characters.");
        return text;
    }
    static void Command(int version,Guid request,int revision,bool create)
    {
        if(version!=1||request==Guid.Empty||revision<0||create&&revision!=0||!create&&revision==0)
            throw new ArgumentException("Use the supported request version, a new request identity and the saved revision. Refresh the record before editing.");
    }
    static (Guid Id,string Name) Actor(ClaimsPrincipal user)=>
        (Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)??throw new InvalidOperationException("Authenticated account identity is missing.")),user.Identity!.Name!);
    public void Audit(Guid actor,string name,string action,Guid resource,int? revision=null)=>db.Audit.Add(new(){ActorId=actor,ActorName=name,Action=action,ResourceId=resource,Revision=revision,TimestampUtc=OrganizationTime.UtcNow});
    internal async Task<T> Mutate<T>(ClaimsPrincipal user,Guid request,object input,Func<Guid,string,Task<T>> change,CancellationToken token)
    {
        var actor=Actor(user);var hash=Hash(input);
        await using var tx=await db.Database.BeginTransactionAsync(token);
        // The same command is serialized across processes. Other commands/records are independent.
        var bytes=SHA256.HashData(Encoding.UTF8.GetBytes(actor.Id.ToString()+request));var key=System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(bytes);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({key})",token);
        var receipt=await db.Receipts.FindAsync([actor.Id,request],token);
        if(receipt is not null){if(receipt.RequestSha256!=hash)throw new OrganizationConflict("That request identity already belongs to a different change. Your draft was not saved.");return JsonSerializer.Deserialize<T>(receipt.ResponseJson,WireJson.Options)!;}
        try{
            var response=await change(actor.Id,actor.Name);
            db.Receipts.Add(new(){ActorId=actor.Id,RequestId=request,RequestSha256=hash,ResponseJson=JsonSerializer.Serialize(response,WireJson.Options)});
            await db.SaveChangesAsync(token);await tx.CommitAsync(token);return response;
        }catch(DbUpdateConcurrencyException){throw new OrganizationConflict("Someone saved a newer revision. Your draft is still here; reload the saved record before trying again.");}
    }
    internal void Revision(Guid id,int revision,string kind,object snapshot,Guid author)
    {
        var json=JsonSerializer.Serialize(snapshot,WireJson.Options);
        db.Revisions.Add(new(){ResourceId=id,Revision=revision,Kind=kind,SnapshotJson=json,Sha256=Hash(snapshot),AuthorId=author,TimestampUtc=OrganizationTime.UtcNow});
    }
    async Task LockResource(Guid? id,CancellationToken token)
    {
        if(id is null)return;
        var key=System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(id.Value.ToByteArray());
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({key})",token);
    }
    static OrganizationProgramView View(ProgramRecord p)=>new(p.Id,p.Revision,p.Name,p.Description,p.CreatedUtc,p.UpdatedUtc,p.AuthorId,p.AuthorName);
    static OrganizationCampaignView View(CampaignRecord c)=>JsonSerializer.Deserialize<OrganizationCampaignView>(c.SnapshotJson,WireJson.Options)!;
    public async Task<OrganizationPage<OrganizationProgramView>> Programs(int offset,CancellationToken token)
    {
        if(offset<0)throw new ArgumentException("Choose a valid program page.");
        var rows=await db.Programs.AsNoTracking().OrderBy(p=>p.CreatedUtc).ThenBy(p=>p.Id).Skip(offset).Take(PageSize+1).ToArrayAsync(token);
        return new(1,rows.Take(PageSize).Select(View).ToArray(),rows.Length>PageSize?checked(offset+PageSize):null);
    }
    public async Task<OrganizationPage<OrganizationCampaignView>> Campaigns(Guid program,int offset,CancellationToken token)
    {
        if(offset<0)throw new ArgumentException("Choose a valid campaign page.");
        if(!await db.Programs.AnyAsync(p=>p.Id==program,token))throw new OrganizationNotFound();
        var rows=await db.Campaigns.AsNoTracking().Where(c=>c.ProgramId==program).OrderBy(c=>c.CreatedUtc).ThenBy(c=>c.Id).Skip(offset).Take(PageSize+1).ToArrayAsync(token);
        return new(1,rows.Take(PageSize).Select(View).ToArray(),rows.Length>PageSize?checked(offset+PageSize):null);
    }
    public async Task<OrganizationProgramView> Program(Guid id,CancellationToken token)=>View(await db.Programs.AsNoTracking().SingleOrDefaultAsync(p=>p.Id==id,token)??throw new OrganizationNotFound());
    public Task<OrganizationProgramView> SaveProgram(Guid? id,OrganizationProgramRequest request,ClaimsPrincipal user,CancellationToken token)
    {
        Command(request.Version,request.RequestId,request.ExpectedRevision,id is null);
        var name=Text(request.Name,120,"Program name");var description=Text(request.Description,4000,"Program description",false);
        return Mutate(user,request.RequestId,new{operation="program",id,request},async(actor,author)=>{
            await LockResource(id,token);
            var now=OrganizationTime.UtcNow;
            var row=id is {} existing?await db.Programs.SingleOrDefaultAsync(p=>p.Id==existing,token)??throw new OrganizationNotFound():new ProgramRecord{Id=Guid.NewGuid(),CreatedUtc=now};
            if(row.Revision!=request.ExpectedRevision)throw new OrganizationConflict("This program has a newer saved revision. Your draft was not overwritten; reload the saved program.");
            row.Revision++;row.Name=name;row.Description=description;row.UpdatedUtc=now;row.AuthorId=actor;row.AuthorName=author;
            if(id is null)db.Programs.Add(row);var view=View(row);Revision(row.Id,row.Revision,"program",view,actor);Audit(actor,author,id is null?"program-created":"program-edited",row.Id,row.Revision);return view;
        },token);
    }
    public Task<OrganizationCampaignView> SaveCampaign(Guid program,Guid? id,OrganizationCampaignRequest request,ClaimsPrincipal user,CancellationToken token)
    {
        Command(request.Version,request.RequestId,request.ExpectedRevision,id is null);
        var name=Text(request.Name,120,"Campaign name");var description=Text(request.Description,4000,"Campaign description",false);var question=Text(request.Objective,4000,"Research question or test objective",false);
        if(!Enum.IsDefined(request.Focus)||request.Focus==OrganizationFocus.Unknown||request.Template is null)throw new ArgumentException("Choose a supported campaign focus and research approach.");
        var definition=OrganizationTemplates.Resolve(request.Template);
        return Mutate(user,request.RequestId,new{operation="campaign",program,id,request},async(actor,author)=>{
            await LockResource(id,token);
            if(!await db.Programs.AnyAsync(p=>p.Id==program,token))throw new OrganizationNotFound();
            var now=OrganizationTime.UtcNow;
            var row=id is {} existing?await db.Campaigns.SingleOrDefaultAsync(c=>c.Id==existing&&c.ProgramId==program,token)??throw new OrganizationNotFound():new CampaignRecord{Id=Guid.NewGuid(),ProgramId=program,CreatedUtc=now};
            if(row.Revision!=request.ExpectedRevision)throw new OrganizationConflict("This campaign has a newer saved revision. Your draft remains unchanged; reload the saved campaign.");
            row.Revision++;
            var view=new OrganizationCampaignView(row.Id,program,row.Revision,name,description,question,request.Focus,OrganizationPublication.Draft,row.PublishedRevision,definition,row.CreatedUtc,now,actor,author);
            row.SnapshotJson=JsonSerializer.Serialize(view,WireJson.Options);if(id is null)db.Campaigns.Add(row);
            Revision(row.Id,row.Revision,"campaign",view,actor);Audit(actor,author,id is null?"campaign-created":"campaign-edited",row.Id,row.Revision);return view;
        },token);
    }
    public Task<OrganizationCampaignView> Publish(Guid id,OrganizationPublishRequest request,ClaimsPrincipal user,CancellationToken token)
    {
        Command(request.Version,request.RequestId,request.ExpectedRevision,false);
        return Mutate(user,request.RequestId,new{operation="publish",id,request},async(actor,author)=>{
            await LockResource(id,token);
            var row=await db.Campaigns.SingleOrDefaultAsync(c=>c.Id==id,token)??throw new OrganizationNotFound();
            if(row.Revision!=request.ExpectedRevision)throw new OrganizationConflict("The campaign changed after your publication review. Reload and review its current saved revision.");
            var previous=View(row);
            if(previous.Status!=OrganizationPublication.Draft)throw new OrganizationConflict("This revision is already published. Edit a new draft to make changes.");
            if(string.IsNullOrWhiteSpace(previous.Objective))throw new ArgumentException("Add a research question or test objective before publishing this campaign.");
            var definition=OrganizationTemplates.Resolve(previous.Definition.Template);
            if(Hash(definition)!=Hash(previous.Definition))throw new OrganizationConflict("The saved research approach differs from the current supported definition. Save a reviewed draft before publishing.");
            row.Revision++;row.PublishedRevision=row.Revision;
            var view=previous with{Revision=row.Revision,PublishedRevision=row.Revision,Status=OrganizationPublication.Published,UpdatedUtc=OrganizationTime.UtcNow,AuthorId=actor,AuthorName=author};
            row.SnapshotJson=JsonSerializer.Serialize(view,WireJson.Options);Revision(row.Id,row.Revision,"campaign",view,actor);Audit(actor,author,"campaign-published",row.Id,row.Revision);return view;
        },token);
    }
    public async Task<DefinitionRevision> ReadRevision(Guid id,int revision,CancellationToken token)
    {
        if(revision<1)throw new ArgumentException("Choose a saved revision.");
        var row=await db.Revisions.AsNoTracking().SingleOrDefaultAsync(r=>r.ResourceId==id&&r.Revision==revision,token)??throw new OrganizationNotFound();
        using var snapshot=JsonDocument.Parse(row.SnapshotJson);
        if(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(row.SnapshotJson)))!=row.Sha256)throw new InvalidOperationException("Original organization revision integrity could not be verified.");
        return row;
    }
}
