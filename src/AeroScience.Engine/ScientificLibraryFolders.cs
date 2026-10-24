using AeroScience.Missions;

namespace AeroScience.Engine;

public interface IScientificFolderOpener
{
    void OpenScientificFolder(Guid libraryId,Guid? sourceId);
}
public sealed record ScientificFolderRequest(Guid LibraryId,Guid? SourceId=null);
public sealed record ScientificFolderLocation(Guid? LibraryId,string Path,long? UsedBytes,long? FreeBytes,bool Available,bool CanOpen,string? Error,string Accounting);
public sealed class ScientificLibraryFolders(ScienceStore science,StorageManagement storage,IScientificFolderOpener? opener=null)
{
    public ScientificFolderLocation Location(bool local,Guid? sourceId=null)
    {
        var path=science.DirectoryPath;
        try
        {
            var snapshot=storage.Snapshot().Science;var id=science.LibraryId;
            path=science.ScientificFolder(id,sourceId);
            return new(id,path,snapshot.UsedBytes,snapshot.FreeBytes,snapshot.Available,local&&opener is not null&&snapshot.Available,snapshot.Error,snapshot.Accounting);
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {return new(null,path,null,null,false,false,error.Message,"Actual configured Library; no replacement directory was created.");}
    }
    public void Open(bool local,ScientificFolderRequest request)
    {
        if(!local)throw new UnauthorizedAccessException("Paired devices can copy the storage path; they cannot open File Explorer on the companion PC.");
        if(opener is null)throw new InvalidOperationException("Open the Windows companion to launch File Explorer. Copy Path remains available.");
        _=science.ScientificFolder(request.LibraryId,request.SourceId);
        opener.OpenScientificFolder(request.LibraryId,request.SourceId);
    }
}
