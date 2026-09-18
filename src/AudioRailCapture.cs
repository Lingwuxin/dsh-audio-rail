// AudioRailCapture — WASAPI loopback capture + FFT band reducer for the
// dsh-audio-rail plugin. Captures the default render device's mix (what the
// user hears), computes 12 log-spaced magnitude bands at ~30 fps and prints
// one JSON line per frame to stdout:
//   {"ready":true,"rate":48000,"channels":2,"bands":12}
//   {"b":[0.12,0.34,...],"l":0.56}
// Errors go to stderr; exit code 2 = fatal (host should not restart),
// 3 = device invalidated/lost (host may restart with backoff).
//
// COM calls go through raw vtable delegates: this machine's runtime rejects
// IID_IAudioClient via marshaled Activate (E_NOINTERFACE), while
// IID_IAudioClient3/2 activate fine — so we probe 3 → 2 → 1.
//
// Build (C# 5 syntax, .NET Framework 4.x, no dependencies):
//   %WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /optimize+ /target:exe /out:AudioRailCapture.exe AudioRailCapture.cs

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace AudioRailCapture
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal class MMDeviceEnumeratorComObject { }

    internal enum EDataFlow { eRender = 0, eCapture = 1, eAll = 2 }
    internal enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }

    // The enumerator's own marshaled interface works fine on this runtime;
    // everything below the device goes through raw vtable delegates.
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(EDataFlow dataFlow, int stateMask, out IntPtr ppDevices);
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IntPtr ppDevice);
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr ppDevice);
        int RegisterEndpointNotificationCallback(IntPtr client);
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    // ---- Raw vtable delegates (IUnknown slots 0-2, then interface order) ----
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int ActivateFn(IntPtr self, IntPtr iid, int clsCtx, IntPtr activationParams, out IntPtr iface);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int InitializeFn(IntPtr self, int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int GetBufferSizeFn(IntPtr self, out uint bufferFrames);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int GetMixFormatFn(IntPtr self, out IntPtr format);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int StartStopFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int GetServiceFn(IntPtr self, IntPtr riid, out IntPtr service);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int GetBufferFn(IntPtr self, out IntPtr data, out uint frames, out int flags, out ulong devicePosition, out ulong qpcPosition);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int ReleaseBufferFn(IntPtr self, uint framesRead);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int GetNextPacketSizeFn(IntPtr self, out uint framesInNextPacket);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int ReleaseFn(IntPtr self);

    internal static class Program
    {
        private const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
        private const int AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
        private const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);
        private const int CLSCTX_ALL = 0x17;

        private const int BAND_COUNT = 12;
        private const int EMIT_MS = 33;       // ~30 fps output cadence
        private static int fftSize = 2048;    // scaled to the mix rate in InitDsp
        private static int hop = 512;         // min new samples between FFT frames
        private static int ringSize = 8192;   // mono sample ring (power of two)

        // This runtime refuses IID_IAudioClient; prefer the newer IIDs whose
        // activated objects share the same base vtable layout we call.
        private static readonly string[] AudioClientIids = {
            "7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42", // IID_IAudioClient3
            "726778CD-F60A-4EDA-82DE-E47610CD78AA", // IID_IAudioClient2
            "1CB0ADCD-84A5-4B70-8065-68B03CF3E95C", // IID_IAudioClient
        };
        private static readonly Guid IID_IAudioCaptureClient = new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

        // FFT scratch
        private static double[] fftRe;
        private static double[] fftIm;
        private static double[] hann;
        private static int[] bitRev;
        private static int[] bandLoBin;
        private static int[] bandHiBin;

        // Per-band normalization state: a slow moving average as the loudness
        // reference — sustained content sits at midline, transients punch
        // above it, quiet passages dip below. A peak-follower would pin the
        // bars at maximum for any steady music.
        private static readonly double[] avgDb = new double[BAND_COUNT];
        private static readonly double[] smooth = new double[BAND_COUNT];
        private static double levelAvgDb = -999;

        [STAThread]
        private static int Main()
        {
            Console.OutputEncoding = Encoding.UTF8;
            TextWriter stdout = Console.Out;
            try
            {
                return Run(stdout);
            }
            catch (WasapiException ex)
            {
                Console.Error.WriteLine("audio-rail-capture: " + ex.Message);
                return ex.HResult == AUDCLNT_E_DEVICE_INVALIDATED ? 3 : 2;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("audio-rail-capture: fatal: " + ex.GetType().Name + ": " + ex.Message);
                return 2;
            }
        }

        private static IntPtr Vslot(IntPtr obj, int slot)
        {
            return Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), slot * IntPtr.Size);
        }

        private static T Fn<T>(IntPtr obj, int slot)
        {
            return (T)(object)Marshal.GetDelegateForFunctionPointer(Vslot(obj, slot), typeof(T));
        }

        private static int Run(TextWriter stdout)
        {
            IntPtr device = IntPtr.Zero;
            IntPtr client = IntPtr.Zero;
            IntPtr capture = IntPtr.Zero;
            IntPtr mixFormat = IntPtr.Zero;
            IMMDeviceEnumerator enumerator = null;
            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                Check(enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eConsole, out device), "GetDefaultAudioEndpoint");

                // IMMDevice::Activate = vtable slot 3
                var activate = Fn<ActivateFn>(device, 3);
                foreach (string iidStr in AudioClientIids)
                {
                    IntPtr pIid = GuidOnStack(new Guid(iidStr));
                    try
                    {
                        int hr = activate(device, pIid, CLSCTX_ALL, IntPtr.Zero, out client);
                        if (hr == 0 && client != IntPtr.Zero) break;
                        if (hr == AUDCLNT_E_DEVICE_INVALIDATED) throw new WasapiException(hr, "Activate");
                    }
                    finally { Marshal.FreeCoTaskMem(pIid); }
                }
                if (client == IntPtr.Zero)
                    throw new WasapiException(unchecked((int)0x80004002), "Activate(IAudioClient3/2/1)");

                // IAudioClient::GetMixFormat = slot 8
                Check(Fn<GetMixFormatFn>(client, 8)(client, out mixFormat), "GetMixFormat");

                short tag = Marshal.ReadInt16(mixFormat, 0);
                int channels = Marshal.ReadInt16(mixFormat, 2);
                int rate = Marshal.ReadInt32(mixFormat, 4);
                int bits = Marshal.ReadInt16(mixFormat, 14);
                if (tag == -2) // WAVE_FORMAT_EXTENSIBLE: SubFormat GUID at offset 24, first dword = tag
                    tag = (short)Marshal.ReadInt32(mixFormat, 24);
                bool isFloat;
                if (tag == 3 && bits == 32) isFloat = true;                 // IEEE float32
                else if (tag == 1 && bits == 16) isFloat = false;           // PCM16
                else
                {
                    StringBuilder hex = new StringBuilder();
                    for (int i = 0; i < 48; i++) hex.Append(Marshal.ReadByte(mixFormat, i).ToString("X2") + " ");
                    throw new InvalidOperationException("unsupported mix format tag=" + tag + " bits=" + bits + " bytes=" + hex);
                }
                if (channels < 1 || channels > 8 || rate < 8000)
                    throw new InvalidOperationException("unsupported mix format channels=" + channels + " rate=" + rate);

                // IAudioClient::Initialize = slot 3 (shared mode + loopback, 100 ms buffer)
                Check(Fn<InitializeFn>(client, 3)(client, 0, AUDCLNT_STREAMFLAGS_LOOPBACK, 1000000, 0, mixFormat, IntPtr.Zero), "Initialize(loopback)");
                uint bufferFrames;
                Check(Fn<GetBufferSizeFn>(client, 4)(client, out bufferFrames), "GetBufferSize");

                // IAudioClient::GetService = slot 14
                IntPtr pCapIid = GuidOnStack(IID_IAudioCaptureClient);
                try
                {
                    Check(Fn<GetServiceFn>(client, 14)(client, pCapIid, out capture), "GetService(IAudioCaptureClient)");
                }
                finally { Marshal.FreeCoTaskMem(pCapIid); }

                InitDsp(rate);

                float[] floatBuf = new float[bufferFrames * channels];
                short[] shortBuf = isFloat ? null : new short[bufferFrames * channels];
                double[] ring = new double[ringSize];
                long ringWrite = 0;
                long ringFilled = 0;
                int newSamples = 0;
                long lastDataMs = 0;

                stdout.WriteLine("{\"ready\":true,\"rate\":" + rate + ",\"channels\":" + channels + ",\"bands\":" + BAND_COUNT + "}");
                stdout.Flush();

                // IAudioClient::Start = slot 10
                Check(Fn<StartStopFn>(client, 10)(client), "Start");
                var getNextPacket = Fn<GetNextPacketSizeFn>(capture, 5);
                var getBuffer = Fn<GetBufferFn>(capture, 3);
                var releaseBuffer = Fn<ReleaseBufferFn>(capture, 4);

                Stopwatch clock = Stopwatch.StartNew();
                long nextEmit = 0;

                while (true)
                {
                    uint packet;
                    Check(getNextPacket(capture, out packet), "GetNextPacketSize");
                    while (packet > 0)
                    {
                        IntPtr data;
                        uint frames;
                        int flags;
                        ulong devPos, qpc;
                        Check(getBuffer(capture, out data, out frames, out flags, out devPos, out qpc), "GetBuffer");
                        int count = (int)frames * channels;
                        bool silent = (flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0 || data == IntPtr.Zero;
                        if (!silent)
                        {
                            if (isFloat) Marshal.Copy(data, floatBuf, 0, count);
                            else Marshal.Copy(data, shortBuf, 0, count);
                        }
                        for (int f = 0; f < (int)frames; f++)
                        {
                            double s = 0;
                            if (!silent)
                            {
                                int baseIdx = f * channels;
                                if (isFloat) { for (int ch = 0; ch < channels; ch++) s += floatBuf[baseIdx + ch]; }
                                else { for (int ch = 0; ch < channels; ch++) s += shortBuf[baseIdx + ch] / 32768.0; }
                                s /= channels;
                            }
                            ring[(int)(ringWrite & (ringSize - 1))] = s;
                            ringWrite++;
                            if (ringFilled < ringSize) ringFilled++;
                        }
                        newSamples += (int)frames;
                        if (frames > 0) lastDataMs = clock.ElapsedMilliseconds;
                        Check(releaseBuffer(capture, frames), "ReleaseBuffer");
                        Check(getNextPacket(capture, out packet), "GetNextPacketSize");
                    }

                    long now = clock.ElapsedMilliseconds;
                    if (now >= nextEmit)
                    {
                        nextEmit = now + EMIT_MS;
                        if (newSamples >= hop && ringFilled >= fftSize)
                        {
                            newSamples = 0;
                            EmitFrame(stdout, ring, ringWrite);
                        }
                        else if (now - lastDataMs > 120)
                        {
                            // Capture running dry (paused player / silent mix): decay to zero.
                            DecayFrame(stdout);
                        }
                    }
                    Thread.Sleep(8);
                }
            }
            finally
            {
                try { if (client != IntPtr.Zero) Fn<StartStopFn>(client, 11)(client); } catch { }
                if (mixFormat != IntPtr.Zero) Marshal.FreeCoTaskMem(mixFormat);
                if (capture != IntPtr.Zero) Fn<ReleaseFn>(capture, 2)(capture);
                if (client != IntPtr.Zero) Fn<ReleaseFn>(client, 2)(client);
                if (device != IntPtr.Zero) Fn<ReleaseFn>(device, 2)(device);
                if (enumerator != null) Marshal.ReleaseComObject(enumerator);
            }
        }

        private static IntPtr GuidOnStack(Guid guid)
        {
            IntPtr ptr = Marshal.AllocCoTaskMem(16);
            Marshal.Copy(guid.ToByteArray(), 0, ptr, 16);
            return ptr;
        }

        // ---- DSP ----

        private static void InitDsp(int rate)
        {
            // Keep the FFT bin resolution near 23 Hz regardless of the device
            // rate (a 192 kHz studio device would otherwise smear the bass).
            fftSize = 2048;
            while (rate / fftSize > 24) fftSize <<= 1; // 44.1/48k -> 2048, 96k -> 4096, 192k -> 8192
            ringSize = fftSize * 4;
            hop = Math.Max(512, rate / 64); // ~16 ms of fresh audio per frame

            fftRe = new double[fftSize];
            fftIm = new double[fftSize];
            hann = new double[fftSize];
            for (int i = 0; i < fftSize; i++)
                hann[i] = 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / (fftSize - 1)));

            bitRev = new int[fftSize];
            int bitsCount = 0;
            for (int n = fftSize; n > 1; n >>= 1) bitsCount++;
            for (int i = 0; i < fftSize; i++)
            {
                int r = 0;
                for (int b = 0; b < bitsCount; b++) r |= ((i >> b) & 1) << (bitsCount - 1 - b);
                bitRev[i] = r;
            }

            double nyquist = rate / 2.0;
            double fLo = 45.0;
            double fHi = Math.Min(16000.0, nyquist * 0.9);
            double ratio = fHi / fLo;
            double binHz = rate / (double)fftSize;
            bandLoBin = new int[BAND_COUNT];
            bandHiBin = new int[BAND_COUNT];
            for (int band = 0; band < BAND_COUNT; band++)
            {
                double lo = fLo * Math.Pow(ratio, band / (double)BAND_COUNT);
                double hi = fLo * Math.Pow(ratio, (band + 1) / (double)BAND_COUNT);
                int loBin = (int)Math.Floor(lo / binHz);
                int hiBin = (int)Math.Ceiling(hi / binHz);
                if (loBin < 1) loBin = 1; // skip DC
                if (hiBin <= loBin) hiBin = loBin + 1;
                if (hiBin > fftSize / 2) hiBin = fftSize / 2;
                bandLoBin[band] = loBin;
                bandHiBin[band] = hiBin;
                avgDb[band] = -999; // sentinel: seed from the first frame
                smooth[band] = 0;
            }
        }

        private static void EmitFrame(TextWriter stdout, double[] ring, long ringWrite)
        {
            // Window the latest fftSize mono samples (oldest -> newest).
            long start = ringWrite - fftSize;
            double energy = 0;
            for (int i = 0; i < fftSize; i++)
            {
                double s = ring[(int)((start + i) & (ringSize - 1))];
                energy += s * s;
                fftRe[i] = s * hann[i];
                fftIm[i] = 0;
            }
            FftInplace();
            double rmsDb = 20.0 * Math.Log10(Math.Sqrt(energy / fftSize) + 1e-12);
            if (levelAvgDb < -200) levelAvgDb = rmsDb;
            if (rmsDb > -72) levelAvgDb += (rmsDb - levelAvgDb) * 0.03; // ~1.1 s tau at 30 fps
            double level = Clamp01((rmsDb - levelAvgDb + 9.0) / 26.0);
            if (rmsDb < -72) level = 0;

            StringBuilder sb = new StringBuilder(160);
            sb.Append("{\"b\":[");
            for (int band = 0; band < BAND_COUNT; band++)
            {
                double sum = 0;
                int lo = bandLoBin[band], hi = bandHiBin[band];
                for (int k = lo; k < hi; k++)
                {
                    double re = fftRe[k], im = fftIm[k];
                    sum += Math.Sqrt(re * re + im * im);
                }
                double mag = sum / (hi - lo) * (2.0 / fftSize) * 2.0; // normalize + Hann coherent gain
                double db = 20.0 * Math.Log10(mag + 1e-12);
                if (avgDb[band] < -200) avgDb[band] = db;
                if (db > -72) avgDb[band] += (db - avgDb[band]) * 0.03; // slow loudness reference
                double v = (db - avgDb[band] + 9.0) / 26.0; // midline ~0.35, +17 dB transient saturates
                if (db < -72) v = 0;
                v = Clamp01(v);
                smooth[band] = v > smooth[band] ? v : smooth[band] * 0.72 + v * 0.28;
                if (band > 0) sb.Append(',');
                sb.Append(smooth[band].ToString("0.###", CultureInfo.InvariantCulture));
            }
            sb.Append("],\"l\":");
            sb.Append(level.ToString("0.###", CultureInfo.InvariantCulture));
            sb.Append('}');
            stdout.WriteLine(sb.ToString());
            stdout.Flush();
        }

        private static void DecayFrame(TextWriter stdout)
        {
            bool anyAlive = false;
            StringBuilder sb = new StringBuilder(160);
            sb.Append("{\"b\":[");
            for (int band = 0; band < BAND_COUNT; band++)
            {
                smooth[band] *= 0.75;
                if (smooth[band] > 0.003) anyAlive = true;
                if (band > 0) sb.Append(',');
                sb.Append(smooth[band].ToString("0.###", CultureInfo.InvariantCulture));
            }
            sb.Append("],\"l\":0}");
            if (anyAlive) { stdout.WriteLine(sb.ToString()); stdout.Flush(); }
        }

        private static void FftInplace()
        {
            for (int i = 0; i < fftSize; i++)
            {
                int r = bitRev[i];
                if (r > i)
                {
                    double tr = fftRe[i]; fftRe[i] = fftRe[r]; fftRe[r] = tr;
                    double ti = fftIm[i]; fftIm[i] = fftIm[r]; fftIm[r] = ti;
                }
            }
            for (int size = 2; size <= fftSize; size <<= 1)
            {
                int half = size >> 1;
                double ang = -2.0 * Math.PI / size;
                double wpr = Math.Cos(ang), wpi = Math.Sin(ang);
                for (int baseIdx = 0; baseIdx < fftSize; baseIdx += size)
                {
                    double wr = 1, wi = 0;
                    for (int j = 0; j < half; j++)
                    {
                        int even = baseIdx + j, odd = baseIdx + j + half;
                        double or = fftRe[odd] * wr - fftIm[odd] * wi;
                        double oi = fftRe[odd] * wi + fftIm[odd] * wr;
                        fftRe[odd] = fftRe[even] - or;
                        fftIm[odd] = fftIm[even] - oi;
                        fftRe[even] += or;
                        fftIm[even] += oi;
                        double nwr = wr * wpr - wi * wpi;
                        wi = wr * wpi + wi * wpr;
                        wr = nwr;
                    }
                }
            }
        }

        private static double Clamp01(double v)
        {
            if (v < 0) return 0;
            if (v > 1) return 1;
            return v;
        }

        private static void Check(int hr, string what)
        {
            if (hr < 0) throw new WasapiException(hr, what);
        }

        private sealed class WasapiException : Exception
        {
            internal new readonly int HResult;
            internal WasapiException(int hr, string what)
                : base(what + " failed: 0x" + hr.ToString("X8", CultureInfo.InvariantCulture))
            {
                HResult = hr;
            }
        }
    }
}
