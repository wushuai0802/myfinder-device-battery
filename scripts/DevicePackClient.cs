using System;
using System.IO;
using System.Threading;

namespace AtkBattery {
    internal static class DevicePackNetwork {
        internal static void Validate(Uri uri,string source,string file,bool redirect){
            if(uri==null||!uri.IsAbsoluteUri||uri.Scheme!="https"||uri.Port!=443||uri.UserInfo.Length!=0||uri.Fragment.Length!=0)throw new InvalidDataException("pack_url");
            if(source!=DevicePackTrust.Mirror&&source!=DevicePackTrust.GitHub)throw new InvalidDataException("pack_source");
            if(uri.AbsoluteUri==source+file)return;
            // A mirror cannot redirect into arbitrary paths or into GitHub.
            // GitHub asset redirects use the updater's existing download hosts,
            // but this allowlist remains separate from program updates.
            if(redirect&&source==DevicePackTrust.GitHub&&(uri.IdnHost=="objects.githubusercontent.com"||uri.IdnHost=="release-assets.githubusercontent.com"))return;
            throw new InvalidDataException("pack_redirect");
        }
        internal static byte[] Fetch(IUpdateTransport transport,string source,string file,int limit,CancellationToken cancellation){
            Uri uri=new Uri(source+file);
            for(int redirects=0;;redirects++){
                Validate(uri,source,file,redirects!=0);
                using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation)){
                    timeout.CancelAfter(15000);var token=timeout.Token;
                    using(var response=transport.Get(uri,token)){
                        token.ThrowIfCancellationRequested();
                        if(response.Status==301||response.Status==302||response.Status==303||response.Status==307||response.Status==308){
                            Uri next;if(redirects>=5||String.IsNullOrEmpty(response.Location)||!Uri.TryCreate(uri,response.Location,out next))throw new InvalidDataException("pack_redirect");
                            Validate(next,source,file,true);uri=next;continue;
                        }
                        if(response.Status!=200)throw new InvalidDataException("pack_http_"+response.Status);
                        if(response.Length>limit)throw new InvalidDataException("pack_download_size");
                        using(var memory=new MemoryStream()){
                            byte[] buffer=new byte[8192];int read;
                            while(true){token.ThrowIfCancellationRequested();read=response.Body.Read(buffer,0,buffer.Length);token.ThrowIfCancellationRequested();if(read==0)break;
                                if(memory.Length+read>limit)throw new InvalidDataException("pack_download_size");memory.Write(buffer,0,read);}
                            if(response.Length>=0&&memory.Length!=response.Length)throw new InvalidDataException("pack_download_truncated");return memory.ToArray();
                        }
                    }
                }
            }
        }
    }
    internal sealed class DevicePackController:IDisposable {
        readonly DevicePackManager manager;readonly Timer timer;readonly CancellationTokenSource cancel=new CancellationTokenSource();int disposed;
        internal DevicePackController(DevicePackManager value){manager=value;timer=new Timer(delegate{Check(true);},null,20000,60000);}
        internal void Check(bool automatic){if(Volatile.Read(ref disposed)!=0)return;ThreadPool.QueueUserWorkItem(delegate{if(Volatile.Read(ref disposed)==0)manager.Check(automatic,DateTime.UtcNow,cancel.Token);});}
        public void Dispose(){if(Interlocked.Exchange(ref disposed,1)==0){timer.Dispose();cancel.Cancel();}}
    }
}
