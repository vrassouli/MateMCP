using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Automation;

namespace MateMCP.WindowsDesktopHelper
{
    internal static class Program
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
        private static readonly TreeWalker Walker = TreeWalker.ControlViewWalker;

        public static int Main()
        {
            Response response;
            try
            {
                var request = Json.Deserialize<Request>(Console.In.ReadToEnd()) ?? throw new InvalidOperationException("A helper request is required.");
                response = Execute(request);
            }
            catch (AmbiguousException ex) { response = Response.Fail("ambiguous", ex.Message); }
            catch (NotFoundException ex) { response = Response.Fail("not-found", ex.Message); }
            catch (ProtectedException ex) { response = Response.Fail("protected", ex.Message); }
            catch (ArgumentException ex) { response = Response.Fail("invalid", ex.Message); }
            catch (Exception ex) { response = Response.Fail("operation", ex.Message); }
            Console.Out.Write(Json.Serialize(response));
            return 0;
        }

        private static Response Execute(Request request)
        {
            switch ((request.Command ?? "").Trim().ToLowerInvariant())
            {
                case "snapshot": return Response.ForSnapshot(Snapshot(request.WindowId, Math.Max(1, Math.Min(request.MaxElements <= 0 ? 400 : request.MaxElements, 1000))));
                case "act": return Response.ForElement(Act(request));
                case "click-at": return Response.ForElement(ClickAt(request));
                default: throw new ArgumentException("Unsupported Windows desktop helper command.");
            }
        }

        private static Snapshot Snapshot(string windowId, int maxElements)
        {
            bool truncated;
            var entries = Traverse(ResolveRoot(windowId), maxElements, out truncated);
            return new Snapshot { WindowId = windowId, Platform = "windows-uia", Truncated = truncated, Elements = entries.Select(x => x.Info).ToList() };
        }

        private static Element Act(Request request)
        {
            bool ignored;
            var selected = Resolve(Traverse(ResolveRoot(request.WindowId), 1000, out ignored), request.Selector ?? new Selector());
            var element = selected.Automation;
            switch ((request.Action ?? "").Trim().ToLowerInvariant())
            {
                case "focus": element.SetFocus(); break;
                case "invoke":
                    if (!TryBackgroundClick(element)) Pattern<InvokePattern>(element, InvokePattern.Pattern, "invoke", selected.Info).Invoke();
                    break;
                case "value":
                    if (selected.Info.Protected) throw new ProtectedException("MateMCP refuses to read or replace protected/secure-text values through semantic UI automation.");
                    Pattern<ValuePattern>(element, ValuePattern.Pattern, "set-value", selected.Info).SetValue(request.Text ?? ""); break;
                case "toggle":
                    if (!TryBackgroundClick(element)) Pattern<TogglePattern>(element, TogglePattern.Pattern, "toggle", selected.Info).Toggle();
                    break;
                case "select": Pattern<SelectionItemPattern>(element, SelectionItemPattern.Pattern, "select", selected.Info).Select(); break;
                case "expand": Pattern<ExpandCollapsePattern>(element, ExpandCollapsePattern.Pattern, "expand", selected.Info).Expand(); break;
                case "collapse": Pattern<ExpandCollapsePattern>(element, ExpandCollapsePattern.Pattern, "collapse", selected.Info).Collapse(); break;
                case "scroll": Pattern<ScrollItemPattern>(element, ScrollItemPattern.Pattern, "scroll-into-view", selected.Info).ScrollIntoView(); break;
                default: throw new ArgumentException("Unsupported semantic UI action.");
            }
            return BuildInfo(element, selected.Info.Id, selected.Info.ParentId);
        }

        private static Element ClickAt(Request request)
        {
            var hwnd = ParseWindow(request.WindowId);
            if (!GetWindowRect(hwnd, out var rect)) throw new InvalidOperationException("The target window is no longer available.");
            var width = rect.Right - rect.Left; var height = rect.Bottom - rect.Top;
            if (!Finite(request.X) || !Finite(request.Y) || request.X < 0 || request.Y < 0 || request.X >= width || request.Y >= height)
                throw new ArgumentException("The requested window-relative point is outside the target window.");

            GetWindowThreadProcessId(hwnd, out var targetPid);
            var screenX = rect.Left + request.X;
            var screenY = rect.Top + request.Y;
            var root = ResolveRoot(request.WindowId);
            var candidates = new List<AutomationElement> { root };
            var descendants = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            for (var i = 0; i < descendants.Count; i++) candidates.Add(descendants[i]);

            var hit = candidates
                .Where(element =>
                {
                    var pid = Safe(() => element.Current.ProcessId, 0);
                    if (pid != 0 && targetPid != 0 && pid != unchecked((int)targetPid)) return false;
                    var bounds = Safe(() => element.Current.BoundingRectangle, Rect.Empty);
                    return bounds != Rect.Empty && bounds.Contains(new Point(screenX, screenY));
                })
                .OrderBy(element =>
                {
                    var bounds = Safe(() => element.Current.BoundingRectangle, Rect.Empty);
                    return bounds == Rect.Empty ? double.MaxValue : bounds.Width * bounds.Height;
                })
                .FirstOrDefault();

            if (hit == null)
                throw new InvalidOperationException("No target-window UI Automation element exists at the requested point.");

            var elementAtPoint = hit;
            for (var depth = 0; depth < 32 && elementAtPoint != null; depth++)
            {
                var info = BuildInfo(elementAtPoint, depth == 0 ? "uia:point" : "uia:point-parent:" + depth, null);
                if (info.Enabled)
                {
                    InvokePattern invoke; TogglePattern toggle; SelectionItemPattern select; ExpandCollapsePattern expand;
                    if (TryPattern(elementAtPoint, InvokePattern.Pattern, out invoke) || TryPattern(elementAtPoint, TogglePattern.Pattern, out toggle))
                    {
                        if (!TryBackgroundClick(elementAtPoint))
                            throw new InvalidOperationException("The target control has no background-safe native click path; isolated click-at refused to steal foreground focus.");
                        return BuildInfo(elementAtPoint, info.Id, null);
                    }
                    if (TryPattern(elementAtPoint, SelectionItemPattern.Pattern, out select) || TryPattern(elementAtPoint, ExpandCollapsePattern.Pattern, out expand))
                        throw new InvalidOperationException("The target control requires a UI Automation action that may steal foreground focus; isolated click-at refused the action.");
                }
                elementAtPoint = Safe(() => Walker.GetParent(elementAtPoint), (AutomationElement)null);
                if (elementAtPoint != null)
                {
                    var pid = Safe(() => elementAtPoint.Current.ProcessId, 0);
                    if (pid != 0 && targetPid != 0 && pid != unchecked((int)targetPid)) break;
                }
            }
            throw new InvalidOperationException("No actionable Windows UI Automation control was found at the requested point or in its target-window ancestors.");
        }

        private static AutomationElement ResolveRoot(string windowId) => AutomationElement.FromHandle(ParseWindow(windowId)) ?? throw new InvalidOperationException("Windows UI Automation could not resolve the target window.");
        private static IntPtr ParseWindow(string id)
        {
            long raw; if (string.IsNullOrWhiteSpace(id) || !long.TryParse(id, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out raw)) throw new ArgumentException("Invalid Windows HWND identifier.");
            return new IntPtr(raw);
        }

        private static List<Entry> Traverse(AutomationElement root, int max, out bool truncated)
        {
            var result = new List<Entry>();
            Add(root, "uia:root");
            var children = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            truncated = children.Count + 1 > max;
            for (var i = 0; i < Math.Min(children.Count, Math.Max(0, max - 1)); i++) Add(children[i], "uia:" + (i + 1));
            return result;
            void Add(AutomationElement item, string fallback)
            {
                var id = ElementId(item, fallback); if (result.Any(x => x.Info.Id == id)) id += ":" + result.Count;
                var parent = Safe(() => Walker.GetParent(item), (AutomationElement)null);
                result.Add(new Entry(item, BuildInfo(item, id, parent == null ? null : ElementId(parent, "uia:root"))));
            }
        }

        private static Entry Resolve(List<Entry> entries, Selector selector)
        {
            var matches = entries.Where(x => Match(x.Info.Role, selector.Role) && Match(x.Info.Name, selector.Name) && Match(x.Info.AutomationId, selector.AutomationId) && Match(x.Info.ParentId, selector.ParentId)).ToList();
            if (matches.Count == 0) throw new NotFoundException("No UI element matched the requested semantic selector.");
            if (selector.Index.HasValue)
            {
                if (selector.Index.Value < 0 || selector.Index.Value >= matches.Count) throw new NotFoundException("The semantic selector index is outside the matched element range.");
                return matches[selector.Index.Value];
            }
            if (matches.Count > 1) throw new AmbiguousException("The semantic selector matched multiple UI elements. Add automationId, parentId, or index to disambiguate.");
            return matches[0];
        }
        private static bool Match(string actual, string requested) => string.IsNullOrWhiteSpace(requested) || string.Equals((actual ?? "").Trim(), requested.Trim(), StringComparison.OrdinalIgnoreCase);

        private static Element BuildInfo(AutomationElement element, string id, string parentId)
        {
            var current = element.Current;
            var item = new Element
            {
                Id=id, ParentId=parentId, Role=Role(Safe(() => current.ControlType, (ControlType)null)), Name=Null(Safe(() => current.Name, (string)null)),
                AutomationId=Null(Safe(() => current.AutomationId, (string)null)), Protected=Safe(() => current.IsPassword, false), Enabled=Safe(() => current.IsEnabled, true),
                Focused=IsFocused(element), Bounds=Bounds(Safe(() => current.BoundingRectangle, Rect.Empty)), Actions=new List<string>()
            };
            ValuePattern value; SelectionItemPattern selection; TogglePattern toggle; ExpandCollapsePattern expand;
            if (!item.Protected && TryPattern(element, ValuePattern.Pattern, out value)) item.Value=Null(Safe(() => value.Current.Value, (string)null));
            if (TryPattern(element, SelectionItemPattern.Pattern, out selection)) item.Selected=Safe(() => selection.Current.IsSelected, false);
            if (TryPattern(element, TogglePattern.Pattern, out toggle)) item.Checked=Safe(() => toggle.Current.ToggleState == ToggleState.On, false);
            if (TryPattern(element, ExpandCollapsePattern.Pattern, out expand)) item.Expanded=Safe(() => expand.Current.ExpandCollapseState == ExpandCollapseState.Expanded, false);
            InvokePattern invoke; ScrollItemPattern scroll;
            if (item.Enabled) item.Actions.Add("focus");
            if (TryPattern(element, InvokePattern.Pattern, out invoke)) item.Actions.Add("invoke");
            if (!item.Protected && TryPattern(element, ValuePattern.Pattern, out value)) item.Actions.Add("set-value");
            if (TryPattern(element, TogglePattern.Pattern, out toggle)) item.Actions.Add("toggle");
            if (TryPattern(element, SelectionItemPattern.Pattern, out selection)) item.Actions.Add("select");
            if (TryPattern(element, ExpandCollapsePattern.Pattern, out expand)) { item.Actions.Add("expand"); item.Actions.Add("collapse"); }
            if (TryPattern(element, ScrollItemPattern.Pattern, out scroll)) item.Actions.Add("scroll-into-view");
            return item;
        }
        private static bool TryBackgroundClick(AutomationElement element)
        {
            try
            {
                var type = element.Current.ControlType;
                if (type != ControlType.Button && type != ControlType.CheckBox && type != ControlType.RadioButton) return false;
                var hwnd = element.Current.NativeWindowHandle;
                if (hwnd == 0) return false;
                UIntPtr result;
                return SendMessageTimeout(new IntPtr(hwnd), BmClick, UIntPtr.Zero, IntPtr.Zero, SmtoAbortIfHung, 2000, out result) != IntPtr.Zero;
            }
            catch { return false; }
        }

        private static T Pattern<T>(AutomationElement e, AutomationPattern p, string action, Element info) where T:class
        { T value; if (TryPattern(e,p,out value)) return value; throw new InvalidOperationException("The selected " + info.Role + " does not expose the native '"+action+"' pattern."); }
        private static bool TryPattern<T>(AutomationElement e, AutomationPattern p, out T typed) where T:class
        { typed=null; try { object raw; if (!e.TryGetCurrentPattern(p,out raw) || !(raw is T)) return false; typed=(T)raw; return true; } catch { return false; } }
        private static bool IsFocused(AutomationElement element)
        {
            if (Safe(() => element.Current.HasKeyboardFocus, false)) return true;
            try
            {
                var focused = AutomationElement.FocusedElement;
                var currentId = element.GetRuntimeId();
                var focusedId = focused == null ? null : focused.GetRuntimeId();
                return currentId != null && focusedId != null && currentId.SequenceEqual(focusedId);
            }
            catch { return false; }
        }
        private static T Safe<T>(Func<T> getter, T fallback) { try { return getter(); } catch { return fallback; } }
        private static string ElementId(AutomationElement e,string fallback) { var id=Safe(()=>e.GetRuntimeId(),(int[])null); return id==null||id.Length==0?fallback:"uia:"+string.Join(".",id.Select(x=>x.ToString(CultureInfo.InvariantCulture))); }
        private static string Null(string value)=>string.IsNullOrWhiteSpace(value)?null:value;
        private static bool Finite(double value)=>!double.IsNaN(value)&&!double.IsInfinity(value);
        private static RectDto Bounds(Rect r)=>r==Rect.Empty?null:new RectDto{X=r.X,Y=r.Y,Width=r.Width,Height=r.Height};
        private static string Role(ControlType t)
        {
            if(t==null)return "unknown"; if(t==ControlType.Button)return "button"; if(t==ControlType.CheckBox)return "checkbox"; if(t==ControlType.ComboBox)return "combobox";
            if(t==ControlType.Edit)return "textbox"; if(t==ControlType.Hyperlink)return "link"; if(t==ControlType.ListItem)return "listitem"; if(t==ControlType.List)return "list";
            if(t==ControlType.Menu)return "menu"; if(t==ControlType.MenuBar)return "menubar"; if(t==ControlType.MenuItem)return "menuitem"; if(t==ControlType.RadioButton)return "radiobutton";
            if(t==ControlType.Tab)return "tab"; if(t==ControlType.TabItem)return "tabitem"; if(t==ControlType.Text)return "text"; if(t==ControlType.ToolBar)return "toolbar";
            if(t==ControlType.Tree)return "tree"; if(t==ControlType.TreeItem)return "treeitem"; if(t==ControlType.Group)return "group"; if(t==ControlType.DataGrid)return "datagrid";
            if(t==ControlType.DataItem)return "dataitem"; if(t==ControlType.Document)return "document"; if(t==ControlType.Window)return "window"; if(t==ControlType.Pane)return "pane";
            if(t==ControlType.Table)return "table"; if(t==ControlType.TitleBar)return "titlebar"; if(t==ControlType.Separator)return "separator"; return "control-"+t.Id;
        }

        private const uint BmClick = 0x00F5;
        private const uint SmtoAbortIfHung = 0x0002;
        [DllImport("user32.dll", SetLastError=true)] private static extern IntPtr SendMessageTimeout(IntPtr hwnd,uint msg,UIntPtr wParam,IntPtr lParam,uint flags,uint timeout,out UIntPtr result);
        [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr hwnd,out NativeRect rect);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint processId);
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left,Top,Right,Bottom; }
        private sealed class Entry { public Entry(AutomationElement a,Element i){Automation=a;Info=i;} public AutomationElement Automation{get;} public Element Info{get;} }
    }

    public sealed class Request { public string Command{get;set;} public string WindowId{get;set;} public int MaxElements{get;set;} public string Action{get;set;} public Selector Selector{get;set;} public string Text{get;set;} public double X{get;set;} public double Y{get;set;} }
    public sealed class Selector { public string Role{get;set;} public string Name{get;set;} public string AutomationId{get;set;} public string ParentId{get;set;} public int? Index{get;set;} }
    public sealed class Snapshot { public string WindowId{get;set;} public string Platform{get;set;} public bool Truncated{get;set;} public List<Element> Elements{get;set;} }
    public sealed class Element { public string Id{get;set;} public string ParentId{get;set;} public string Role{get;set;} public string Name{get;set;} public string AutomationId{get;set;} public string Value{get;set;} public bool Protected{get;set;} public bool Enabled{get;set;} public bool Focused{get;set;} public bool? Selected{get;set;} public bool? Checked{get;set;} public bool? Expanded{get;set;} public RectDto Bounds{get;set;} public List<string> Actions{get;set;} }
    public sealed class RectDto { public double X{get;set;} public double Y{get;set;} public double Width{get;set;} public double Height{get;set;} }
    public sealed class Response { public bool Ok{get;set;} public Snapshot Snapshot{get;set;} public Element Element{get;set;} public string Error{get;set;} public string ErrorType{get;set;} public static Response ForSnapshot(Snapshot s)=>new Response{Ok=true,Snapshot=s}; public static Response ForElement(Element e)=>new Response{Ok=true,Element=e}; public static Response Fail(string t,string e)=>new Response{Ok=false,ErrorType=t,Error=e}; }
    internal sealed class AmbiguousException:Exception{public AmbiguousException(string m):base(m){}} internal sealed class NotFoundException:Exception{public NotFoundException(string m):base(m){}} internal sealed class ProtectedException:Exception{public ProtectedException(string m):base(m){}}
}
