param([Parameter(Mandatory)][long]$BubbleHwnd, [Parameter(Mandatory)][string]$ResultPath, [switch]$LookupOnly, [ValidateSet('Unchanged','On','Off')][string]$AutoSetFocus = 'Unchanged', [long]$PermissionOwnerHwnd = 0, [int]$ProbePid = 0)

$ErrorActionPreference = 'Stop'
# Nonshipping, x64, windowless MTA comparison. ABI checked against the saved
# official UIAutomationClient.h; no SetFocus or window activation call exists.
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
public static class NativeBubbleClient {
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr instance);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, UIntPtr process, IntPtr data);
    [StructLayout(LayoutKind.Explicit, Size = 24)] private struct Variant {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr String;
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint ReleaseCall(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ElementCall(IntPtr self, IntPtr hwnd, out IntPtr element);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ConditionCall(IntPtr self, int property, Variant value, out IntPtr condition);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int FindCall(IntPtr self, int scope, IntPtr condition, out IntPtr element);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int PatternCall(IntPtr self, int pattern, ref Guid iid, out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int InvokeCall(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetFocusCall(IntPtr self, out int enabled);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int PutFocusCall(IntPtr self, int enabled);
    public static int ObservedAutoSetFocus { get; private set; }
    public sealed class Stage {
        public string Name { get; set; }
        public long Qpc { get; set; }
        public long Foreground { get; set; }
    }
    private static Delegate Method(IntPtr instance, int slot, Type signature) {
        return Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size), signature);
    }
    private static void Mark(List<Stage> stages, string name) {
        stages.Add(new Stage { Name = name, Qpc = Stopwatch.GetTimestamp(), Foreground = GetForegroundWindow().ToInt64() });
    }
    private static void Check(int result) { Marshal.ThrowExceptionForHR(result); }
    private static void Release(IntPtr instance) {
        if (instance != IntPtr.Zero) ((ReleaseCall)Method(instance, 2, typeof(ReleaseCall)))(instance);
    }
    public static Stage[] Run(long hwnd, bool lookupOnly, string focusPolicy, long permissionOwner, int probePid) {
        if (IntPtr.Size != 8 || Thread.CurrentThread.GetApartmentState() != ApartmentState.MTA)
            throw new InvalidOperationException("Native UIA comparison requires x64 MTA.");
        Check(CoInitializeEx(IntPtr.Zero, 0));
        IntPtr automation = IntPtr.Zero, root = IntPtr.Zero, condition = IntPtr.Zero, element = IntPtr.Zero, pattern = IntPtr.Zero;
        IntPtr name = IntPtr.Zero;
        var stages = new List<Stage>();
        try {
            bool explicitFocus = focusPolicy != "Unchanged";
            Guid classId = new Guid(explicitFocus ? "e22ad333-b25f-460c-83d0-0581107395c9" : "ff48dba4-60ef-4201-aa87-54103eef594e");
            Guid automationId = new Guid(explicitFocus ? "34723aff-0c9d-49d0-9896-7ab52df8cd8a" : "30cbe57d-d9d0-452a-ab13-7ac5ac4825ee");
            Guid invokeId = new Guid("fb377fbe-8ea6-46d5-9c73-6499642d3059");
            Check(CoCreateInstance(ref classId, IntPtr.Zero, 1, ref automationId, out automation));
            ObservedAutoSetFocus = -1;
            if (explicitFocus) {
                int expected = focusPolicy == "On" ? 1 : 0;
                Check(((PutFocusCall)Method(automation, 59, typeof(PutFocusCall)))(automation, expected));
                int observed;
                Check(((GetFocusCall)Method(automation, 58, typeof(GetFocusCall)))(automation, out observed));
                ObservedAutoSetFocus = observed;
                if ((observed != 0) != (expected != 0)) throw new InvalidOperationException("Client AutoSetFocus readback mismatch.");
            }
            Mark(stages, "LookupStart");
            Check(((ElementCall)Method(automation, 6, typeof(ElementCall)))(automation, new IntPtr(hwnd), out root));
            Mark(stages, "RootFound");
            name = Marshal.StringToBSTR("Open ADB View probe");
            var value = new Variant { Type = 8, String = name }; // VT_BSTR.
            Check(((ConditionCall)Method(automation, 23, typeof(ConditionCall)))(automation, 30005, value, out condition));
            Check(((FindCall)Method(root, 5, typeof(FindCall)))(root, 7, condition, out element)); // TreeScope_Subtree.
            if (element == IntPtr.Zero) throw new InvalidOperationException("No named owned bubble action.");
            Mark(stages, "NamedElementFound");
            Check(((PatternCall)Method(element, 14, typeof(PatternCall)))(element, 10000, ref invokeId, out pattern));
            Mark(stages, "PatternAcquired");
            if (permissionOwner != 0) {
                if (probePid <= 0 || SendMessage(new IntPtr(permissionOwner), 0x8000 + 41, new UIntPtr((uint)probePid), IntPtr.Zero).ToInt64() != 1)
                    throw new InvalidOperationException("Foreground owner did not grant owned-probe permission immediately before the action.");
                Mark(stages, "ForegroundPermissionGranted");
            }
            if (!lookupOnly) {
                Mark(stages, "BeforeInvoke");
                Check(((InvokeCall)Method(pattern, 3, typeof(InvokeCall)))(pattern));
                Mark(stages, "InvokeReturned");
            }
            Mark(stages, "ClientComplete");
            return stages.ToArray();
        }
        finally {
            Release(pattern); Release(element); Release(condition); Release(root); Release(automation);
            if (name != IntPtr.Zero) Marshal.FreeBSTR(name);
            CoUninitialize();
        }
    }
}
'@
$stages = [NativeBubbleClient]::Run($BubbleHwnd, $LookupOnly.IsPresent, $AutoSetFocus, $PermissionOwnerHwnd, $ProbePid)
@{Passed=$true;Name='Open ADB View probe';ControlType='Native UIA Invoke';Invoked=(-not $LookupOnly);Apartment=[Threading.Thread]::CurrentThread.GetApartmentState().ToString();ClientPid=$PID;Frequency=[Diagnostics.Stopwatch]::Frequency;Stages=$stages;Client=$(if ($AutoSetFocus -eq 'Unchanged') { 'Native COM CUIAutomation' } else { 'Native COM CUIAutomation8' });RequestedAutoSetFocus=$AutoSetFocus;ObservedAutoSetFocus=[NativeBubbleClient]::ObservedAutoSetFocus} |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $ResultPath -Encoding utf8
