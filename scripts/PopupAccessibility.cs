using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace AtkBatteryTray {
    // Rendering, pointer hit testing and accessibility all consume PopupControl.
    // Children resolve their stable identity again before invoking an action.
    internal sealed class PopupAccessibility:AccessibleObject {
        readonly Control host;
        readonly Func<List<PopupControl>> controls;
        readonly Func<int> focused;
        readonly Action<PopupControl> invoke,setFocus;
        readonly Action<PopupControl,int> setSlider;
        readonly Func<float> scale;
        readonly Dictionary<string,Item> children=new Dictionary<string,Item>();
        internal PopupAccessibility(Control host,Func<List<PopupControl>> controls,Func<int> focused,Action<PopupControl> invoke,Func<float> scale,Action<PopupControl> setFocus,Action<PopupControl,int> setSlider) {
            this.host=host;this.controls=controls;this.focused=focused;this.invoke=invoke;this.scale=scale;this.setFocus=setFocus;this.setSlider=setSlider;
        }
        internal List<PopupControl> Items {get{return host.IsDisposed?new List<PopupControl>():controls();}}
        public override string Name {get{return host.AccessibleName??host.Text;}set{host.AccessibleName=value;}}
        public override AccessibleRole Role {get{return AccessibleRole.Pane;}}
        public override AccessibleStates State {get{return host.Visible?AccessibleStates.Focusable:AccessibleStates.Invisible;}}
        public override Rectangle Bounds {get{return host.IsHandleCreated?host.RectangleToScreen(host.ClientRectangle):Rectangle.Empty;}}
        public override AccessibleObject Parent {get{return null;}}
        public override int GetChildCount(){return Items.Count;}
        public override AccessibleObject GetChild(int index) {
            var list=Items;if(index<0||index>=list.Count)return null;var row=list[index];Item item;
            if(!children.TryGetValue(row.StableKey,out item)){item=new Item(this,row.StableKey);children[row.StableKey]=item;}
            if(children.Count>512){children.Clear();children[row.StableKey]=item;}return item;
        }
        public override AccessibleObject GetFocused() {
            var list=Items;for(int i=0;i<list.Count;i++)if(list[i].Id==focused()&&!list[i].Information)return GetChild(i);return null;
        }
        public override AccessibleObject GetSelected() {
            var list=Items;for(int i=0;i<list.Count;i++)if(list[i].Selected||list[i].Toggle&&list[i].Checked)return GetChild(i);return null;
        }
        public override AccessibleObject HitTest(int x,int y) {
            for(int i=0;i<GetChildCount();i++){var child=GetChild(i);if(child!=null&&child.Bounds.Contains(x,y))return child;}return Bounds.Contains(x,y)?this:null;
        }
        PopupControl Resolve(string key){return Items.Find(delegate(PopupControl row){return row.StableKey==key;});}
        void Run(Action action) {
            if(host.IsDisposed||host.Disposing||!host.IsHandleCreated)return;
            if(host.InvokeRequired){try{host.BeginInvoke(action);}catch(InvalidOperationException){}}else action();
        }
        sealed class Item:AccessibleObject {
            readonly PopupAccessibility tree;readonly string key;
            internal Item(PopupAccessibility tree,string key){this.tree=tree;this.key=key;}
            PopupControl Row {get{return tree.Resolve(key);}}
            public override AccessibleObject Parent {get{return tree;}}
            public override string Name {get{var row=Row;return row==null?"设备已断开":row.Text;}}
            public override string Description {get{var row=Row;return row==null?"此操作已不可用":row.Detail;}}
            public override AccessibleRole Role {get{var row=Row;return row==null||row.Information?AccessibleRole.StaticText:row.Slider?AccessibleRole.Slider:row.Toggle?AccessibleRole.CheckButton:AccessibleRole.PushButton;}}
            public override string DefaultAction {get{var row=Row;return row==null||row.Information||!row.Enabled?null:row.Slider?"调整音量":row.Toggle?"切换":"执行";}}
            public override string Value {
                get{var row=Row;return row==null?null:row.Toggle?row.Checked?"开启":"关闭":row.Value;}
                set {
                    var row=Row;int number;string normalized=(value??"").Trim().TrimEnd('%');
                    if(row==null||!row.Enabled||!row.Slider||tree.setSlider==null||!Int32.TryParse(normalized,NumberStyles.Integer,CultureInfo.InvariantCulture,out number)||number<0||number>100)return;
                    tree.Run(delegate {var live=Row;if(live!=null&&live.Enabled&&live.Slider)tree.setSlider(live,number);});
                }
            }
            public override Rectangle Bounds {
                get {
                    var row=Row;if(row==null||!tree.host.IsHandleCreated)return Rectangle.Empty;
                    float factor=Math.Max(.5f,tree.scale());var rect=row.Rect;
                    var scaled=Rectangle.FromLTRB((int)Math.Floor(rect.Left*factor),(int)Math.Floor(rect.Top*factor),(int)Math.Ceiling(rect.Right*factor),(int)Math.Ceiling(rect.Bottom*factor));
                    scaled=Rectangle.Intersect(scaled,tree.host.ClientRectangle);return scaled.IsEmpty?Rectangle.Empty:tree.host.RectangleToScreen(scaled);
                }
            }
            public override AccessibleStates State {
                get {
                    var row=Row;if(row==null)return AccessibleStates.Unavailable|AccessibleStates.Invisible;
                    var state=row.Information?AccessibleStates.ReadOnly:AccessibleStates.Focusable;
                    if(!row.Enabled)state|=AccessibleStates.Unavailable;
                    if(row.Selected)state|=AccessibleStates.Selected;
                    if(row.Toggle&&row.Checked)state|=AccessibleStates.Checked;
                    if(row.Busy)state|=AccessibleStates.Busy;
                    if(row.Id==tree.focused()&&!row.Information)state|=AccessibleStates.Focused;
                    if(Bounds.IsEmpty)state|=AccessibleStates.Offscreen;
                    if(!tree.host.Visible)state|=AccessibleStates.Invisible;
                    return state;
                }
            }
            public override void DoDefaultAction() {
                tree.Run(delegate {var row=Row;if(row!=null&&row.Enabled&&!row.Information&&!row.Slider){if(tree.setFocus!=null)tree.setFocus(row);tree.invoke(row);}});
            }
            public override void Select(AccessibleSelection flags) {
                if((flags&(AccessibleSelection.TakeFocus|AccessibleSelection.TakeSelection))==0)return;
                tree.Run(delegate {var row=Row;if(row!=null&&row.Enabled&&!row.Information&&tree.setFocus!=null)tree.setFocus(row);});
            }
            public override AccessibleObject Navigate(AccessibleNavigation direction) {
                var list=tree.Items;int index=list.FindIndex(delegate(PopupControl row){return row.StableKey==key;});
                if(direction==AccessibleNavigation.Next||direction==AccessibleNavigation.Down||direction==AccessibleNavigation.Right)return tree.GetChild(index+1);
                if(direction==AccessibleNavigation.Previous||direction==AccessibleNavigation.Up||direction==AccessibleNavigation.Left)return tree.GetChild(index-1);
                return null;
            }
        }
    }
}
