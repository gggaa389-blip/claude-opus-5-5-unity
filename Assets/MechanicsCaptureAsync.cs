using System;
using System.IO;
using System.Collections;
using System.Collections.Concurrent;
using System.Threading;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;

public partial class MechanicsCapture {
    class ReadbackSlot {public RenderTexture texture;public AsyncGPUReadbackRequest request;public bool pending;public int frame;}
    ReadbackSlot[] readbacks;bool asyncEnabled;int encoders,savedFrames;string asyncFailure;float captureStarted;
    readonly ConcurrentQueue<byte[]> pixels=new ConcurrentQueue<byte[]>();readonly object failureGate=new object();readonly AutoResetEvent encoded=new AutoResetEvent(false);
    void BeginAsync(){
        asyncEnabled=Array.IndexOf(Environment.GetCommandLineArgs(),"-islandCaptureAsync")>=0;captureStarted=Time.realtimeSinceStartup;
        if(!asyncEnabled)return;if(frameExtension!="jpg"||!SystemInfo.supportsAsyncGPUReadback)throw new Exception("Async native capture requires JPEG and GPU readback support");
        asyncFailure=null;savedFrames=0;encoders=0;readbacks=new ReadbackSlot[3];
        for(int i=0;i<readbacks.Length;i++){var rt=new RenderTexture(width,height,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.sRGB);rt.name="Native screenshot readback "+i;rt.Create();readbacks[i]=new ReadbackSlot{texture=rt};}
    }
    void ThrowCaptureFailure(){lock(failureGate)if(asyncFailure!=null)throw new Exception("Native frame capture failed: "+asyncFailure);}
    void QueuePixels(ReadbackSlot slot){
        if(!slot.pending)return;slot.request.WaitForCompletion();if(slot.request.hasError)throw new Exception("GPU screenshot readback failed for frame "+slot.frame);
        // Backpressure blocks this capture frame rather than advancing simulation without a frame.
        while(Volatile.Read(ref encoders)>=3){ThrowCaptureFailure();encoded.WaitOne(5);}
        byte[] raw;if(!pixels.TryDequeue(out raw))raw=new byte[width*height*4];slot.request.GetData<byte>().CopyTo(raw);slot.pending=false;
        string path=FrameName(slot.frame);int w=width,h=height;bool flip=SystemInfo.graphicsUVStartsAtTop;Interlocked.Increment(ref encoders);
        ThreadPool.QueueUserWorkItem(_=>{try{if(flip){int stride=w*4;var row=new byte[stride];for(int y=0;y<h/2;y++){int a=y*stride,b=(h-1-y)*stride;Buffer.BlockCopy(raw,a,row,0,stride);Buffer.BlockCopy(raw,b,raw,a,stride);Buffer.BlockCopy(row,0,raw,b,stride);}}var jpg=ImageConversion.EncodeArrayToJPG(raw,GraphicsFormat.R8G8B8A8_UNorm,(uint)w,(uint)h,0,95);File.WriteAllBytes(path,jpg);Interlocked.Increment(ref savedFrames);}catch(Exception e){lock(failureGate)asyncFailure=e.ToString();}finally{pixels.Enqueue(raw);Interlocked.Decrement(ref encoders);encoded.Set();}});
    }
    void SubmitAsync(int number){
        ThrowCaptureFailure();foreach(var ready in readbacks)if(ready.pending&&ready.request.done)QueuePixels(ready);
        var slot=readbacks[number%readbacks.Length];if(slot.pending)QueuePixels(slot);
        ScreenCapture.CaptureScreenshotIntoRenderTexture(slot.texture);slot.frame=number;slot.request=AsyncGPUReadback.Request(slot.texture,0,TextureFormat.RGBA32);slot.pending=true;
        if(number==0&&Array.IndexOf(Environment.GetCommandLineArgs(),"-islandCaptureProbe")>=0){var reference=ScreenCapture.CaptureScreenshotAsTexture();File.WriteAllBytes(Path.ChangeExtension(FilePath,"reference.png"),reference.EncodeToPNG());Destroy(reference);}
    }
    IEnumerator FinishAsync(){
        foreach(var slot in readbacks)if(slot.pending)QueuePixels(slot);
        while(Volatile.Read(ref encoders)>0){ThrowCaptureFailure();yield return null;}
        ThrowCaptureFailure();if(savedFrames!=Frames)throw new Exception("Native capture frame count mismatch: "+savedFrames+" / "+Frames);
        ReleaseReadbacks();Debug.Log("UNITY_ASYNC_CAPTURE_DRAINED frames="+savedFrames+" wallSeconds="+(Time.realtimeSinceStartup-captureStarted));
    }
    void ReleaseReadbacks(){if(readbacks!=null){foreach(var slot in readbacks){slot.texture.Release();Destroy(slot.texture);}readbacks=null;}asyncEnabled=false;while(pixels.TryDequeue(out _)){} }
    void CloseAsync(){if(!asyncEnabled)return;try{foreach(var slot in readbacks)if(slot.pending)QueuePixels(slot);while(Volatile.Read(ref encoders)>0)encoded.WaitOne(5);}catch(Exception e){Error=e.Message;}finally{ReleaseReadbacks();}}
}
