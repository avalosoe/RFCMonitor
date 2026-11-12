using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using MonitorRFC.Contracts;

namespace MonitorRFC.Infrastructure.Sources;

public sealed record Download(string Path, string Hash, string Url, string? ETag, DateTimeOffset? LastModified, long Bytes);
public sealed class SafeDownloader : IDisposable
{
    private readonly HttpClient client;
    public SafeDownloader()
    {
        var handler=new SocketsHttpHandler {AllowAutoRedirect=false, AutomaticDecompression=DecompressionMethods.All, UseProxy=false};
        handler.ConnectCallback=async (context,ct) =>
        {
            var addresses=await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host,ct);
            if (addresses.Length==0 || addresses.Any(a=>!IsPublic(a))) throw new HttpRequestException("Destino de red no permitido.");
            Exception? last=null;
            foreach(var address in addresses)
            {
                var socket=new Socket(address.AddressFamily,SocketType.Stream,ProtocolType.Tcp);
                try { await socket.ConnectAsync(address,context.DnsEndPoint.Port,ct); return new NetworkStream(socket,true); }
                catch(Exception ex) { socket.Dispose(); last=ex; }
            }
            throw new HttpRequestException("No se pudo conectar a la fuente.",last);
        };
        client=new HttpClient(handler) {Timeout=Timeout.InfiniteTimeSpan};
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MonitorRFC/1.0");
    }
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) return IsPublic(address.MapToIPv4());
        if (IPAddress.IsLoopback(address)) return false;
        var b=address.GetAddressBytes();
        if (b.Length==16) return !address.IsIPv6LinkLocal && !address.IsIPv6Multicast && !address.Equals(IPAddress.IPv6Any) && (b[0]&0xfe)!=0xfc && !address.Equals(IPAddress.IPv6None);
        return b[0] is not (0 or 10 or 127) && b[0]<224 && !(b[0]==169&&b[1]==254) &&
            !(b[0]==172&&b[1]>=16&&b[1]<=31) && !(b[0]==192&&b[1]==168) && !(b[0]==100&&b[1]>=64&&b[1]<=127);
    }
    public static void ValidateUri(Uri uri)
    {
        if (uri.Scheme!="https" || uri.Port!=443 || uri.UserInfo.Length>0)
            throw new InvalidDataException("La fuente debe utilizar HTTPS oficial.");
        var allowed=uri.Host switch
        {
            "www.sat.gob.mx" or "sat.gob.mx" => uri.AbsolutePath.StartsWith("/minisitio/DatosAbiertos/",StringComparison.Ordinal),
            "wu1agsprosta001.blob.core.windows.net" => uri.AbsolutePath.StartsWith("/agsc-publicaciones/Datos_abiertos/",StringComparison.Ordinal),
            "sidof.segob.gob.mx" => uri.AbsolutePath.StartsWith("/dof/sidof/",StringComparison.Ordinal) || uri.AbsolutePath.StartsWith("/notas/",StringComparison.Ordinal),
            "www.dof.gob.mx" or "dof.gob.mx" => true,
            _=>false
        };
        if (!allowed) throw new InvalidDataException("Host o ruta de fuente no permitido.");
    }
    public async Task<Download> FetchAsync(string url,AgentOptions options,CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await FetchOnceAsync(url, options, ct); }
            catch (HttpRequestException ex) when (attempt < 2 && (ex.StatusCode is null || ex.StatusCode == HttpStatusCode.TooManyRequests || (int)ex.StatusCode >= 500))
            { await Task.Delay(TimeSpan.FromSeconds(2 << attempt), ct); }
            catch (OperationCanceledException) when (attempt < 2 && !ct.IsCancellationRequested)
            { await Task.Delay(TimeSpan.FromSeconds(2 << attempt), ct); }
        }
    }
    private async Task<Download> FetchOnceAsync(string url,AgentOptions options,CancellationToken ct)
    {
        var uri=new Uri(url);
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.HttpTimeoutSeconds));
        var dir=System.IO.Path.Combine(options.DataDirectory,"evidence");
        Directory.CreateDirectory(dir);
        for(var redirect=0;redirect<5;redirect++)
        {
            ValidateUri(uri);
            using var request=new HttpRequestMessage(HttpMethod.Get,uri);
            using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
            if ((int)response.StatusCode is >=300 and <400)
            {
                uri=new Uri(uri,response.Headers.Location ?? throw new InvalidDataException("Redirección sin destino."));
                continue;
            }
            response.EnsureSuccessStatusCode();
            if(response.Content.Headers.ContentLength>options.MaxDownloadBytes) throw new InvalidDataException("Descarga excede el límite.");
            var temp=System.IO.Path.Combine(dir,Guid.NewGuid().ToString("N")+".part");
            try
            {
                using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long total=0;
                await using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,true))
                await using(var input=await response.Content.ReadAsStreamAsync(timeout.Token))
                {
                    var buffer=new byte[65536];
                    int count;
                    while((count=await input.ReadAsync(buffer,timeout.Token))>0)
                    {
                        total+=count;
                        if(total>options.MaxDownloadBytes) throw new InvalidDataException("Descarga excede el límite.");
                        hash.AppendData(buffer,0,count);
                        await output.WriteAsync(buffer.AsMemory(0,count),timeout.Token);
                    }
                    await output.FlushAsync(timeout.Token);
                }
                var digest=Convert.ToHexString(hash.GetHashAndReset());
                var target=System.IO.Path.Combine(dir,digest+".bin");
                if(File.Exists(target)) File.Delete(temp); else File.Move(temp,target);
                var result = new Download(target,digest,uri.ToString(),response.Headers.ETag?.Tag,response.Content.Headers.LastModified,total);
                var urlKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..16];
                Configuration.AgentConfiguration.AtomicWrite(target + "." + urlKey + ".json",
                    System.Text.Json.JsonSerializer.Serialize(new { download = result, originalUrl = url, detected = DateTimeOffset.Now }, Configuration.AgentConfiguration.Json));
                return result;
            }
            finally { if(File.Exists(temp)) File.Delete(temp); }
        }
        throw new InvalidDataException("Demasiadas redirecciones.");
    }
    public async Task<string> TextAsync(string url,AgentOptions options,CancellationToken ct)
    {
        var result=await FetchAsync(url,options,ct);
        return await File.ReadAllTextAsync(result.Path,Encoding.UTF8,ct);
    }
    public void Dispose()=>client.Dispose();
}
