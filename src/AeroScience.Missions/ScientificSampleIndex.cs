using Microsoft.Data.Sqlite;
namespace AeroScience.Missions;

// Disposable, capacity-reserved analysis index. This is never a mission/science history database.
// The 16 MiB SQLite page cache and tile-keyed reads bound RAM independently of mission duration.
internal sealed class ScientificSampleIndex : IDisposable
{
    private readonly SqliteConnection db;
    private readonly SqliteCommand insert;
    private readonly long maximumBytes;
    private readonly string path;
    public ScientificSampleIndex(AnalysisScratch scratch,string selection)
    {
        path=scratch.Path;maximumBytes=scratch.MaximumBytes;
        db=new(new SqliteConnectionStringBuilder{DataSource=path,Mode=SqliteOpenMode.ReadWriteCreate,Pooling=false}.ToString());
        try
        {
        db.Open();
        using var create=db.CreateCommand();
        // Loss of this explicitly disposable scratch index is recoverable from immutable inputs.
        // Persistent MissionStore/ScienceStore synchronous=FULL and transaction behavior are unchanged.
        create.CommandText="PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF; PRAGMA cache_size=-16384; CREATE TABLE samples(tile INTEGER NOT NULL,cell INTEGER NOT NULL,v0 REAL,v1 REAL,v2 REAL,v3 REAL,PRIMARY KEY(tile,cell)) WITHOUT ROWID";create.ExecuteNonQuery();
        insert=db.CreateCommand();insert.CommandText=selection=="first"?"INSERT OR IGNORE INTO samples VALUES($tile,$cell,$v0,$v1,$v2,$v3)":"INSERT INTO samples VALUES($tile,$cell,$v0,$v1,$v2,$v3) ON CONFLICT(tile,cell) DO UPDATE SET v0=excluded.v0,v1=excluded.v1,v2=excluded.v2,v3=excluded.v3";
        insert.Parameters.Add("$tile",SqliteType.Integer);insert.Parameters.Add("$cell",SqliteType.Integer);for(var b=0;b<4;b++)insert.Parameters.Add("$v"+b,SqliteType.Real);
        }
        catch{db.Dispose();throw;}
    }
    public SqliteTransaction Begin(){var transaction=db.BeginTransaction();insert.Transaction=transaction;return transaction;}
    public void Put(int column,int row,int fullWidth,double?[] values)
    {
        insert.Parameters["$tile"].Value=(row/512)*((fullWidth+511)/512)+column/512;insert.Parameters["$cell"].Value=(row%512)*512+column%512;
        for(var b=0;b<4;b++)insert.Parameters["$v"+b].Value=b<values.Length&&values[b] is {} n?n:DBNull.Value;insert.ExecuteNonQuery();
    }
    public void VerifySize(){if(new FileInfo(path).Length>maximumBytes)throw new IOException("Scientific working index exceeded its reservation; the operation stopped without modifying originals.");}
    public void Fill(int tile,int width,double?[][] values,CancellationToken token)
    {
        using var cmd=db.CreateCommand();cmd.CommandText="SELECT cell,v0,v1,v2,v3 FROM samples WHERE tile=$tile ORDER BY cell";cmd.Parameters.AddWithValue("$tile",tile);using var rows=cmd.ExecuteReader();
        while(rows.Read()){token.ThrowIfCancellationRequested();var cell=rows.GetInt32(0);var index=(cell/512)*width+cell%512;for(var b=0;b<values.Length;b++)values[b][index]=rows.IsDBNull(b+1)?null:rows.GetDouble(b+1);}
    }
    public void Dispose(){insert.Dispose();db.Dispose();}
}
