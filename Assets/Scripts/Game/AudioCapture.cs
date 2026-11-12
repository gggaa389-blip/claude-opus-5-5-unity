using System.IO;
using Unity.Collections;
using UnityEngine;

/// The game's sound for the recorded videos: with a fixed capture frame rate Unity's AudioRenderer mixes
/// exactly one frame of audio per rendered frame, which goes into a 16-bit WAV next to the frames.
public class AudioCapture
{
    readonly FileStream _fs;
    readonly int _channels, _rate;
    long _samples;

    public AudioCapture(string path)
    {
        _rate = AudioSettings.outputSampleRate;
        _channels = AudioSettings.speakerMode == AudioSpeakerMode.Mono ? 1 : 2;
        _fs = new FileStream(path, FileMode.Create);
        _fs.Write(new byte[44], 0, 44);   // the header goes in at the end
        AudioRenderer.Start();
    }

    /// call once per captured frame
    public void Frame()
    {
        int n = AudioRenderer.GetSampleCountForCaptureFrame();
        if (n <= 0) return;
        var buf = new NativeArray<float>(n * _channels, Allocator.Temp);
        AudioRenderer.Render(buf);
        var bytes = new byte[buf.Length * 2];
        for (int i = 0; i < buf.Length; i++)
        {
            short v = (short)(Mathf.Clamp(buf[i], -1f, 1f) * 32767f);
            bytes[2 * i] = (byte)(v & 0xff);
            bytes[2 * i + 1] = (byte)((v >> 8) & 0xff);
        }
        buf.Dispose();
        _fs.Write(bytes, 0, bytes.Length);
        _samples += n;
    }

    public void Close()
    {
        AudioRenderer.Stop();
        long data = _samples * _channels * 2;
        _fs.Seek(0, SeekOrigin.Begin);
        var w = new BinaryWriter(_fs);
        w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); w.Write((int)(36 + data));
        w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE")); w.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        w.Write(16); w.Write((short)1); w.Write((short)_channels); w.Write(_rate);
        w.Write(_rate * _channels * 2); w.Write((short)(_channels * 2)); w.Write((short)16);
        w.Write(System.Text.Encoding.ASCII.GetBytes("data")); w.Write((int)data);
        w.Flush();
        _fs.Close();
    }
}
