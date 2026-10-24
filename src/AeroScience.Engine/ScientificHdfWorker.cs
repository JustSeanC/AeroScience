using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using AeroScience.Missions;
using AeroScience.Hdf;
using PureHDF;
using PureHDF.Selections;
using PureHDF.VOL.Native;

namespace AeroScience.Engine;

// No scientific/HTTP host, database, imports or providers are started in this mode.
// Untrusted HDF parsing is private to this entry point after OS resource isolation.
public static class ScientificHdfWorker
{
    private static readonly Lazy<Stream> Output=new(Console.OpenStandardOutput);
    public const string Command="--scientific-hdf-reader-v1";
    public static bool IsCommand(string[] args)=>args.Length==1&&args[0]==Command;
    public static int Run()
    {
        try
        {
            using var boundary=ScientificHdfBoundary.Enter();
            if(typeof(H5File).Assembly.GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>().SingleOrDefault(a=>a.Key=="AeroScienceHdfSourcePolicy")?.Value!="2")throw new ScientificHdfException("IsolationUnavailable","Unrestricted or research-only HDF assembly cannot replace the production policy.");
            new Thread(()=>{Thread.Sleep(TimeSpan.FromSeconds(630));Environment.FailFast("Scientific reader absolute deadline exceeded.");}){IsBackground=true}.Start();
            if(!int.TryParse(Environment.GetEnvironmentVariable("AEROSCIENCE_HDF_PARENT_PID"),out var owner)||owner<=0||owner==Environment.ProcessId)throw new ScientificHdfException("IsolationUnavailable","Reader owner identity missing.");
            new Thread(()=>{try{using var parent=Process.GetProcessById(owner);parent.WaitForExit();Environment.FailFast("Scientific reader owner exited.");}catch(ArgumentException){Environment.FailFast("Scientific reader owner unavailable.");}}){IsBackground=true}.Start();
            using var stdin=new BufferedStream(Console.OpenStandardInput(),65536);
            var request=JsonSerializer.Deserialize<HdfReadRequest>(ScientificHdfReader.Line(stdin,ScientificHdfReader.MaximumRequest,CancellationToken.None).GetAwaiter().GetResult(),ScientificHdfReader.Json)??throw new ArgumentException("Missing request.");
            ScientificHdfReader.Validate(request);var timer=Stopwatch.StartNew();var original=request.Original;
            var path=OriginalPath(original);var stamp=File.GetLastWriteTimeUtc(path);
            using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.RandomAccess);
            Verify(file,original);Send(new("verified"));
            using var bounded=new HdfInput(file);HdfWindowResult[] windows;Dictionary<string,HdfAttributeValue> attributes;
            using(var hdf=H5File.Open(bounded,leaveOpen:true))
            {
                attributes=Attributes(hdf,request.RootAttributes);long total=0;var results=new List<HdfWindowResult>();
                foreach(var item in request.Windows)
                {
                    var dataset=(NativeDataset)hdf.Dataset(item.Dataset);var dims=Dimensions(dataset.Space.Dimensions);var isString=ScientificHdfNumeric.IsString(dataset);var type=isString?"String":ScientificHdfNumeric.Describe(dataset).Type;var chunks=dataset.Layout.Class==H5DataLayoutClass.Chunked?dataset.Layout.Chunks:[];
                    if(dataset.Layout.Class is not(H5DataLayoutClass.Chunked or H5DataLayoutClass.Compact or H5DataLayoutClass.Contiguous))throw new ScientificHdfException("ExternalStorage","Unsupported HDF storage.");
                    if(chunks.Length>0&&Size(chunks,dataset.Type.Size)>ScientificHdfReader.MaximumChunk)throw new ScientificHdfException("Limit","Decoded chunk exceeds its bound.");
                    byte[]? data=null;string[]? strings=null;
                    if(item.Count is {} count){if(count.Length!=dims.Length||count.Select((n,i)=>item.Start![i]>=dims[i]||n>dims[i]-item.Start[i]).Any(v=>v))throw new ArgumentException("Numeric window is outside dataset bounds.");
                        if(isString){if(dims.Length!=1||dims[0]>128||item.Start![0]!=0||count[0]!=dims[0])throw new ScientificHdfException("Limit","Read only a bounded whole string-label vector.");strings=BoundedStrings(dataset.Read<string[]>());total=checked(total+strings.Sum(s=>s.Length*4L));}
                        else{var size=Size(count,dataset.Type.Size);total=checked(total+size);if(total>ScientificHdfReader.MaximumValues)throw new ScientificHdfException("Limit","Numeric response exceeds its bound.");data=Values(dataset,type,item.Start!,count);}}
                    if(total>ScientificHdfReader.MaximumValues)throw new ScientificHdfException("Limit","Reader response exceeds its bound.");
                    results.Add(new(item.Dataset,type,dims,dataset.Layout.Class.ToString(),chunks,item.Start,item.Count,data,Attributes(dataset,item.Attributes),strings));
                }
                windows=results.ToArray();
            }
            Send(new("rechecking"));
            if(path!=OriginalPath(original)||File.GetLastWriteTimeUtc(path)!=stamp)throw new ScientificHdfException("Integrity","Original identity changed.");Verify(file,original);
            var result=new HdfReadResult(1,original.LibraryId,original.RelativePath,original.Sha256,original.Bytes,windows,attributes,
                new(boundary.Kind,ScientificHdfReader.ProcessBytes,ScientificHdfReader.HeapBytes,OperatingSystem.IsLinux()?ScientificHdfReader.AddressBytes:0,bounded.ReadBytes,timer.ElapsedMilliseconds,Process.GetCurrentProcess().WorkingSet64),
                "Original numeric bits in canonical little-endian order; bounded native hyperslabs. No radiometric, QA, geolocation, NASA-format or measurement-accuracy inference.");
            Send(new("complete",result));return 0;
        }
        catch(Exception error)when(error is not OutOfMemoryException and not StackOverflowException)
        {
            var failure=error.GetBaseException();
            var code=failure is ScientificHdfException known?known.Code:failure is NotSupportedException&&new[]{"AeroScience rejects decompressed HDF chunks above 4 MiB.","AeroScience rejects decompressed HDF chunks above their declared size."}.Contains(failure.Message)?"Limit":failure is NotSupportedException&&new[]{"AeroScience rejects external HDF links before resolution.","AeroScience rejects symbolic HDF links before resolution.","AeroScience rejects external HDF raw storage before resolution.","AeroScience rejects HDF virtual storage before resolution."}.Contains(failure.Message)?"ExternalStorage":failure is UnauthorizedAccessException?"Ownership":"Format";
            Send(new("error",Code:code));return 2;
        }
    }
    private static void Send(HdfFrame frame){var data=JsonSerializer.SerializeToUtf8Bytes(frame,ScientificHdfReader.Json);if(data.Length>ScientificHdfReader.MaximumReply)throw new ScientificHdfException("Limit","Output metadata exceeds its bound.");var output=Output.Value;output.Write(data);output.WriteByte(10);output.Flush();}
    private static string OriginalPath(HdfOriginal original)
    {
        try
        {
            var root=Path.GetFullPath(original.RootDirectory);StorageManager.ValidateLocalPath(root);var marker=Path.Combine(root,"library.json");StorageManager.ValidateLocalPath(marker);if(!File.Exists(marker)||new FileInfo(marker).Length>65536||!Directory.Exists(root)||StorageManager.LibraryId(root)!=original.LibraryId)throw new ScientificHdfException("Ownership","Configured Library identity is unavailable.");
            var path=StorageManager.ResolveDataset(root,original.LibraryId,original.RelativePath);StorageManager.ValidateLocalPath(path);
            var info=new FileInfo(path);if(!info.Exists||info.Length!=original.Bytes||(info.Attributes&(FileAttributes.Directory|FileAttributes.ReparsePoint|FileAttributes.Device))!=0)throw new ScientificHdfException("Ownership","Original ownership or size differs.");return path;
        }
        catch(Exception e)when(e is IOException or UnauthorizedAccessException or ArgumentException){throw new ScientificHdfException("Ownership","Approved original path is unavailable or outside its ownership boundary.");}
    }
    private static void Verify(FileStream file,HdfOriginal original){if(file.Length!=original.Bytes)throw new ScientificHdfException("Integrity","Original length changed.");file.Position=0;var actual=Convert.ToHexStringLower(SHA256.HashData(file));if(actual!=original.Sha256)throw new ScientificHdfException("Integrity","Original hash differs.");file.Position=0;}
    private static ulong[] Dimensions(ulong[] dims){if(dims.Length>4||dims.Any(n=>n is <1 or >1048576))throw new ScientificHdfException("Limit","Dataset extent is unsupported.");return dims;}
    private static long Size(ulong[] dims,int bytes){if(bytes is <1 or >8)throw new ScientificHdfException("Limit","Element width is unsupported.");ulong n=(ulong)bytes;foreach(var d in dims){if(d==0||n>(ulong)ScientificHdfReader.MaximumValues/d)throw new ScientificHdfException("Limit","Numeric allocation exceeds its bound.");n*=d;}return(long)n;}
    private static byte[] Bytes<T>(T[] data)where T:unmanaged=>MemoryMarshal.AsBytes(data.AsSpan()).ToArray();
    private static byte[] Values(NativeDataset d,string type,ulong[] start,ulong[] count)
    {
        var access=new H5DatasetAccess(ChunkCache:new SimpleReadingChunkCache(1,(uint)ScientificHdfReader.MaximumChunk));var selection=new HyperslabSelection(start.Length,start,count);
        var encoding=ScientificHdfNumeric.Describe(d);var values=d.Read<byte[]>(access,fileSelection:selection);var width=d.Type.Size;
        if(values.LongLength!=Size(count,width))throw new ScientificHdfException("Integrity","Numeric window byte count differs from its dimensions.");
        if(encoding.BigEndian&&width>1)for(var i=0;i<values.Length;i+=width)values.AsSpan(i,width).Reverse();return values;
    }
    private static Dictionary<string,HdfAttributeValue> Attributes(NativeObject node,string[]? names)
    {
        var result=new Dictionary<string,HdfAttributeValue>();
        foreach(var name in names??[]){if(!node.AttributeExists(name)){result[name]=new("Missing","Unknown",[]);continue;}var attr=node.Attribute(name);var dims=Dimensions(attr.Space.Dimensions);var elements=dims.Aggregate(1UL,(n,d)=>checked(n*d));if(elements>128||attr.Type.Size is <1 or >4096)throw new ScientificHdfException("Limit","Attribute metadata exceeds its bound.");
            if(ScientificHdfNumeric.IsString((NativeAttribute)attr)){if(elements*(ulong)attr.Type.Size>8192)throw new ScientificHdfException("Limit","Attribute string exceeds its bound.");result[name]=new("Original","String",dims,Strings:BoundedStrings(attr.Read<string[]>()));}
            else if(attr.Type.Class is H5DataTypeClass.FloatingPoint or H5DataTypeClass.FixedPoint){var type=ScientificHdfNumeric.Describe((NativeAttribute)attr).Type;var data=type switch{"Float32"=>Bytes(attr.Read<float[]>()),"Float64"=>Bytes(attr.Read<double[]>()),"Int8"=>Bytes(attr.Read<sbyte[]>()),"UInt8"=>attr.Read<byte[]>(),"Int16"=>Bytes(attr.Read<short[]>()),"UInt16"=>Bytes(attr.Read<ushort[]>()),"Int32"=>Bytes(attr.Read<int[]>()),"UInt32"=>Bytes(attr.Read<uint[]>()),"Int64"=>Bytes(attr.Read<long[]>()),"UInt64"=>Bytes(attr.Read<ulong[]>()),_=>throw new NotSupportedException()};result[name]=new("Original",type,dims,data);}
            else result[name]=new("Unsupported",attr.Type.Class.ToString(),dims);
        }
        return result;
    }
    private static string[] BoundedStrings(string[] values)
    {if(values.Length>128||values.Any(s=>s is null||s.Length>4096)||values.Sum(s=>s.Length)>8192)throw new ScientificHdfException("Limit","String metadata exceeds its bound.");return values;}
    private sealed class HdfInput(FileStream source):Stream
    {
        internal long ReadBytes{get;private set;}
        public override bool CanRead=>true;public override bool CanSeek=>true;public override bool CanWrite=>false;
        public override long Length=>source.Length;public override long Position{get=>source.Position;set{if(value<0||value>Length)throw new InvalidDataException("HDF offset is outside the original.");source.Position=value;}}
        public override int Read(byte[] b,int o,int c)=>Read(b.AsSpan(o,c));public override int Read(Span<byte>b){if(b.Length>ScientificHdfReader.MaximumRead-ReadBytes)throw new ScientificHdfException("Limit","HDF input read budget exceeded.");var n=source.Read(b);ReadBytes+=n;return n;}
        public override long Seek(long o,SeekOrigin origin){Position=checked((origin switch{SeekOrigin.Begin=>0,SeekOrigin.Current=>Position,SeekOrigin.End=>Length,_=>throw new ArgumentException()})+o);return Position;}
        public override void Flush(){}public override void SetLength(long v)=>throw new NotSupportedException();public override void Write(byte[]b,int o,int c)=>throw new NotSupportedException();
    }
}
