using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using AtkBattery;
using AtkBatteryTray;

static class FeedbackAgreementTests {
    static int checks;
    static void Check(bool passed, string message) {
        if (!passed) throw new Exception(message);
        checks++;
    }
    static T Find<T>(Control parent, string name) where T:Control {
        return (T)parent.Controls.Find(name,true)[0];
    }
    static FeedbackReport Report() {
        return new FeedbackReport("fixture","fixture",new WirelessDevice[0],new SuppressedDevice[0],
            new UnadaptedDevice[0],new ReaderHealth[0],new string[0]);
    }
    sealed class FakeHttp:IFeedbackTransport {
        internal volatile byte[] Body;
        public UpdateResponse Post(Uri uri, byte[] body, CancellationToken token) {
            Body=body;
            var response=Encoding.UTF8.GetBytes("{\"number\":42,\"url\":\"https://github.com/wushuai0802/myfinder-device-battery/issues/42\"}");
            return new UpdateResponse(201,new MemoryStream(response),response.Length,null,null);
        }
    }
    static FeedbackForm Form(string relay, FakeHttp http, Action<string> copy) {
        return new FeedbackForm(Report(),false,false,copy,delegate{},relay,http,new FeedbackSession(),delegate{return true;});
    }
    static void Show(FeedbackForm form) {
        form.Opacity=0;
        form.Show();
        Application.DoEvents();
    }

    [STAThread] static int Main() {
        try {
            FeedbackDraft.Clear();
            const string description="配件的无线电量没有显示";
            const string answer=FeedbackInput.ComparisonQuestion;
            Check(FeedbackInput.WithComparison(description,0,1)==description,"Unanswered leaves the text as entered.");
            Check(FeedbackInput.WithComparison(description,1,1)==description+"\r\n"+answer+"一致","Agreement has the expected label.");
            Check(FeedbackInput.WithComparison(description,3,1)==description+"\r\n"+answer+"官方读不到","Unavailable official data is explicit.");

            string copied=null;
            using(var f=Form("",new FakeHttp(),x=>copied=x)){
                Show(f);
                var box=Find<TextBox>(f,"Description");
                var choice=Find<ComboBox>(f,"BatteryComparison");
                var delta=Find<TextBox>(f,"BatteryComparisonDifference");
                var submit=Find<Button>(f,"SubmitFeedback");
                var status=Find<Label>(f,"FeedbackStatus");
                Check(choice.SelectedIndex==0&&!delta.Visible,"Comparison is optional and hides the unused input.");
                choice.SelectedIndex=2;
                Application.DoEvents();
                submit.PerformClick();
                Check(delta.Visible&&submit.Enabled&&status.Text=="描述至少 5 个字，还差 5 个字"&&copied==null,"Selecting a comparison still requires a description.");
                box.Text=description;
                submit.PerformClick();
                Check(status.Text=="相差请输入 1–100 的整数。"&&copied==null,"A difference must be entered explicitly.");
                delta.Text="101";
                submit.PerformClick();
                Check(status.Text=="相差请输入 1–100 的整数。"&&copied==null,"Out-of-range differences are rejected.");
                delta.Text="7";
                box.Text=new string('x',2000);
                submit.PerformClick();
                Check(status.Text=="描述最多 2000 个字，请缩短后再提交。"&&copied==null,"The appended comparison obeys the existing payload limit.");
                box.Text=description;
                submit.PerformClick();
                Check(copied!=null&&copied.Contains(answer+"相差 7%"),"The browser fallback carries the selected comparison.");
                f.Close();
            }
            using(var f=Form("",new FakeHttp(),null)){
                Check(Find<ComboBox>(f,"BatteryComparison").SelectedIndex==2&&
                    Find<TextBox>(f,"BatteryComparisonDifference").Text=="7","The draft restores the optional answer.");
            }
            FeedbackDraft.Clear();
            var http=new FakeHttp();
            using(var f=Form("https://feedback.invalid/service",http,null)){
                Show(f);
                Find<TextBox>(f,"Description").Text=description;
                Find<ComboBox>(f,"BatteryComparison").SelectedIndex=1;
                Find<Button>(f,"SubmitFeedback").PerformClick();
                var until=DateTime.UtcNow.AddSeconds(5);
                while(Find<Button>(f,"SubmitFeedback").Text!="关闭"&&DateTime.UtcNow<until){
                    Application.DoEvents();
                    Thread.Sleep(3);
                }
                var body=http.Body;
                Check(body!=null&&UpdateJson.Read<FeedbackPayload>(body).Description==description+"\r\n"+answer+"一致",
                    "Only the selected answer is appended to the in-app submission.");
                f.Close();
            }
            Console.WriteLine("PASS "+checks+" feedback comparison checks; fake browser and HTTP");
            return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
        finally{FeedbackDraft.Clear();}
    }
}
