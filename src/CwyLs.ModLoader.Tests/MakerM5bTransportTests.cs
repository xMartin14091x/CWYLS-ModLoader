using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using CwyLs.ModLoader.Core.Api;
using Xunit;

namespace CwyLs.ModLoader.Tests
{
    [Collection(SocketTestCollection.Name)]
    public sealed class MakerM5bTransportTests
    {
        [Fact]
        public void Mono_style_abort_that_sends_default_response_cannot_report_success()
        {
            var abortedStatus=0;
            var run=SocketTestServer.Start(ApiTransportMode.HttpListener,p=>new MakerRig(port:p),
                (_,request)=> { request.ReadBody(65536); return ApiResponse.Error(400,"bad_request","incomplete"); },
                abortResponse: response=> {
                    // Mono's abort sends the response's defaults instead of resetting the connection.
                    Volatile.Write(ref abortedStatus,response.StatusCode);
                    response.Close();
                });
            using var transport=run.Transport;
            using var client=new TcpClient(AddressFamily.InterNetwork) {ReceiveTimeout=20000,SendTimeout=10000};
            client.Connect(IPAddress.Loopback,run.Port);
            using var stream=client.GetStream();
            var request=Encoding.ASCII.GetBytes("PUT /v1/maker/packs/sample.pack/subtitles/th/one HTTP/1.1\r\nHost: 127.0.0.1:"+run.Port+"\r\nContent-Length: 1000\r\n\r\n{");
            stream.Write(request,0,request.Length);
            using var received=new MemoryStream();var buffer=new byte[4096];
            try { int n;while((n=stream.Read(buffer,0,buffer.Length))>0)received.Write(buffer,0,n); } catch(IOException) { }
            SocketTestServer.Until(()=>Volatile.Read(ref abortedStatus)!=0,"deadline never aborted the fake Mono response");
            Assert.Equal(408,abortedStatus);
            var reply=Encoding.UTF8.GetString(received.ToArray());
            Assert.StartsWith("HTTP/1.1 408",reply);
            Assert.Contains("Connection: close",reply,StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Cache-Control: no-store",reply,StringComparison.OrdinalIgnoreCase);
            Assert.Contains("X-Content-Type-Options: nosniff",reply,StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"code\":\"request_timeout\"",reply);
        }

        [Fact]
        public void Tcp_answers_a_fully_stalled_body_with_408_request_timeout_at_the_fixed_deadline()
        {
            var run = SocketTestServer.Start(ApiTransportMode.Tcp, p => new MakerRig(port: p), (rig, request) => rig.Router.Handle(request));
            using var transport = run.Transport;
            using var client = new TcpClient(AddressFamily.InterNetwork) { ReceiveTimeout = 20000, SendTimeout = 10000 };
            var clock = System.Diagnostics.Stopwatch.StartNew();
            client.Connect(IPAddress.Loopback, run.Port);
            using var stream = client.GetStream();
            // A complete head and the start of the body, then nothing at all: no drip keeps the socket busy, so the
            // 2 s idle read timeout would fire long before the 5 s request deadline.
            var request = Encoding.ASCII.GetBytes("PUT /v1/maker/packs/thai-dub/subtitles/th/one HTTP/1.1\r\nHost: 127.0.0.1:" + run.Port
                + "\r\nAuthorization: " + ApiTestKit.AuthValue(run.Rig.Token)
                + "\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: 100\r\n\r\n{\"text\":");
            stream.Write(request, 0, request.Length);
            using var received = new MemoryStream();
            var buffer = new byte[4096];
            try { int n; while ((n = stream.Read(buffer, 0, buffer.Length)) > 0) received.Write(buffer, 0, n); } catch (IOException) { }
            var elapsed = clock.Elapsed;

            var reply = Encoding.UTF8.GetString(received.ToArray());
            Assert.StartsWith("HTTP/1.1 408", reply);
            Assert.Contains("\"code\":\"request_timeout\"", reply);
            Assert.True(elapsed >= TimeSpan.FromSeconds(4), "answered after " + elapsed.TotalSeconds + " s, before the 5 s request deadline");
            lock (run.Rig.Records)
            {
                var record = Assert.Single(run.Rig.Records).Record;
                Assert.Equal(408, record.Status);
                Assert.Equal("request_timeout", record.Error);
            }
        }

        [Fact]
        public void Starting_transport_raises_thread_pool_minimum_workers_to_cover_request_slots_plus_margin()
        {
            ThreadPool.GetMinThreads(out var originalWorkers, out var originalIo);
            try
            {
                ThreadPool.SetMinThreads(4, originalIo);
                var run = SocketTestServer.Start(ApiTransportMode.HttpListener, p => new MakerRig(port: p),
                    (rig, request) => rig.Router.Handle(request));
                using var transport = run.Transport;
                ThreadPool.GetMinThreads(out var raisedWorkers, out _);
                Assert.True(raisedWorkers >= ApiDefaults.MinWorkerThreads,
                    "Expected ThreadPool min workers >= " + ApiDefaults.MinWorkerThreads + ", got " + raisedWorkers);
            }
            finally
            {
                ThreadPool.SetMinThreads(originalWorkers, originalIo);
            }
        }
    }
}
