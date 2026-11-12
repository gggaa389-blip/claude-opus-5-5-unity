using System;
using System.IO;
using System.Collections;
using System.Diagnostics;
using UnityEngine;
using Unity.Collections;
using Debug=UnityEngine.Debug;
// Native Unity PNG frames and mixed game audio are captured first; MP4 encoding starts afterward.
public partial class MechanicsCapture : MonoBehaviour {
    public bool Recording {get;private set;}public bool Complete {get;private set;}public int Frames {get;private set;}
    public string FilePath {get;private set;}public string Error {get;private set;}="";
    BinaryWriter wave;byte[] audioBytes;string folder,tempAudio;long samples;double energy;int sampleRate,channels,fps,width,height;bool audioRunning;float oldCapture;string frameExtension="png";
    string FrameName(int number){return Path.Combine(folder,"frame-"+number.ToString("D6")+"."+frameExtension);}
    public void Begin(string path,int rate=30){
        frameExtension=Array.IndexOf(Environment.GetCommandLineArgs(),"-islandCaptureJpeg")>=0?"jpg":"png";FilePath=Path.GetFullPath(path);Directory.CreateDirectory(Path.GetDirectoryName(FilePath));folder=Path.Combine(Path.GetDirectoryName(FilePath),"capture-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);tempAudio=Path.Combine(folder,"game-audio.wav");Complete=false;Frames=0;samples=0;energy=0;Error="";fps=rate;width=Screen.width;height=Screen.height;
        oldCapture=Time.captureDeltaTime;Time.captureFramerate=fps;sampleRate=AudioSettings.outputSampleRate;channels=AudioSettings.speakerMode==AudioSpeakerMode.Mono?1:2;wave=new BinaryWriter(File.Create(tempAudio));Header(0);audioRunning=AudioRenderer.Start();if(!audioRunning)throw new Exception("Unity audio capture could not start");Recording=true;BeginAsync();StartCoroutine(Capture());Debug.Log("UNITY_CAPTURE_BEGIN "+FilePath+" native frames and audio; encoding deferred");
    }
    IEnumerator Capture(){while(Recording){yield return new WaitForEndOfFrame();if(!Recording)break;if(asyncEnabled)SubmitAsync(Frames);else{var frame=ScreenCapture.CaptureScreenshotAsTexture();File.WriteAllBytes(FrameName(Frames),frameExtension=="jpg"?frame.EncodeToJPG(95):frame.EncodeToPNG());Destroy(frame);}Frames++;
            int count=AudioRenderer.GetSampleCountForCaptureFrame()*channels;if(count>0){using(var buffer=new NativeArray<float>(count,Allocator.Temp,NativeArrayOptions.UninitializedMemory)){if(!AudioRenderer.Render(buffer))throw new Exception("Unity audio render failed");if(audioBytes==null||audioBytes.Length<count*2)audioBytes=new byte[count*2];for(int i=0;i<count;i++){float value=Mathf.Clamp(buffer[i],-1,1);energy+=value*value;short pcm=(short)(value*32767);audioBytes[i*2]=(byte)pcm;audioBytes[i*2+1]=(byte)(pcm>>8);}wave.Write(audioBytes,0,count*2);samples+=count;}}
        }}
    void Header(int bytes){wave.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));wave.Write(bytes+36);wave.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));wave.Write(16);wave.Write((short)1);wave.Write((short)channels);wave.Write(sampleRate);wave.Write(sampleRate*channels*2);wave.Write((short)(channels*2));wave.Write((short)16);wave.Write(System.Text.Encoding.ASCII.GetBytes("data"));wave.Write(bytes);}
    [Serializable] public class EncodingRequest {public string source="Unity ScreenCapture frames and AudioRenderer; encode after player exits",status="captured",output,frameDirectory,audioPath,frameExtension;public int frames,fps,width,height,sampleRate,channels;public long audioSamples;public double audioRms;}
    public IEnumerator Finish(){
        Recording=false;if(asyncEnabled)yield return FinishAsync();yield return new WaitForEndOfFrame();yield return null;if(audioRunning){AudioRenderer.Stop();audioRunning=false;}Time.captureDeltaTime=oldCapture;wave.Seek(0,SeekOrigin.Begin);Header((int)(samples*2));wave.Close();wave=null;
        if(Frames<=0||Directory.GetFiles(folder,"frame-*."+frameExtension).Length!=Frames)throw new Exception("Native frame capture was incomplete; captured media retained");
        var request=new EncodingRequest{output=FilePath,frameExtension=frameExtension,frameDirectory=folder,audioPath=tempAudio,frames=Frames,fps=fps,width=width,height=height,sampleRate=sampleRate,channels=channels,audioSamples=samples,audioRms=Math.Sqrt(energy/Math.Max(1,samples))};
        string json=JsonUtility.ToJson(request,true);File.WriteAllText(Path.ChangeExtension(FilePath,"encode.json"),json);File.WriteAllText(Path.ChangeExtension(FilePath,"capture.json"),json);File.Copy(FrameName(0),Path.ChangeExtension(FilePath,"preview."+frameExtension),true);Complete=true;
        Debug.Log("UNITY_CAPTURE_READY "+FilePath+" frames="+Frames+" mixedAudioSamples="+samples+"; post-exit encoding manifest saved");
    }
    void OnDestroy(){CloseAsync();if(audioRunning)AudioRenderer.Stop();if(wave!=null){wave.Seek(0,SeekOrigin.Begin);Header((int)(samples*2));wave.Close();}Time.captureDeltaTime=oldCapture;}
}



