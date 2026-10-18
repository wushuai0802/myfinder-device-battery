using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace AtkBatteryTray {
    internal sealed class ActivityRefreshRequest {
        internal readonly string PhysicalId;
        internal readonly bool IsNativeAtk,Retry;
        internal readonly int Epoch;
        internal ActivityRefreshRequest(string id,bool native,bool retry,int epoch){PhysicalId=id;IsNativeAtk=native;Retry=retry;Epoch=epoch;}
    }
    // UI-thread policy: activity requests real reads; it never changes a device
    // reading, online flag, measurement timestamp, tray preference or DPI queue.
    internal sealed class ActivityRefreshPolicy:IDisposable {
        sealed class Source {
            internal string Id;internal bool Native,Pending,InFlight,Retry,RetryAvailable,Unsupported,SeenMouse,Deferred;
            internal DateTime Due,Cooldown;internal int Failures,Epoch;
        }
        readonly Dictionary<string,Source> sources=new Dictionary<string,Source>(StringComparer.Ordinal);
        int epoch;bool disposed;
        internal long Events,FreshSkipped,Coalesced,CooldownSkipped,UnsupportedSkipped,Reads,Retries,Failures,Deferred;
        static bool Valid(string id){return Regex.IsMatch(id??"",@"^device-[a-f0-9]{24}$");}
        Source Get(string id,bool native){Source state;if(!sources.TryGetValue(id,out state)&&sources.Count<256){state=new Source {Id=id,Native=native,Epoch=epoch};sources.Add(id,state);}return state;}
        internal void ObserveMouse(string id){if(disposed||!Valid(id))return;var state=Get(id,false);if(state!=null){state.SeenMouse=true;state.Unsupported=false;}}
        internal bool Record(string id,bool native,bool fresh,DateTime now){
            if(disposed||!Valid(id))return false;Events++;
            Source state;sources.TryGetValue(id,out state);
            if(fresh){FreshSkipped++;if(state!=null&&!state.InFlight){state.Pending=false;state.RetryAvailable=false;state.Failures=0;}return false;}
            state=state??Get(id,native);if(state==null)return false;
            if(state.Unsupported){UnsupportedSkipped++;return false;}
            if(state.Pending||state.InFlight){Coalesced++;return false;}
            if(now<state.Cooldown){CooldownSkipped++;return false;}
            state.Native=native;state.Pending=true;state.Retry=false;state.RetryAvailable=true;state.Due=now;state.Deferred=false;return true;
        }
        internal ActivityRefreshRequest Take(DateTime now,Func<string,bool,bool> fresh,Func<bool,bool> canStart){
            if(disposed)return null;
            foreach(var state in sources.Values){
                if(!state.Pending||state.InFlight)continue;
                if(fresh(state.Id,state.Native)){state.Pending=false;state.RetryAvailable=false;state.Failures=0;FreshSkipped++;continue;}
                if(state.Due>now)continue;
                if(!canStart(state.Native)){if(!state.Deferred){state.Deferred=true;Deferred++;}continue;}
                state.Pending=false;state.InFlight=true;Reads++;if(state.Retry)Retries++;
                return new ActivityRefreshRequest(state.Id,state.Native,state.Retry,state.Epoch);
            }
            return null;
        }
        internal void Complete(ActivityRefreshRequest request,bool fresh,bool healthyWithoutMouse,DateTime now){
            Source state;if(disposed||request==null||!sources.TryGetValue(request.PhysicalId,out state)||state.Epoch!=request.Epoch)return;
            state.InFlight=false;
            if(fresh){state.Failures=0;state.RetryAvailable=false;state.Cooldown=now.AddSeconds(5);return;}
            if(!state.Native&&healthyWithoutMouse&&!state.SeenMouse){state.Unsupported=true;state.Pending=false;state.RetryAvailable=false;return;}
            Failures++;
            if(state.RetryAvailable){state.RetryAvailable=false;state.Pending=true;state.Retry=true;state.Due=now.AddMilliseconds(1500);state.Deferred=false;return;}
            state.Failures=Math.Min(6,state.Failures+1);state.Cooldown=now.AddSeconds(Math.Min(300,15*Math.Pow(2,state.Failures-1)));state.Pending=false;
        }
        internal DateTime? NextDue(Func<bool,bool> canStart){
            if(disposed)return null;DateTime? result=null;foreach(var state in sources.Values)if(state.Pending&&!state.InFlight&&canStart(state.Native)&&(!result.HasValue||state.Due<result.Value))result=state.Due;return result;
        }
        internal int Pending {get{int count=0;foreach(var state in sources.Values)if(state.Pending||state.InFlight)count++;return count;}}
        internal void TopologyChanged(){if(disposed)return;epoch++;sources.Clear();}
        public void Dispose(){disposed=true;epoch++;sources.Clear();}
    }
}
