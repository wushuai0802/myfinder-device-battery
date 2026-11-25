using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using AtkBattery;
using AtkBatteryTray;

static class FeedbackImagesTests {
    const string Relay="https://feedback.invalid/service";
    const string Issue="https://github.com/wushuai0802/myfinder-device-battery/issues/42";
    static int checks,failures;

    static void Check(bool value,string message){if(!value)throw new Exception(message);checks++;}
    static void Run(string name,Action action){try{action();Console.WriteLine("PASS "+name);}catch(Exception e){failures++;Console.Error.WriteLine("FAIL "+name+": "+e);}}
    static UpdateResponse Response(int status,string body){var bytes=Encoding.UTF8.GetBytes(body);return new UpdateResponse(status,new MemoryStream(bytes),bytes.Length,null,null);}
    static FeedbackReport Report(){return new FeedbackReport("fixture-version","fixture-windows",new WirelessDevice[0],new SuppressedDevice[0],new UnadaptedDevice[0],new ReaderHealth[0],new string[0]);}

    sealed class FakeHttp:IFeedbackTransport {
        internal Uri Uri;internal byte[] Body;internal int Calls;
        internal Func<Uri,byte[],UpdateResponse> Handler=delegate{return Response(201,"{\"number\":42,\"url\":\""+Issue+"\",\"replyAccess\":true}");};
        public UpdateResponse Post(Uri uri,byte[] body,CancellationToken token){Calls++;Uri=uri;Body=body;return Handler(uri,body);}
    }

    static Bitmap MakeNoisyBitmap(int width,int height){
        var bmp=new Bitmap(width,height,PixelFormat.Format24bppRgb);
        var rng=new Random(12345);
        for(int y=0;y<height;y+=2){
            for(int x=0;x<width;x+=2){
                var color=Color.FromArgb(rng.Next(256),rng.Next(256),rng.Next(256));
                bmp.SetPixel(x,y,color);
                if(x+1<width)bmp.SetPixel(x+1,y,color);
                if(y+1<height)bmp.SetPixel(x,y+1,color);
                if(x+1<width&&y+1<height)bmp.SetPixel(x+1,y+1,color);
            }
        }
        return bmp;
    }

    static void EncoderBoundsAndMetadataStrip(string output){
        // Small clean UI screenshot with injected EXIF tag encodes as lossless PNG with EXIF stripped.
        using(var bmp=new Bitmap(320,200,PixelFormat.Format32bppArgb)){
            using(var g=Graphics.FromImage(bmp)){g.Clear(Color.FromArgb(24,110,200));g.FillRectangle(Brushes.White,20,20,100,40);}
            var exif=(PropertyItem)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(PropertyItem));
            exif.Id=0x010F;exif.Type=2;exif.Value=Encoding.ASCII.GetBytes("SecretCameraModel\0");exif.Len=exif.Value.Length;
            bmp.SetPropertyItem(exif);
            var res=FeedbackImageEncoder.EncodeImage(bmp,"ui.png");
            Check(res.Attachment!=null&&res.Error==null,"small bitmap encodes cleanly");
            using(var att=res.Attachment){
                Check(att.Mime=="image/png","small crisp image stays PNG");
                Check(att.Width==320&&att.Height==200,"dimensions preserved under 1600px");
                Check(att.ByteLength<=FeedbackImageEncoder.MaxEncodedBytes,"within 512 KiB");
                byte[] decoded=Convert.FromBase64String(att.Base64Data);
                Check(decoded[0]==0x89&&decoded[1]==0x50&&decoded[2]==0x4E&&decoded[3]==0x47,"valid PNG magic bytes");
                Check(!Encoding.ASCII.GetString(decoded).Contains("SecretCameraModel"),"EXIF tag bytes stripped from output");
                using(var ms=new MemoryStream(decoded,false))
                using(var reloaded=Image.FromStream(ms,false,true)){
                    Check(Array.IndexOf(reloaded.PropertyIdList,0x010F)<0,"EXIF property ID stripped");
                }
                Check(att.Thumbnail!=null&&att.Thumbnail.Width<=96&&att.Thumbnail.Height<=72,"thumbnail bounded");
            }
        }

        // Large noisy photo downscales to <= 1600px and falls back to JPEG <= 512 KiB.
        using(var huge=MakeNoisyBitmap(2400,1600)){
            var res=FeedbackImageEncoder.EncodeImage(huge,"photo.jpg");
            Check(res.Attachment!=null&&res.Error==null,"oversized noisy photo compresses cleanly");
            using(var att=res.Attachment){
                Check(att.Mime=="image/jpeg","large noisy photo converts to JPEG");
                Check(Math.Max(att.Width,att.Height)<=1600,"long edge clamped to <= 1600px");
                Check(att.ByteLength<=FeedbackImageEncoder.MaxEncodedBytes,"JPEG compressed under 512 KiB: "+att.ByteLength);
                byte[] decoded=Convert.FromBase64String(att.Base64Data);
                Check(decoded[0]==0xFF&&decoded[1]==0xD8&&decoded[2]==0xFF,"valid JPEG magic bytes");
            }
        }

        // Corrupt file fails cleanly without throwing.
        string badPath=Path.Combine(output,"corrupt.png");
        File.WriteAllText(badPath,"not a real png image");
        var badRes=FeedbackImageEncoder.EncodeFile(badPath);
        Check(badRes.Attachment==null&&!String.IsNullOrEmpty(badRes.Error),"corrupt file rejected with Chinese message");
    }

    static void PayloadCompatibilityAndEndpoints(){
        // 0 images -> original /feedback endpoint and exact 6 keys (no "images" key).
        var http0=new FakeHttp();
        var client0=new FeedbackClient(Relay,http0,new FeedbackSession());
        var res0=client0.Submit("ui","纯文字反馈不带图片",null,CancellationToken.None,new List<FeedbackImageAttachment>());
        Check(res0.Kind==FeedbackResultKind.Success,"0-image submit succeeds");
        Check(http0.Uri.AbsoluteUri==Relay+"/feedback","0-image uses legacy /feedback endpoint");
        var fields0=UpdateJson.Read<Dictionary<string,object>>(http0.Body,true);
        Check(!fields0.ContainsKey("images"),"0-image JSON omits images field completely");

        // With 1 image -> /feedback/with-images endpoint and images array present.
        using(var bmp=new Bitmap(120,80)){
            var enc=FeedbackImageEncoder.EncodeImage(bmp,"shot.png");
            using(var att=enc.Attachment){
                var list=new List<FeedbackImageAttachment> {att};
                var http1=new FakeHttp();
                var client1=new FeedbackClient(Relay,http1,new FeedbackSession());
                var res1=client1.Submit("ui","带一张截图的界面反馈",null,CancellationToken.None,list);
                Check(res1.Kind==FeedbackResultKind.Success,"with-images submit succeeds");
                Check(http1.Uri.AbsoluteUri==Relay+"/feedback/with-images","with-images routes to /feedback/with-images");
                var fields1=UpdateJson.Read<Dictionary<string,object>>(http1.Body,true);
                Check(fields1.ContainsKey("images"),"with-images JSON includes images field");

                // Relay returning 503 images_unavailable or 404 (legacy) maps cleanly to ImagesUnavailable.
                var http503=new FakeHttp {Handler=delegate{return Response(503,"{\"error\":\"images_unavailable\"}");}};
                var store503=new FeedbackStore(null);
                var res503=new FeedbackClient(Relay,http503,new FeedbackSession(),20000,store503).Submit("ui","带图但服务端未开 R2",null,CancellationToken.None,list);
                Check(res503.Kind==FeedbackResultKind.Unavailable&&res503.Failure==FeedbackFailure.ImagesUnavailable,"503 images_unavailable mapped");
                Check(store503.Snapshot().Records.Count==0,"503 images_unavailable cancels pending creation placeholder");

                var http404=new FakeHttp {Handler=delegate{return Response(404,"{\"error\":\"invalid\"}");}};
                var store404=new FeedbackStore(null);
                var res404=new FeedbackClient(Relay,http404,new FeedbackSession(),20000,store404).Submit("ui","旧版中继返回 404",null,CancellationToken.None,list);
                Check(res404.Kind==FeedbackResultKind.Unavailable&&res404.Failure==FeedbackFailure.ImagesUnavailable&&http404.Calls==1,"404 on with-images mapped to ImagesUnavailable without unsafe retry");
                Check(store404.Snapshot().Records.Count==0,"404 cancels placeholder");
            }
        }
    }

    static void SupplementWithImages(){
        var store=new FeedbackStore(null);
        store.Change(delegate(FeedbackData d){
            d.Records.Add(new FeedbackRecord {
                Number=42,Url=Issue,Category="ui",Summary="界面问题",
                ReplyKey=FeedbackProtocol.NewKey(),CanSupplement=true,
                CreatedAt=DateTime.UtcNow,LastActivityAt=DateTime.UtcNow,NextCheckAt=DateTime.UtcNow,
            });
        });
        var http=new FakeHttp {Handler=delegate{return Response(201,"{\"number\":42,\"commentId\":\"901\",\"duplicate\":false}");}};
        var replies=new FeedbackReplies(store,http,Relay);
        using(var bmp=new Bitmap(100,60))
        using(var att=FeedbackImageEncoder.EncodeImage(bmp,"supp.png").Attachment){
            var result=replies.Supplement(42,"补充一张截图用于排查",null,CancellationToken.None,new List<FeedbackImageAttachment> {att});
            Check(result.Kind==SupplementKind.Success,"supplement with image succeeds");
            Check(http.Uri.AbsoluteUri==Relay+"/feedback/supplement/with-images","supplement with image routes to /supplement/with-images");
            var fields=UpdateJson.Read<Dictionary<string,object>>(http.Body,true);
            Check(fields.ContainsKey("images"),"supplement JSON includes images");
        }

        // 503 images_unavailable clears Pending so user is not stuck in retry-only mode.
        var httpUnavail=new FakeHttp {Handler=delegate{return Response(503,"{\"error\":\"images_unavailable\"}");}};
        var repliesUnavail=new FeedbackReplies(store,httpUnavail,Relay);
        using(var bmp=new Bitmap(100,60))
        using(var att=FeedbackImageEncoder.EncodeImage(bmp,"supp2.png").Attachment){
            var result=repliesUnavail.Supplement(42,"补充截图未开启",null,CancellationToken.None,new List<FeedbackImageAttachment> {att});
            Check(result.Kind==SupplementKind.ImagesUnavailable,"supplement 503 images_unavailable mapped");
            Check(store.Find(42).Pending==null,"Pending cleared on ImagesUnavailable");
        }
    }

    static void StripControlCapAndFormRender(string output){
        FeedbackDraft.Clear();
        using(var bmp=new Bitmap(160,100)){
            using(var g=Graphics.FromImage(bmp)){g.Clear(Color.SteelBlue);g.FillEllipse(Brushes.Gold,20,20,60,60);}
            using(var form=new FeedbackForm(Report(),false,false,delegate{},delegate{},Relay,new FakeHttp())){
                form.Opacity=0;form.Show();Application.DoEvents();
                var found=form.Controls.Find("FeedbackImages",true);
                Check(found.Length==1,"FeedbackImages strip exists in FeedbackForm");
                var strip=(FeedbackImageStrip)found[0];
                string err;
                Check(strip.TryAddImage(bmp,"img1.png",out err)&&strip.ImageCount==1,"add 1st image");
                Check(strip.TryAddImage(bmp,"img2.png",out err)&&strip.ImageCount==2,"add 2nd image");
                Check(strip.TryAddImage(bmp,"img3.png",out err)&&strip.ImageCount==3,"add 3rd image");
                Check(!strip.TryAddImage(bmp,"img4.png",out err)&&strip.ImageCount==3&&err!=null&&err.Contains("3"),"4th image rejected at cap");
                Application.DoEvents();
                if(output!=null){
                    using(var shot=new Bitmap(form.Width,form.Height)){
                        form.DrawToBitmap(shot,new Rectangle(Point.Empty,shot.Size));
                        shot.Save(Path.Combine(output,"feedback-with-images-light.png"));
                    }
                }
                strip.RemoveAt(1);
                Check(strip.ImageCount==2,"remove middle thumbnail");
                form.Close();
            }
            // Draft preserves attached images when reopened with same entry category.
            Check(FeedbackDraft.HasImages,"closing without submit preserves images in draft");
            using(var reopened=new FeedbackForm(Report(),false,true,delegate{},delegate{},Relay,new FakeHttp())){
                reopened.Opacity=0;reopened.Show();Application.DoEvents();
                var strip=(FeedbackImageStrip)reopened.Controls.Find("FeedbackImages",true)[0];
                Check(strip.ImageCount==2,"reopened FeedbackForm restores 2 draft images");
                if(output!=null){
                    using(var shot=new Bitmap(reopened.Width,reopened.Height)){
                        reopened.DrawToBitmap(shot,new Rectangle(Point.Empty,shot.Size));
                        shot.Save(Path.Combine(output,"feedback-with-images-dark.png"));
                    }
                }
                reopened.Close();
            }
            FeedbackDraft.Clear();
        }
    }

    [STAThread] static int Main(string[] args){
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        string output=args.Length>0?args[0]:Path.Combine(Path.GetTempPath(),"myfinder-feedback-images-tests");
        Directory.CreateDirectory(output);
        Run("encoder bounds and EXIF strip",delegate{EncoderBoundsAndMetadataStrip(output);});
        Run("payload compatibility and endpoints",PayloadCompatibilityAndEndpoints);
        Run("supplement with images",SupplementWithImages);
        Run("image strip cap, draft and form render",delegate{StripControlCapAndFormRender(output);});
        Console.WriteLine(checks+" feedback-image checks, "+failures+" failed groups");
        return failures==0?0:1;
    }
}
