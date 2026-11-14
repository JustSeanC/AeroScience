using System.Net;
using AeroScience.Missions;
using Xunit;

namespace AeroScience.Tests;

// Controlled HTTP bodies prove accounting/lifecycle only, not online-source acceptance.
public sealed class P08aScientificNetworkTests
{
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> send):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(send(request));
    }
    private sealed class HeldStream(TaskCompletionSource started,Task release):Stream
    {
        private bool sent;
        public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>false;
        public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token=default)
        {
            started.TrySetResult();await release.WaitAsync(token);if(sent)return 0;buffer.Span[0]=123;buffer.Span[1]=125;sent=true;return 2;
        }
        public override int Read(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
        public override void Flush(){}public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();
        public override void SetLength(long value)=>throw new NotSupportedException();public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task SharedCountersMeasureActualRasterAndPublicEptReadersAndReleaseOnCancellation(bool cancelFirst)
    {
        var firstStarted=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var secondStarted=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRelease=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var secondRelease=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http=new ScientificHttp(new Handler(r=>new(HttpStatusCode.OK){Content=new StreamContent(r.RequestUri!.Host=="earth-search.aws.element84.com"?new HeldStream(firstStarted,firstRelease.Task):new HeldStream(secondStarted,secondRelease.Task))}));
        using var cancel=new CancellationTokenSource();var firstBudget=new ScientificDownloadBudget(1048576);var secondBudget=new ScientificDownloadBudget(1048576);
        var first=http.Bytes(new("https://earth-search.aws.element84.com/v1/p08a-fixture"),1048576,firstBudget,cancel.Token);
        var second=http.LidarBytes(new(LidarCatalog.Bucket+"P08a_Fixture/ept.json"),65536,secondBudget,default);
        try
        {
            await Task.WhenAll(firstStarted.Task,secondStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2,http.NetworkActivity().ActiveTransfers);Assert.Equal(0,http.NetworkActivity().Bytes);
            if(cancelFirst){cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>await first);}
            else{firstRelease.TrySetResult();Assert.Equal(new byte[]{123,125},await first);}
            Assert.Equal(1,http.NetworkActivity().ActiveTransfers);Assert.Equal(cancelFirst?0:2,http.NetworkActivity().Bytes);
            secondRelease.TrySetResult();Assert.Equal(new byte[]{123,125},await second);
            Assert.Equal(0,http.NetworkActivity().ActiveTransfers);Assert.Equal(cancelFirst?2:4,http.NetworkActivity().Bytes);
            Assert.Equal(http.NetworkActivity().Bytes,firstBudget.Bytes+secondBudget.Bytes);
        }
        finally{firstRelease.TrySetResult();secondRelease.TrySetResult();}
    }
}
