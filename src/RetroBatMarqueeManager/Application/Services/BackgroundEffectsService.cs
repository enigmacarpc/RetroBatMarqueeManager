using System.Collections.Concurrent;
using System.Diagnostics;
using RetroBatMarqueeManager.Core.Interfaces;

namespace RetroBatMarqueeManager.Application.Services
{
    /// <summary>
    /// EN: Optional animated effects on top of a composed marquee (twinkling stars, glowing moon / big lights, flickering fire and warm lights).
    ///     The effect is found automatically from the bright spots of the background picture, rendered once into a short looping mp4 next to the
    ///     composed picture and then reused. The logo is kept crisp: the effect is only applied where the logo does not cover the background.
    /// </summary>
    public class BackgroundEffectsService
    {
        /// <summary>EN: When true (Ctrl+E), the render is awaited so the effect shows straight away; otherwise it is rendered in the background.</summary>
        public static readonly AsyncLocal<bool> WaitForRender = new AsyncLocal<bool>();

        private const int Fps = 25;
        private const int Seconds = 6;
        private const string Version = "v2";

        private readonly IConfigService _config;
        private readonly ImageConversionService _img;
        private readonly ILogger<BackgroundEffectsService> _logger;
        private readonly ConcurrentDictionary<string, Task> _running = new ConcurrentDictionary<string, Task>(StringComparer.OrdinalIgnoreCase);
        private string? _encoder;

        public BackgroundEffectsService(IConfigService config, ImageConversionService img, ILogger<BackgroundEffectsService> logger)
        {
            _config = config;
            _img = img;
            _logger = logger;
        }

        private string Signature(string composedPath)
        {
            var fi = new FileInfo(composedPath);
            return $"{Version}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}|{(_config.EffectStars ? 1 : 0)}{(_config.EffectMoon ? 1 : 0)}{(_config.EffectFire ? 1 : 0)}|{_config.EffectStrength}|{_config.MarqueeWidth}x{_config.MarqueeHeight}";
        }

        /// <summary>
        /// EN: Returns the effect video for this composed picture when it is ready. When it is not, it is rendered (awaited when
        ///     <see cref="WaitForRender"/> is set, otherwise in the background for next time) and null is returned.
        /// </summary>
        public async Task<string?> GetOrQueueAsync(string composedPath, Func<Task<string?>> getBackgroundOnly)
        {
            try
            {
                if (!_config.EffectStars && !_config.EffectMoon && !_config.EffectFire) return null;
                if (!File.Exists(composedPath)) return null;
                string mp4 = composedPath.Substring(0, composedPath.Length - 4) + ".fx.mp4";
                string sigPath = mp4 + ".sig";
                string sig = Signature(composedPath);
                if (File.Exists(mp4) && File.Exists(sigPath) && new FileInfo(mp4).Length > 1024 && File.ReadAllText(sigPath) == sig) return mp4;

                bool wait = WaitForRender.Value;
                var gate = new TaskCompletionSource<bool>();
                if (_running.TryAdd(composedPath, gate.Task))
                {
                    async Task Work()
                    {
                        try { await DoWork(composedPath, mp4, sig, getBackgroundOnly); }
                        finally { gate.TrySetResult(true); _running.TryRemove(composedPath, out _); }
                    }
                    if (wait) await Work(); else _ = Task.Run(Work);
                }
                else if (wait && _running.TryGetValue(composedPath, out var existing))
                {
                    await existing; // EN: a background render of this same picture is already running
                }
                if (wait && File.Exists(mp4) && File.Exists(sigPath) && File.ReadAllText(sigPath) == sig) return mp4;
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[Effects] {ex.Message}");
                return null;
            }
        }

        private async Task DoWork(string composedPath, string mp4, string sig, Func<Task<string?>> getBackgroundOnly)
        {
            try
            {
                string? bgOnly = await getBackgroundOnly();
                if (string.IsNullOrEmpty(bgOnly) || !File.Exists(bgOnly) || !bgOnly.EndsWith("-nologo_composed.png", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("[Effects] No plain background available for this game; effects skipped.");
                    return;
                }
                if (Render(composedPath, bgOnly, mp4)) File.WriteAllText(mp4 + ".sig", sig);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[Effects] Render failed: {ex.Message}");
            }
        }

        // ------------------------------------------------------------------ rendering

        private bool Render(string composedPath, string bgOnlyPath, string mp4)
        {
            int W = _config.MarqueeWidth, H = _config.MarqueeHeight;
            var sw = Stopwatch.StartNew();
            byte[]? composed = DecodeRaw(composedPath, W, H);
            byte[]? plain = DecodeRaw(bgOnlyPath, W, H);
            if (composed == null || plain == null) return false;

            var fx = new EffectsCore(W, H, plain, composed, _config.EffectStars, _config.EffectMoon, _config.EffectFire, _config.EffectStrength / 100f);
            _logger.LogInformation($"[Effects] Found: {fx.Summary}");
            int frames = Fps * Seconds;

            string tmp = mp4 + ".tmp.mp4";
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }

            var psi = new ProcessStartInfo
            {
                FileName = _img.GetFfmpegPath(),
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var a in new[] { "-y", "-loglevel", "error", "-f", "rawvideo", "-pix_fmt", "bgr24", "-s", $"{W}x{H}", "-r", Fps.ToString(), "-i", "-", "-an" }) psi.ArgumentList.Add(a);
            foreach (var a in EncoderArgs()) psi.ArgumentList.Add(a);
            foreach (var a in new[] { "-pix_fmt", "yuv420p", "-f", "mp4", tmp }) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p == null) return false;
            p.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) _logger.LogWarning($"[Effects ffmpeg] {e.Data}"); };
            p.BeginErrorReadLine();
            p.BeginOutputReadLine();

            try
            {
                var stream = p.StandardInput.BaseStream;
                var frame = new byte[W * H * 3];
                for (int i = 0; i < frames; i++)
                {
                    fx.RenderFrame(i, frames, frame);
                    stream.Write(frame, 0, frame.Length);
                }
                stream.Flush();
                p.StandardInput.Close();
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[Effects] Encoder pipe failed: {ex.Message}");
                try { p.Kill(); } catch { }
                return false;
            }

            if (!p.WaitForExit(60000)) { try { p.Kill(); } catch { } return false; }
            if (p.ExitCode != 0 || !File.Exists(tmp) || new FileInfo(tmp).Length < 1024) return false;

            File.Copy(tmp, mp4, true);
            try { File.Delete(tmp); } catch { }
            _logger.LogInformation($"[Effects] Rendered {Path.GetFileName(mp4)} in {sw.Elapsed.TotalSeconds:F1}s");
            return true;
        }

        private IEnumerable<string> EncoderArgs()
        {
            string hw = _config.FfmpegHwEncoding;
            if (!string.IsNullOrWhiteSpace(hw)) return new[] { "-c:v", hw };
            if (_encoder == null)
            {
                string list = "";
                try
                {
                    var psi = new ProcessStartInfo { FileName = _img.GetFfmpegPath(), UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                    psi.ArgumentList.Add("-hide_banner"); psi.ArgumentList.Add("-encoders");
                    using var p = Process.Start(psi);
                    if (p != null) { list = p.StandardOutput.ReadToEnd(); p.WaitForExit(10000); }
                }
                catch { }
                _encoder = list.Contains("libx264") ? "x264" : list.Contains("libopenh264") ? "openh264" : "mpeg4";
            }
            return _encoder switch
            {
                "x264" => new[] { "-c:v", "libx264", "-crf", "20", "-preset", "veryfast" },
                "openh264" => new[] { "-c:v", "libopenh264", "-b:v", "8M" },
                _ => new[] { "-c:v", "mpeg4", "-q:v", "3" }
            };
        }

        private byte[]? DecodeRaw(string path, int w, int h)
        {
            var psi = new ProcessStartInfo
            {
                FileName = _img.GetFfmpegPath(),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var a in new[] { "-v", "error", "-i", path, "-vf", $"scale={w}:{h}", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "bgr24", "pipe:1" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return null;
            p.ErrorDataReceived += (s, e) => { };
            p.BeginErrorReadLine();
            var buf = new byte[w * h * 3];
            int read = 0;
            var st = p.StandardOutput.BaseStream;
            while (read < buf.Length)
            {
                int n = st.Read(buf, read, buf.Length - read);
                if (n <= 0) break;
                read += n;
            }
            p.WaitForExit(10000);
            return read == buf.Length ? buf : null;
        }

        // ------------------------------------------------------------------ the effects themselves

        private sealed class EffectsCore
        {
            private readonly int _w, _h, _n;
            private readonly byte[] _plain, _composed;
            private readonly float[] _stars, _starGlow, _starPhase, _starSpeed, _moon, _moonHalo, _warm, _warmGlow, _warmPhase, _keep;
            private readonly bool[] _active;
            private readonly bool _doStars, _doMoon, _doFire;
            private readonly float _k;
            private readonly float[] _moonPhase;
            public string Summary { get; private set; } = "";

            public EffectsCore(int w, int h, byte[] plain, byte[] composed, bool stars, bool moon, bool fire, float strength)
            {
                _w = w; _h = h; _n = w * h; _plain = plain; _composed = composed;
                _doStars = stars; _doMoon = moon; _doFire = fire; _k = strength;
                _moonPhase = new float[w * h];
                var rng = new Random(3);

                var gray = new float[_n];
                for (int i = 0; i < _n; i++) gray[i] = 0.114f * plain[i * 3] + 0.587f * plain[i * 3 + 1] + 0.299f * plain[i * 3 + 2];

                _stars = new float[_n]; _starGlow = new float[_n]; _starPhase = new float[_n]; _starSpeed = new float[_n];
                _moon = new float[_n]; _moonHalo = new float[_n];
                _warm = new float[_n]; _warmGlow = new float[_n]; _warmPhase = new float[_n];

                if (stars)
                {
                    var opened = MinMax(MinMax(gray, w, h, 3, false), w, h, 3, true);
                    var surround = Gauss(gray, w, h, 14);
                    var s = new float[_n];
                    for (int i = 0; i < _n; i++) s[i] = (gray[i] - opened[i] > 20 && gray[i] > 80 && surround[i] < 140) ? 1f : 0f;
                    s = MinMax(s, w, h, 1, true);
                    Array.Copy(s, _stars, _n);
                    _starGlow = Gauss(_stars, w, h, 3);
                    var rnd = RandomField(rng, _n);
                    var z = Normalize(Gauss(rnd, w, h, 2));
                    for (int i = 0; i < _n; i++) { _starPhase[i] = z[i] * 2.0f; _starSpeed[i] = 1 + ((int)(Math.Abs(z[i]) * 2) % 3); }
                }

                if (moon)
                {
                    // EN: moon / lamps / big lights = clearly brighter than their surroundings (works on dark or vignetted backgrounds too).
                    var local = Gauss(gray, w, h, 30);
                    var big = new float[_n];
                    for (int i = 0; i < _n; i++) big[i] = (gray[i] - local[i] > 28 && gray[i] > 80) ? 1f : 0f;
                    big = MinMax(MinMax(big, w, h, 2, false), w, h, 2, true);
                    FilterComponents(big, w, h, 300, 6000, _moon);
                    _moonHalo = Gauss(_moon, w, h, 22);
                    var zr = Normalize(Gauss(RandomField(rng, _n), w, h, 40));
                    for (int i = 0; i < _n; i++) _moonPhase[i] = zr[i] * 1.6f; // lights pulse out of step with each other
                }

                if (fire)
                {
                    var warm = new float[_n];
                    for (int i = 0; i < _n; i++)
                    {
                        float b = plain[i * 3], g = plain[i * 3 + 1], r = plain[i * 3 + 2];
                        float mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
                        if (mx <= 90 || mx <= 0) continue;
                        float sat = 255f * (mx - mn) / mx;
                        if (sat <= 110) continue;
                        float hue; float d = mx - mn;
                        if (mx == r) hue = 60f * (((g - b) / d) % 6f);
                        else if (mx == g) hue = 60f * (((b - r) / d) + 2f);
                        else hue = 60f * (((r - g) / d) + 4f);
                        if (hue < 0) hue += 360f;
                        float h8 = hue / 2f; // OpenCV style 0..180
                        if (h8 >= 3 && h8 <= 32 && _moon[i] <= 0) warm[i] = 1f;
                    }
                    warm = MinMax(MinMax(warm, w, h, 1, false), w, h, 1, true);
                    _warm = Gauss(warm, w, h, 1.2);
                    _warmGlow = Gauss(_warm, w, h, 14);
                    var rnd = RandomField(rng, _n);
                    var z = Normalize(Gauss(rnd, w, h, 9));
                    for (int i = 0; i < _n; i++) _warmPhase[i] = z[i] * 2.2f;
                    // EN: huge warm areas (sunset skies) pulse gently as one piece instead of looking like lava.
                    var strong = new float[_n];
                    for (int i = 0; i < _n; i++) strong[i] = _warm[i] > 0.3f ? 1f : 0f;
                    var bigWarm = new float[_n];
                    FilterComponents(strong, w, h, 9000, int.MaxValue, bigWarm);
                    for (int i = 0; i < _n; i++) if (bigWarm[i] > 0) _warmPhase[i] = 0;
                }

                // EN: where the composed picture differs from the plain background the logo is drawn: keep that untouched.
                _keep = new float[_n];
                for (int i = 0; i < _n; i++)
                {
                    int d = Math.Abs(composed[i * 3] - plain[i * 3]) + Math.Abs(composed[i * 3 + 1] - plain[i * 3 + 1]) + Math.Abs(composed[i * 3 + 2] - plain[i * 3 + 2]);
                    _keep[i] = Math.Min(1f, d / 20f);
                }
                int cs = 0, cm = 0, cw = 0;
                for (int i = 0; i < _n; i++) { if (_stars[i] > 0) cs++; if (_moon[i] > 0) cm++; if (_warm[i] > 0.3f) cw++; }
                Summary = $"stars {cs}px, glowing lights {cm}px, warm/fire {cw}px";
                _active = new bool[_n];
                for (int i = 0; i < _n; i++)
                    _active[i] = _stars[i] > 0 || _starGlow[i] > 0.002f || _moon[i] > 0 || _moonHalo[i] > 0.002f || _warm[i] > 0.002f || _warmGlow[i] > 0.002f;
            }

            public void RenderFrame(int i, int frames, byte[] outBuf)
            {
                double a = 2 * Math.PI * i / frames;
                float pulse = (float)(0.5 + 0.5 * Math.Sin(a));
                float stutter = (i % (frames / 2)) < (frames / 2) - 7 ? 1f : 0.55f;
                int w = _w;
                Buffer.BlockCopy(_composed, 0, outBuf, 0, _n * 3);
                System.Threading.Tasks.Parallel.For(0, _h, y =>
                {
                    for (int x = 0; x < w; x++)
                    {
                        int p = y * w + x;
                        if (!_active[p]) continue;
                        float keepLogo = 1f - _keep[p];
                        if (keepLogo <= 0.001f) continue;
                        float f0b = _plain[p * 3], f0g = _plain[p * 3 + 1], f0r = _plain[p * 3 + 2];
                        float b = f0b, g = f0g, r = f0r;

                        if (_doStars && (_stars[p] > 0 || _starGlow[p] > 0.002f))
                        {
                            float tw = (float)(0.5 + 0.5 * Math.Sin(_starSpeed[p] * a + _starPhase[p]));
                            float dim = 1f - _stars[p] * Math.Min(0.85f, 0.45f * _k) * (1f - tw);
                            float add = _stars[p] * tw * 70f * _k + _starGlow[p] * tw * 60f * _k;
                            b = b * dim + add; g = g * dim + add; r = r * dim + add;
                        }
                        if (_doMoon && (_moon[p] > 0 || _moonHalo[p] > 0.002f))
                        {
                            float lp = (float)(0.5 + 0.5 * Math.Sin(a + _moonPhase[p]));
                            float halo = _moonHalo[p] * (35f + 55f * lp) * _k;
                            b += halo * 0.85f; g += halo * 0.97f; r += halo * 1.0f;
                            float gain = 1f + _moon[p] * 0.06f * lp * _k;
                            b *= gain; g *= gain; r *= gain;
                        }
                        if (_doFire && (_warm[p] > 0.002f || _warmGlow[p] > 0.002f))
                        {
                            float ph = _warmPhase[p];
                            float fl = 1f + _k * (0.22f * (float)Math.Sin(3 * a + ph) + 0.12f * (float)Math.Sin(5 * a + 1.7 * ph));
                            float gain = 1f + _warm[p] * (fl - 1f) * stutter;
                            float glow = _warmGlow[p] * (45f + 30f * (float)Math.Sin(3 * a + ph)) * _k * stutter;
                            b = b * gain + glow * 0.2f; g = g * gain + glow * 0.55f; r = r * gain + glow * 1.0f;
                        }

                        int o = p * 3;
                        outBuf[o] = Clamp(_composed[o] + (b - f0b) * keepLogo);
                        outBuf[o + 1] = Clamp(_composed[o + 1] + (g - f0g) * keepLogo);
                        outBuf[o + 2] = Clamp(_composed[o + 2] + (r - f0r) * keepLogo);
                    }
                });
            }

            private static byte Clamp(float v) => v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)(v + 0.5f);

            private static float[] RandomField(Random rng, int n)
            {
                var f = new float[n];
                for (int i = 0; i < n; i++) f[i] = (float)rng.NextDouble();
                return f;
            }

            private static float[] Normalize(float[] f)
            {
                double mean = 0; foreach (var v in f) mean += v; mean /= f.Length;
                double variance = 0; foreach (var v in f) variance += (v - mean) * (v - mean); variance /= f.Length;
                double sd = Math.Sqrt(variance) + 1e-9;
                var o = new float[f.Length];
                for (int i = 0; i < f.Length; i++) o[i] = (float)((f[i] - mean) / sd);
                return o;
            }

            // Approximate gaussian blur: three box blurs.
            private static float[] Gauss(float[] src, int w, int h, double sigma)
            {
                double wBox = Math.Sqrt(4 * sigma * sigma + 1);
                int r = Math.Max(1, (int)((wBox - 1) / 2));
                var a = (float[])src.Clone();
                var b = new float[a.Length];
                for (int pass = 0; pass < 3; pass++)
                {
                    BoxH(a, b, w, h, r);
                    BoxV(b, a, w, h, r);
                }
                return a;
            }

            private static void BoxH(float[] src, float[] dst, int w, int h, int r)
            {
                System.Threading.Tasks.Parallel.For(0, h, y =>
                {
                    var pre = new double[w + 1];
                    int row = y * w;
                    for (int x = 0; x < w; x++) pre[x + 1] = pre[x] + src[row + x];
                    for (int x = 0; x < w; x++)
                    {
                        int lo = Math.Max(0, x - r), hi = Math.Min(w - 1, x + r);
                        dst[row + x] = (float)((pre[hi + 1] - pre[lo]) / (hi - lo + 1));
                    }
                });
            }

            private static void BoxV(float[] src, float[] dst, int w, int h, int r)
            {
                System.Threading.Tasks.Parallel.For(0, w, x =>
                {
                    var pre = new double[h + 1];
                    for (int y = 0; y < h; y++) pre[y + 1] = pre[y] + src[y * w + x];
                    for (int y = 0; y < h; y++)
                    {
                        int lo = Math.Max(0, y - r), hi = Math.Min(h - 1, y + r);
                        dst[y * w + x] = (float)((pre[hi + 1] - pre[lo]) / (hi - lo + 1));
                    }
                });
            }

            // Separable min (erode) / max (dilate) filter with a (2r+1) square window.
            private static float[] MinMax(float[] src, int w, int h, int r, bool max)
            {
                var tmp = new float[src.Length];
                var dst = new float[src.Length];
                System.Threading.Tasks.Parallel.For(0, h, y =>
                {
                    int row = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        int lo = Math.Max(0, x - r), hi = Math.Min(w - 1, x + r);
                        float v = src[row + lo];
                        for (int k = lo + 1; k <= hi; k++) { float s = src[row + k]; if (max ? s > v : s < v) v = s; }
                        tmp[row + x] = v;
                    }
                });
                System.Threading.Tasks.Parallel.For(0, w, x =>
                {
                    for (int y = 0; y < h; y++)
                    {
                        int lo = Math.Max(0, y - r), hi = Math.Min(h - 1, y + r);
                        float v = tmp[lo * w + x];
                        for (int k = lo + 1; k <= hi; k++) { float s = tmp[k * w + x]; if (max ? s > v : s < v) v = s; }
                        dst[y * w + x] = v;
                    }
                });
                return dst;
            }

            // Copies into dst (value 1) only the connected areas of src whose pixel count is within [minArea, maxArea].
            private static void FilterComponents(float[] src, int w, int h, int minArea, int maxArea, float[] dst)
            {
                var seen = new bool[src.Length];
                var stack = new Stack<int>();
                var members = new List<int>();
                for (int start = 0; start < src.Length; start++)
                {
                    if (src[start] <= 0.5f || seen[start]) continue;
                    members.Clear();
                    stack.Push(start); seen[start] = true;
                    while (stack.Count > 0)
                    {
                        int c = stack.Pop(); members.Add(c);
                        int cx = c % w, cy = c / w;
                        if (cx > 0 && src[c - 1] > 0.5f && !seen[c - 1]) { seen[c - 1] = true; stack.Push(c - 1); }
                        if (cx < w - 1 && src[c + 1] > 0.5f && !seen[c + 1]) { seen[c + 1] = true; stack.Push(c + 1); }
                        if (cy > 0 && src[c - w] > 0.5f && !seen[c - w]) { seen[c - w] = true; stack.Push(c - w); }
                        if (cy < h - 1 && src[c + w] > 0.5f && !seen[c + w]) { seen[c + w] = true; stack.Push(c + w); }
                    }
                    if (members.Count >= minArea && members.Count <= maxArea) foreach (var m in members) dst[m] = 1f;
                }
            }
        }
    }
}
