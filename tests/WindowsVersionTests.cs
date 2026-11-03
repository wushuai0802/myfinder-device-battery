using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using AtkBattery;
using AtkBatteryTray;

// Version sources are injected; controller readers are offline fixtures.
static class WindowsVersionTests {
    const BindingFlags Static=BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public;
    static int checks,failures;
    static void Check(bool value,string name){if(!value)throw new Exception(name);checks++;}
    static void Run(string name,Action test){try{test();Console.WriteLine("PASS "+name);}catch(Exception error){failures++;Console.Error.WriteLine("FAIL "+name+": "+(error.InnerException??error).Message);}}
    static Type VersionType(){var type=typeof(StartupDiagnostics).Assembly.GetType("AtkBattery.WindowsVersion");Check(type!=null,"real Windows version reader exists");return type;}
    static string Read(Func<Version> native,Func<string,object> registry,Func<Version> fallback){
        return (string)VersionType().GetMethod("Read",Static,null,new[]{typeof(Func<Version>),typeof(Func<string,object>),typeof(Func<Version>)},null).Invoke(null,new object[]{native,registry,fallback});
    }
    static Version UnexpectedFallback(){throw new Exception("Environment.OSVersion must be the last resort");}
    static Dictionary<string,object> Registry(){return new Dictionary<string,object>{{"CurrentMajorVersionNumber",10},{"CurrentMinorVersionNumber",0},{"CurrentBuildNumber","26300"},{"UBR",1234}};}
    static Func<string,object> Reader(Dictionary<string,object> values){return delegate(string name){object value;return values.TryGetValue(name,out value)?value:null;};}
    static void NativePriority(){
        int calls=0;
        Check(Read(delegate{return new Version(10,0,26300);},delegate(string name){calls++;Check(name=="UBR","native success reads only the registry revision");return 1234;},UnexpectedFallback)=="10.0.26300.1234","native version plus registry revision");
        Check(calls==1,"native success does not query the registry version triplet");
        Check(Read(delegate{return new Version(10,0,26300);},delegate(string name){return null;},UnexpectedFallback)=="10.0.26300","missing revision is omitted");
        Check(Read(delegate{return new Version(10,0,26300);},delegate(string name){throw new UnauthorizedAccessException();},UnexpectedFallback)=="10.0.26300","registry access failure preserves native version");
        foreach(object revision in new object[]{0,"42",-1,"invalid",Int64.MaxValue}){
            string expected=revision is int&&(int)revision==0?"10.0.26300.0":revision is string&&(string)revision=="42"?"10.0.26300.42":"10.0.26300";
            Check(Read(delegate{return new Version(10,0,26300);},delegate(string name){return revision;},UnexpectedFallback)==expected,"revision format and validation: "+revision);
        }
    }
    static void RegistryFallback(){
        var values=Registry();
        Check(Read(delegate{return null;},Reader(values),UnexpectedFallback)=="10.0.26300.1234","registry supplies version when native call fails");
        Check(Read(delegate{throw new EntryPointNotFoundException();},Reader(values),UnexpectedFallback)=="10.0.26300.1234","unavailable native entry point uses registry");
        Check(Read(delegate{throw new DllNotFoundException();},Reader(values),UnexpectedFallback)=="10.0.26300.1234","unavailable native library uses registry");
        Check(Read(delegate{return new Version(10,0);},Reader(values),UnexpectedFallback)=="10.0.26300.1234","incomplete native result uses registry");
        values.Remove("UBR");Check(Read(delegate{return null;},Reader(values),UnexpectedFallback)=="10.0.26300","registry version without revision has three components");
        values["CurrentMinorVersionNumber"]=2;values["CurrentMajorVersionNumber"]=11;values["CurrentBuildNumber"]="30000";
        Check(Read(delegate{return null;},Reader(values),UnexpectedFallback)=="11.2.30000","registry version components are read rather than hardcoded");
        var previous=Thread.CurrentThread.CurrentCulture;
        try{Thread.CurrentThread.CurrentCulture=new CultureInfo("ar-SA");Check(Read(delegate{return null;},Reader(values),UnexpectedFallback)=="11.2.30000","version format is culture independent");}
        finally{Thread.CurrentThread.CurrentCulture=previous;}
    }
    static void EnvironmentFallback(){
        int fallbacks=0;Func<Version> fallback=delegate{fallbacks++;return new Version(6,2,9200,0);};
        Check(Read(delegate{throw new DllNotFoundException();},delegate(string name){throw new System.Security.SecurityException();},fallback)=="6.2.9200.0"&&fallbacks==1,"all sources failing returns the environment version unchanged once");
        foreach(string missing in new[]{"CurrentMajorVersionNumber","CurrentMinorVersionNumber","CurrentBuildNumber"}){
            var values=Registry();values.Remove(missing);Check(Read(delegate{return null;},Reader(values),fallback)=="6.2.9200.0","missing registry component falls back: "+missing);
        }
        foreach(object invalid in new object[]{"invalid",-1,Int64.MaxValue}){
            var values=Registry();values["CurrentBuildNumber"]=invalid;Check(Read(delegate{return null;},Reader(values),fallback)=="6.2.9200.0","invalid registry build falls back: "+invalid);
        }
    }
    static void Reports(){
        string expected=(string)VersionType().GetMethod("Read",Static,null,Type.EmptyTypes,null).Invoke(null,null);
        var previous=DeviceSettings.Current;DeviceSettings.UseTransientConfiguration(new AudioBindings {RememberReadings=false,ReducedMotion=true});
        var owner=new TrayContext(false,delegate(bool battery){return new MouseSnapshot();},delegate(int stage){throw new Exception("No device writes");});
        try{
            DateTime until=DateTime.UtcNow.AddSeconds(4);while(owner.IsRefreshing&&DateTime.UtcNow<until){Application.DoEvents();Thread.Sleep(2);}Check(!owner.IsRefreshing,"offline controller settles");
            Check(owner.CreateFeedbackReport().FullText.Contains("\r\nWindows "+expected+"\r\n"),"feedback report uses the real Windows version");
            Check(owner.DiagnosticSummary().Contains("\r\nWindows "+expected+"\r\n"),"diagnostic summary uses the same Windows version");
            Console.WriteLine("Live Windows version: "+expected);
        }finally{owner.ExitThread();owner.Dispose();DeviceSettings.UseTransientConfiguration(previous);}
    }
    [STAThread] static int Main(){
        Run("native Windows version priority and revision format",NativePriority);Run("registry Windows version fallback",RegistryFallback);Run("environment Windows version fallback",EnvironmentFallback);Run("Windows version in reports",Reports);
        Console.WriteLine(checks+" checks, "+failures+" failed groups");return failures==0?0:1;
    }
}
