// =====================================================================
//  KinectBridge —— 把 Kinect v2 的骨骼与画面推送给浏览器里的挥砍水果游戏
//
//  提供两个接口（默认端口 8181）：
//    ws://127.0.0.1:8181/ws      骨骼数据，JSON，30fps
//    http://127.0.0.1:8181/video 实时画面，MJPEG，可直接用浏览器打开自检
//
//  运行环境：Windows 8 及以上 + Kinect for Windows SDK 2.0
//  编译：直接运行 build.bat，不需要安装 Visual Studio
//
//  启动参数：
//    --port 8181       监听端口
//    --width 960       画面输出宽度（越小越省 CPU）
//    --fps 20          画面帧率（骨骼始终 30fps，不受影响）
//    --quality 70      JPEG 质量 1-100
//    --cutout          抠像模式：只显示人物，背景填成纯色
//    --bg 0B1F26       抠像模式的背景色（十六进制）
//    --nowindow        不显示控制台窗口日志
// =====================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Kinect;

namespace KinectBridge
{
    class Program
    {
        // ---------- 设定 ----------
        static int Port = 8181;
        static int OutWidth = 960;
        static int VideoFps = 20;
        static long JpegQuality = 70;
        static bool Cutout = false;
        static Color BgColor = ColorTranslator.FromHtml("#0B1F26");
        static bool Quiet = false;

        // ---------- Kinect ----------
        static KinectSensor sensor;
        static MultiSourceFrameReader reader;
        static CoordinateMapper mapper;

        static byte[] colorPixels;      // 1920x1080 BGRA
        static ushort[] depthData;      // 512x424
        static byte[] bodyIndexData;    // 512x424
        static Body[] bodies;

        static int colorW, colorH, depthW, depthH;

        // ---------- 共享输出 ----------
        static readonly object jpegLock = new object();
        static byte[] latestJpeg;
        static long jpegSeq;

        static readonly object skelLock = new object();
        static string latestSkeleton = "{\"body\":null}";

        static ImageCodecInfo jpegCodec;
        static EncoderParameters encoderParams;

        // =================================================================
        static void Main(string[] args)
        {
            ParseArgs(args);
            Console.Title = "KinectBridge";
            Log("KinectBridge 启动中…");

            if (!StartKinect())
            {
                Log("错误：找不到 Kinect 传感器。");
                Log("请检查：1) Kinect v2 电源适配器是否插好  2) 是否已安装 Kinect for Windows SDK 2.0");
                Log("按任意键退出。");
                Console.ReadKey();
                return;
            }

            jpegCodec = GetCodec("image/jpeg");
            encoderParams = new EncoderParameters(1);
            // 必须写全名：System.Text 里也有一个 Encoder，直接写会冲突
            encoderParams.Param[0] = new EncoderParameter(
                System.Drawing.Imaging.Encoder.Quality, JpegQuality);

            StartHttp();

            Log("");
            Log("  骨骼数据  ws://127.0.0.1:" + Port + "/ws");
            Log("  实时画面  http://127.0.0.1:" + Port + "/video");
            Log("  抠像模式  " + (Cutout ? "开（只显示人物）" : "关（显示完整画面）"));
            Log("");
            Log("保持本窗口开着，然后打开游戏页面即可。按 Ctrl+C 退出。");

            Thread.Sleep(Timeout.Infinite);
        }

        static void ParseArgs(string[] a)
        {
            for (int i = 0; i < a.Length; i++)
            {
                string k = a[i].ToLowerInvariant();
                if (k == "--port" && i + 1 < a.Length) Port = int.Parse(a[++i]);
                else if (k == "--width" && i + 1 < a.Length) OutWidth = int.Parse(a[++i]);
                else if (k == "--fps" && i + 1 < a.Length) VideoFps = Math.Max(5, int.Parse(a[++i]));
                else if (k == "--quality" && i + 1 < a.Length) JpegQuality = long.Parse(a[++i]);
                else if (k == "--cutout") Cutout = true;
                else if (k == "--bg" && i + 1 < a.Length) BgColor = ColorTranslator.FromHtml("#" + a[++i].TrimStart('#'));
                else if (k == "--nowindow") Quiet = true;
            }
        }

        static void Log(string s)
        {
            if (!Quiet) Console.WriteLine(s);
        }

        // =================================================================
        //  Kinect
        // =================================================================
        static bool StartKinect()
        {
            sensor = KinectSensor.GetDefault();
            if (sensor == null) return false;

            mapper = sensor.CoordinateMapper;

            FrameDescription cd = sensor.ColorFrameSource.CreateFrameDescription(ColorImageFormat.Bgra);
            colorW = cd.Width; colorH = cd.Height;
            colorPixels = new byte[colorW * colorH * 4];

            FrameDescription dd = sensor.DepthFrameSource.FrameDescription;
            depthW = dd.Width; depthH = dd.Height;
            depthData = new ushort[depthW * depthH];
            bodyIndexData = new byte[depthW * depthH];

            bodies = new Body[sensor.BodyFrameSource.BodyCount];

            reader = sensor.OpenMultiSourceFrameReader(
                FrameSourceTypes.Color | FrameSourceTypes.Depth |
                FrameSourceTypes.BodyIndex | FrameSourceTypes.Body);
            reader.MultiSourceFrameArrived += OnFrame;

            sensor.Open();

            // 等传感器就绪
            for (int i = 0; i < 60 && !sensor.IsAvailable; i++) Thread.Sleep(100);
            return true;
        }

        static long lastVideoTick;

        static void OnFrame(object sender, MultiSourceFrameArrivedEventArgs e)
        {
            MultiSourceFrame msf = e.FrameReference.AcquireFrame();
            if (msf == null) return;

            // ---- 骨骼：每帧都处理，游戏手感靠它 ----
            using (BodyFrame bf = msf.BodyFrameReference.AcquireFrame())
            {
                if (bf != null)
                {
                    bf.GetAndRefreshBodyData(bodies);
                    string json = BuildSkeletonJson();
                    lock (skelLock) { latestSkeleton = json; }
                }
            }

            // ---- 画面：按设定帧率抽帧，省 CPU ----
            long now = Environment.TickCount;
            bool wantVideo = now - lastVideoTick >= 1000 / VideoFps;
            if (!wantVideo) return;
            lastVideoTick = now;

            bool haveColor = false, haveDepth = false, haveIndex = false;

            using (ColorFrame cf = msf.ColorFrameReference.AcquireFrame())
            {
                if (cf != null) { cf.CopyConvertedFrameDataToArray(colorPixels, ColorImageFormat.Bgra); haveColor = true; }
            }
            if (Cutout)
            {
                using (DepthFrame df = msf.DepthFrameReference.AcquireFrame())
                {
                    if (df != null) { df.CopyFrameDataToArray(depthData); haveDepth = true; }
                }
                using (BodyIndexFrame bif = msf.BodyIndexFrameReference.AcquireFrame())
                {
                    if (bif != null) { bif.CopyFrameDataToArray(bodyIndexData); haveIndex = true; }
                }
            }

            if (!haveColor) return;

            try
            {
                byte[] jpg = (Cutout && haveDepth && haveIndex) ? RenderCutout() : RenderColor();
                if (jpg != null)
                {
                    lock (jpegLock) { latestJpeg = jpg; jpegSeq++; }
                }
            }
            catch (Exception ex) { Log("画面编码出错: " + ex.Message); }
        }

        // 完整彩色画面，缩放到 OutWidth
        static byte[] RenderColor()
        {
            int w = OutWidth, h = (int)Math.Round(OutWidth * (double)colorH / colorW);
            using (Bitmap src = new Bitmap(colorW, colorH, PixelFormat.Format32bppRgb))
            {
                BitmapData bd = src.LockBits(new Rectangle(0, 0, colorW, colorH),
                                             ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                Marshal.Copy(colorPixels, 0, bd.Scan0, colorPixels.Length);
                src.UnlockBits(bd);
                return Scale(src, w, h);
            }
        }

        // 抠像：只画属于人体的像素，其余填背景色。
        // 在深度空间（512x424）里做，比在 1920x1080 里逐点映射便宜 10 倍。
        static byte[] RenderCutout()
        {
            ColorSpacePoint[] map = new ColorSpacePoint[depthW * depthH];
            mapper.MapDepthFrameToColorSpace(depthData, map);

            byte[] outPix = new byte[depthW * depthH * 4];
            byte bgB = BgColor.B, bgG = BgColor.G, bgR = BgColor.R;

            for (int i = 0; i < depthW * depthH; i++)
            {
                int o = i * 4;
                if (bodyIndexData[i] != 0xFF)   // 0xFF = 不属于任何人
                {
                    float cx = map[i].X, cy = map[i].Y;
                    if (!float.IsNegativeInfinity(cx) && !float.IsNegativeInfinity(cy))
                    {
                        int px = (int)(cx + 0.5f), py = (int)(cy + 0.5f);
                        if (px >= 0 && px < colorW && py >= 0 && py < colorH)
                        {
                            int s = (py * colorW + px) * 4;
                            outPix[o] = colorPixels[s];
                            outPix[o + 1] = colorPixels[s + 1];
                            outPix[o + 2] = colorPixels[s + 2];
                            outPix[o + 3] = 255;
                            continue;
                        }
                    }
                }
                outPix[o] = bgB; outPix[o + 1] = bgG; outPix[o + 2] = bgR; outPix[o + 3] = 255;
            }

            using (Bitmap src = new Bitmap(depthW, depthH, PixelFormat.Format32bppRgb))
            {
                BitmapData bd = src.LockBits(new Rectangle(0, 0, depthW, depthH),
                                             ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                Marshal.Copy(outPix, 0, bd.Scan0, outPix.Length);
                src.UnlockBits(bd);
                int w = Math.Min(OutWidth, depthW * 2);
                int h = (int)Math.Round(w * (double)depthH / depthW);
                return Scale(src, w, h);
            }
        }

        static byte[] Scale(Bitmap src, int w, int h)
        {
            using (Bitmap dst = new Bitmap(w, h, PixelFormat.Format24bppRgb))
            using (Graphics g = Graphics.FromImage(dst))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                g.DrawImage(src, 0, 0, w, h);
                using (MemoryStream ms = new MemoryStream())
                {
                    dst.Save(ms, jpegCodec, encoderParams);
                    return ms.ToArray();
                }
            }
        }

        static ImageCodecInfo GetCodec(string mime)
        {
            foreach (ImageCodecInfo c in ImageCodecInfo.GetImageEncoders())
                if (c.MimeType == mime) return c;
            return null;
        }

        // =================================================================
        //  骨骼 JSON
        //  坐标归一化到「画面」本身，这样网页叠加骨骼线时能和画面对齐
        // =================================================================
        static readonly JointType[] Wanted = {
            JointType.Head, JointType.Neck, JointType.SpineShoulder, JointType.SpineMid, JointType.SpineBase,
            JointType.ShoulderRight, JointType.ElbowRight, JointType.WristRight, JointType.HandRight,
            JointType.ShoulderLeft,  JointType.ElbowLeft,  JointType.WristLeft,  JointType.HandLeft,
            JointType.HipRight, JointType.KneeRight, JointType.AnkleRight,
            JointType.HipLeft,  JointType.KneeLeft,  JointType.AnkleLeft
        };

        static string BuildSkeletonJson()
        {
            Body best = null;
            float bestZ = float.MaxValue;
            foreach (Body b in bodies)
            {
                if (b == null || !b.IsTracked) continue;
                // 多人时取离摄像头最近的那个作为玩家
                float z = b.Joints[JointType.SpineMid].Position.Z;
                if (z > 0 && z < bestZ) { bestZ = z; best = b; }
            }
            if (best == null) return "{\"body\":null}";

            StringBuilder sb = new StringBuilder(900);
            sb.Append("{\"body\":{\"id\":\"").Append(best.TrackingId).Append("\",\"z\":")
              .Append(bestZ.ToString("0.00", CultureInfo.InvariantCulture)).Append(",\"j\":{");

            bool first = true;
            foreach (JointType jt in Wanted)
            {
                Joint j = best.Joints[jt];
                if (j.TrackingState == TrackingState.NotTracked) continue;

                float nx, ny;
                if (Cutout)
                {
                    DepthSpacePoint p = mapper.MapCameraPointToDepthSpace(j.Position);
                    if (float.IsInfinity(p.X) || float.IsInfinity(p.Y)) continue;
                    nx = p.X / depthW; ny = p.Y / depthH;
                }
                else
                {
                    ColorSpacePoint p = mapper.MapCameraPointToColorSpace(j.Position);
                    if (float.IsInfinity(p.X) || float.IsInfinity(p.Y)) continue;
                    nx = p.X / colorW; ny = p.Y / colorH;
                }
                if (nx < -0.5f || nx > 1.5f || ny < -0.5f || ny > 1.5f) continue;

                if (!first) sb.Append(',');
                first = false;
                sb.Append('"').Append(jt.ToString()).Append("\":{\"x\":")
                  .Append(nx.ToString("0.0000", CultureInfo.InvariantCulture)).Append(",\"y\":")
                  .Append(ny.ToString("0.0000", CultureInfo.InvariantCulture)).Append('}');
            }
            sb.Append("}}}");
            return sb.ToString();
        }

        // =================================================================
        //  HTTP / WebSocket
        // =================================================================
        static void StartHttp()
        {
            HttpListener http = new HttpListener();
            http.Prefixes.Add("http://+:" + Port + "/");
            try { http.Start(); }
            catch (HttpListenerException)
            {
                // 没有管理员权限时退回只监听本机
                http = new HttpListener();
                http.Prefixes.Add("http://127.0.0.1:" + Port + "/");
                http.Prefixes.Add("http://localhost:" + Port + "/");
                http.Start();
                Log("提示：未以管理员运行，仅监听本机地址（同机使用不受影响）。");
            }

            Task.Run(async () =>
            {
                while (true)
                {
                    HttpListenerContext ctx;
                    try { ctx = await http.GetContextAsync(); }
                    catch { break; }
                    var _ = Task.Run(() => Handle(ctx));
                }
            });
        }

        static async void Handle(HttpListenerContext ctx)
        {
            string path = ctx.Request.Url.AbsolutePath.TrimEnd('/').ToLowerInvariant();
            ctx.Response.AddHeader("Access-Control-Allow-Origin", "*");

            try
            {
                if (ctx.Request.IsWebSocketRequest && (path == "/ws" || path == ""))
                {
                    HttpListenerWebSocketContext wsc = await ctx.AcceptWebSocketAsync(null);
                    await PushSkeleton(wsc.WebSocket);
                    return;
                }
                if (path == "/video") { WriteMjpeg(ctx); return; }
                if (path == "/snapshot") { WriteSnapshot(ctx); return; }
                WriteText(ctx, "KinectBridge 运行中。\n骨骼: ws://127.0.0.1:" + Port + "/ws\n画面: http://127.0.0.1:" + Port + "/video\n");
            }
            catch { try { ctx.Response.Abort(); } catch { } }
        }

        static void WriteText(HttpListenerContext ctx, string s)
        {
            byte[] b = Encoding.UTF8.GetBytes(s);
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            ctx.Response.ContentLength64 = b.Length;
            ctx.Response.OutputStream.Write(b, 0, b.Length);
            ctx.Response.Close();
        }

        static void WriteSnapshot(HttpListenerContext ctx)
        {
            byte[] jpg;
            lock (jpegLock) { jpg = latestJpeg; }
            if (jpg == null) { ctx.Response.StatusCode = 503; ctx.Response.Close(); return; }
            ctx.Response.ContentType = "image/jpeg";
            ctx.Response.ContentLength64 = jpg.Length;
            ctx.Response.OutputStream.Write(jpg, 0, jpg.Length);
            ctx.Response.Close();
        }

        // MJPEG：一个长连接不停推 JPEG，浏览器 <img> 直接就能显示
        static void WriteMjpeg(HttpListenerContext ctx)
        {
            const string boundary = "kinectframe";
            ctx.Response.ContentType = "multipart/x-mixed-replace; boundary=" + boundary;
            ctx.Response.SendChunked = true;
            Stream os = ctx.Response.OutputStream;
            long seen = -1;

            try
            {
                while (true)
                {
                    byte[] jpg = null;
                    lock (jpegLock)
                    {
                        if (jpegSeq != seen) { jpg = latestJpeg; seen = jpegSeq; }
                    }
                    if (jpg == null) { Thread.Sleep(15); continue; }

                    byte[] head = Encoding.ASCII.GetBytes(
                        "\r\n--" + boundary + "\r\nContent-Type: image/jpeg\r\nContent-Length: " + jpg.Length + "\r\n\r\n");
                    os.Write(head, 0, head.Length);
                    os.Write(jpg, 0, jpg.Length);
                    os.Flush();
                    Thread.Sleep(1000 / VideoFps / 2);
                }
            }
            catch { /* 浏览器断开，正常 */ }
            finally { try { ctx.Response.Close(); } catch { } }
        }

        static async Task PushSkeleton(WebSocket ws)
        {
            string last = null;
            try
            {
                while (ws.State == WebSocketState.Open)
                {
                    string json;
                    lock (skelLock) { json = latestSkeleton; }
                    if (json != last)
                    {
                        last = json;
                        byte[] b = Encoding.UTF8.GetBytes(json);
                        await ws.SendAsync(new ArraySegment<byte>(b), WebSocketMessageType.Text, true, CancellationToken.None);
                    }
                    await Task.Delay(33);
                }
            }
            catch { /* 浏览器断开，正常 */ }

            // 注意：C# 5 不允许在 finally 里 await，收尾写在外面
            try
            {
                if (ws.State == WebSocketState.Open)
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
            }
            catch { }

            try { ws.Dispose(); } catch { }
        }
    }
}
