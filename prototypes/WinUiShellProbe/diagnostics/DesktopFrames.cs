// Nonshipping desktop observer, compiled only by the diagnostic PowerShell driver.
// COM slots verified against Microsoft win32metadata headers in the research note.
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;

public static unsafe class DesktopFrames {
    [DllImport("dxgi.dll", ExactSpelling=true)] private static extern int CreateDXGIFactory1(in Guid iid, out nint factory);
    [DllImport("d3d11.dll", ExactSpelling=true)] private static extern int D3D11CreateDevice(nint adapter, int driver, nint software, uint flags, nint levels, uint count, uint sdk, out nint device, out int level, out nint context);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rectangle);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] private struct OutputDesc {
        public fixed char Name[32]; public Rect Bounds; public int Attached, Rotation; public nint Monitor;
    }
    [StructLayout(LayoutKind.Sequential)] private struct FrameInfo {
        public long PresentTime, MouseTime; public uint Accumulated; public int Coalesced, Masked;
        public int PointerX, PointerY, PointerVisible; public uint MetadataBytes, ShapeBytes;
    }
    [StructLayout(LayoutKind.Sequential)] private struct TextureDesc {
        public uint Width, Height, Mips, ArraySize, Format, Samples, Quality, Usage, Bind, Cpu, Misc;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Box { public uint Left, Top, Front, Right, Bottom, Back; }
    [StructLayout(LayoutKind.Sequential)] private struct Mapped { public nint Data; public uint RowPitch, DepthPitch; }
    private static nint Slot(nint instance, int slot) => (*(nint**)instance)[slot];
    private static void Check(int result) { if (result < 0) Marshal.ThrowExceptionForHR(result); }
    private static nint Query(nint instance, string id) {
        Guid iid=new Guid(id); nint result=0;
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(instance,0))(instance,&iid,&result));
        return result;
    }
    private static void Release(ref nint instance) {
        if(instance!=0) { ((delegate* unmanaged[Stdcall]<nint,uint>)Slot(instance,2))(instance); instance=0; }
    }

    // GPU desktop acquisition is cropped before CPU copy. Only the verified probe's
    // rectangle is saved; no audio, pointer images, or full-desktop recording.
    public static void Capture(long windowValue, int expectedPid, string prefix, int seconds, long underlayValue=0, int underlayPid=0) {
        if(seconds<1 || seconds>60) throw new ArgumentOutOfRangeException(nameof(seconds));
        nint window=(nint)windowValue;
        nint underlay=(nint)underlayValue;
        if(underlay!=0) {
            GetWindowThreadProcessId(underlay,out uint actualUnderlayPid);
            if(underlayPid<=0 || actualUnderlayPid!=underlayPid || !IsWindowVisible(underlay))
                throw new InvalidOperationException("Warm-open underlay is not the owned visible helper.");
        }
        GetWindowThreadProcessId(window,out uint actualPid);
        using(Process target=Process.GetProcessById(expectedPid)) {
            if(actualPid!=expectedPid || !string.Equals(target.ProcessName,"WinUiShellProbe",StringComparison.Ordinal))
                throw new InvalidOperationException("Capture target is not the owned WinUI probe.");
        }
        nint priorDpi=SetThreadDpiAwarenessContext((nint)(-4));
        if(priorDpi==0) throw new InvalidOperationException("Capture DPI context unavailable.");
        nint factory=0,adapter=0,output=0,output1=0,device=0,context=0,duplication=0,staging=0;
        try {
            if(!GetWindowRect(window,out Rect bounds) || !IsWindowVisible(window)) throw new InvalidOperationException("Probe must be visible before capture.");
            int width=bounds.Right-bounds.Left,height=bounds.Bottom-bounds.Top;
            if(width<1 || height<1 || width>1000 || height>1500) throw new InvalidOperationException("Unexpected capture extent.");
            Check(CreateDXGIFactory1(new Guid("770aae78-f26f-4dba-a829-253c83d1b387"),out factory));
            nint monitor=MonitorFromWindow(window,2); OutputDesc description=default; bool found=false;
            for(uint a=0;!found;a++) {
                nint candidate=0;
                int result=((delegate* unmanaged[Stdcall]<nint,uint,nint*,int>)Slot(factory,12))(factory,a,&candidate);
                if((uint)result==0x887a0002) break;
                Check(result);
                try {
                    for(uint o=0;!found;o++) {
                        nint candidateOutput=0;
                        result=((delegate* unmanaged[Stdcall]<nint,uint,nint*,int>)Slot(candidate,7))(candidate,o,&candidateOutput);
                        if((uint)result==0x887a0002) break;
                        Check(result);
                        Check(((delegate* unmanaged[Stdcall]<nint,OutputDesc*,int>)Slot(candidateOutput,7))(candidateOutput,&description));
                        if(description.Monitor==monitor) { output=candidateOutput;adapter=candidate;candidate=0;found=true; }
                        else Release(ref candidateOutput);
                    }
                } finally { Release(ref candidate); }
            }
            if(!found || description.Rotation!=1) throw new InvalidOperationException("No matching unrotated output.");
            if(bounds.Left<description.Bounds.Left || bounds.Top<description.Bounds.Top || bounds.Right>description.Bounds.Right || bounds.Bottom>description.Bounds.Bottom)
                throw new InvalidOperationException("Probe crosses the selected output.");
            Check(D3D11CreateDevice(adapter,0,0,0x20,0,0,7,out device,out _,out context));
            output1=Query(output,"00cddea8-939b-4b83-a340-a685226666cc");
            nint created=0;
            Check(((delegate* unmanaged[Stdcall]<nint,nint,nint*,int>)Slot(output1,22))(output1,device,&created));
            duplication=created;
            var desc=new TextureDesc {Width=(uint)width,Height=(uint)height,Mips=1,ArraySize=1,Format=87,Samples=1,Usage=3,Cpu=0x20000};
            created=0;
            Check(((delegate* unmanaged[Stdcall]<nint,TextureDesc*,nint,nint*,int>)Slot(device,5))(device,&desc,0,&created));
            staging=created;
            byte[] pixels=new byte[checked(width*height*4)];
            using var frameFile=File.Create(prefix+".bgra.gz");
            using var compressed=new GZipStream(frameFile,CompressionLevel.Fastest,leaveOpen:true);
            using var frames=new BinaryWriter(compressed);
            using var csv=new StreamWriter(prefix+".frames.csv");
            csv.WriteLine("Index,PresentQpc,ObservedQpc,AccumulatedFrames,Hash,AcquireStartQpc,AcquiredQpc,CopiedQpc,HashedQpc,WrittenQpc");
            var debug=(DebuggableAttribute)Attribute.GetCustomAttribute(typeof(DesktopFrames).Assembly,typeof(DebuggableAttribute));
            var elapsed=Stopwatch.StartNew();int index=0;
            while(elapsed.Elapsed.TotalSeconds<seconds) {
                FrameInfo info=default;nint resource=0,texture=0;
                long acquireStart=Stopwatch.GetTimestamp();
                int acquired=((delegate* unmanaged[Stdcall]<nint,uint,FrameInfo*,nint*,int>)Slot(duplication,8))(duplication,50,&info,&resource);
                long acquiredAt=Stopwatch.GetTimestamp();
                if((uint)acquired==0x887a0027) continue;
                Check(acquired);
                try {
                    if(info.PresentTime==0 || info.Accumulated==0) continue;
                    bool visible=IsWindowVisible(window);
                    if(!GetWindowRect(window,out Rect current) || (!visible && underlay==0) || current.Left!=bounds.Left || current.Top!=bounds.Top || current.Right!=bounds.Right || current.Bottom!=bounds.Bottom)
                        throw new InvalidOperationException("Probe visibility/bounds changed during capture.");
                    if(underlay!=0) {
                        GetWindowThreadProcessId(underlay,out uint owner);
                        if(owner!=underlayPid || !IsWindowVisible(underlay) || !GetWindowRect(underlay,out Rect backing)
                            || backing.Left>bounds.Left || backing.Top>bounds.Top || backing.Right<bounds.Right || backing.Bottom<bounds.Bottom)
                            throw new InvalidOperationException("Owned underlay no longer covers the warm-open crop.");
                        for(int gy=0;gy<3;gy++) for(int gx=0;gx<3;gx++) {
                            nint covering=WindowFromPoint(new Point {X=bounds.Left+2+(width-5)*gx/2,Y=bounds.Top+2+(height-5)*gy/2});
                            GetWindowThreadProcessId(covering,out uint coveringPid);
                            if(coveringPid!=expectedPid && coveringPid!=underlayPid)
                                throw new InvalidOperationException("Foreign window covers the synthetic capture; no further pixels saved.");
                        }
                    }
                    texture=Query(resource,"6f15aaf2-d208-4e89-9ab4-489535d34f9c");
                    Box box=new Box {Left=(uint)(bounds.Left-description.Bounds.Left),Top=(uint)(bounds.Top-description.Bounds.Top),Right=(uint)(bounds.Right-description.Bounds.Left),Bottom=(uint)(bounds.Bottom-description.Bounds.Top),Back=1};
                    ((delegate* unmanaged[Stdcall]<nint,nint,uint,uint,uint,uint,nint,uint,Box*,void>)Slot(context,46))(context,staging,0,0,0,0,texture,0,&box);
                    Mapped mapped=default;
                    Check(((delegate* unmanaged[Stdcall]<nint,nint,uint,int,uint,Mapped*,int>)Slot(context,14))(context,staging,0,1,0,&mapped));
                    try {
                        for(int row=0;row<height;row++) Marshal.Copy(mapped.Data+checked((int)(row*mapped.RowPitch)),pixels,row*width*4,width*4);
                    } finally { ((delegate* unmanaged[Stdcall]<nint,nint,uint,void>)Slot(context,15))(context,staging,0); }
                    long copiedAt=Stopwatch.GetTimestamp();
                    ulong hash=14695981039346656037UL;
                    foreach(byte value in pixels) hash=unchecked((hash^value)*1099511628211UL);
                    long hashedAt=Stopwatch.GetTimestamp();
                    frames.Write(pixels);
                    if(frameFile.Position>128*1024*1024) throw new IOException("Diagnostic capture exceeded its 128 MiB disk bound.");
                    long writtenAt=Stopwatch.GetTimestamp();
                    csv.WriteLine($"{index++},{info.PresentTime},{Stopwatch.GetTimestamp()},{info.Accumulated},{hash},{acquireStart},{acquiredAt},{copiedAt},{hashedAt},{writtenAt}");
                    if(index==1) {
                        csv.Flush();
                        File.WriteAllText(prefix+".ready.json",System.Text.Json.JsonSerializer.Serialize(new { Width=width,Height=height,X=bounds.Left,Y=bounds.Top,Frequency=Stopwatch.Frequency,ProcessId=expectedPid,JitOptimizerDisabled=debug?.IsJITOptimizerDisabled,BaselinePresentQpc=info.PresentTime,BaselineObservedQpc=writtenAt,WarmOpen=underlay!=0 }));
                    }
                } finally {
                    Release(ref texture);Release(ref resource);
                    Check(((delegate* unmanaged[Stdcall]<nint,int>)Slot(duplication,14))(duplication));
                }
            }
            if(index<2) throw new InvalidOperationException("Insufficient desktop frames.");
        } finally {
            Release(ref staging);Release(ref duplication);Release(ref context);Release(ref device);
            Release(ref output1);Release(ref output);Release(ref adapter);Release(ref factory);
            SetThreadDpiAwarenessContext(priorDpi);
        }
    }
}
