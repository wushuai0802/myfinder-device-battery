using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using AtkBattery;

namespace AtkBatteryTray {
    /// <summary>
    /// macOS Control Center 风格的视觉设计器与原生 GDI+ 绘制引擎。
    /// 严格遵循 C# 5 语法，兼容 Windows 内置 csc.exe 编译器。
    /// 负责托盘单色小环形图标、面板几何布局（尺寸/圆角/点击热区）以及高品质圆角抗锯齿绘制。
    /// </summary>
    public static class VisualDesign {
        #region 布局常量与公共几何热区

        // 鼠标热区始终固定；只有确认无线耳机连接后才展开第二组卡片。
        private static readonly Size _panelSize = new Size(280, 210);
        private const int HeadsetSectionHeight = 130;
        private const int DeviceCardGap = 8;
        public static Size PanelSize {
            get { return _panelSize; }
        }
        public static Size PanelSizeFor(HeadsetReading headset) {
            return PanelSizeFor(headset, null);
        }
        public static Size PanelSizeFor(HeadsetReading headset, DpiConfig dpi) {
            return PanelSizeFor(ShouldShowHeadset(headset), dpi);
        }
        public static bool ShouldShowHeadset(HeadsetReading headset) {
            return headset != null && headset.Connected &&
                (headset.ConnectionVerified ||
                (headset.ReceiverPresent && headset.ConnectionTimestampUtc != DateTime.MinValue));
        }
        public static Size PanelSizeFor(bool showHeadset, DpiConfig dpi = null) {
            return new Size(_panelSize.Width, MousePanelHeight(dpi) + (showHeadset ? HeadsetSectionHeight + DeviceCardGap : 0));
        }
        private static int MousePanelHeight(DpiConfig dpi) {
            return dpi != null && dpi.Stages != null && dpi.Stages.Count > 4 ? 230 : _panelSize.Height;
        }

        public static RectangleF SpeakerOutputRect { get { return SpeakerOutputRectFor(null); } }
        public static RectangleF HeadsetOutputRect { get { return HeadsetOutputRectFor(null); } }
        public static RectangleF MicrophoneMuteRect { get { return MicrophoneMuteRectFor(null); } }
        public static RectangleF SpeakerOutputRectFor(DpiConfig dpi) {
            return new RectangleF(14f, MousePanelHeight(dpi) + DeviceCardGap + 94f, 92f, 26f);
        }
        public static RectangleF HeadsetOutputRectFor(DpiConfig dpi) {
            return new RectangleF(112f, MousePanelHeight(dpi) + DeviceCardGap + 94f, 92f, 26f);
        }
        public static RectangleF MicrophoneMuteRectFor(DpiConfig dpi) {
            return new RectangleF(210f, MousePanelHeight(dpi) + DeviceCardGap + 94f, 56f, 26f);
        }

        // macOS 风格外窗体圆角半径
        public static float PanelRadius {
            get { return 16f; }
        }

        // 标题栏右上角圆形刷新按钮热区
        private static readonly RectangleF _refreshRect = new RectangleF(240f, 16f, 26f, 26f);
        public static RectangleF RefreshRect {
            get { return _refreshRect; }
        }

        /// <summary>
        /// 获取指定索引与总档位数的 DPI 胶囊按钮热区。
        /// 1~4 档单行排列；5~8 档分为双行网格，严格对齐并支持点击映射。
        /// </summary>
        public static RectangleF StageRect(int index, int count) {
            if (count < 1) count = 1;
            if (count <= 4) {
                // 单行排列 (Y=156, H=32)
                int gap = 6;
                float totalW = 252f;
                float w = (totalW - (count - 1) * gap) / count;
                int x = 14 + (int)Math.Round(index * (w + gap));
                int pillW = (int)Math.Round(w);
                return new RectangleF(x, 156f, pillW, 32f);
            } else {
                // 双行网格 (Row 0: Y=154, Row 1: Y=184, H=25, 4 列)
                int col = index % 4;
                int row = index / 4;
                int x = 14 + col * 64; // 58px 宽 + 6px 间距
                int y = 154 + row * 29;
                return new RectangleF(x, y, 58f, 25f);
            }
        }

        #endregion

        #region 菜单栏单色小环形电量图标

        /// <summary>
        /// 生成菜单栏单色小环形电量图标。
        /// 尺寸 32x32（在高分屏与标准 16/24px 托盘下均保持纯净锐利）。
        /// 纯图形无内部挤塞数字；充电时居中呈现高清晰闪电图标；未知时居中圆点；缓存态降低不透明度。
        /// </summary>
        public static Bitmap Tray(int? percent, bool charge, bool dark, bool stale) {
            return TrayGlyph.Render(percent,charge,dark,stale,32);
        }
        public static Bitmap Tray(int? percent,bool charge,bool dark,bool stale,int size) {
            return TrayGlyph.Render(percent,charge,dark,stale,size);
        }

        #endregion

        #region 主面板原生高品质绘制

        /// <summary>
        /// 绘制 macOS Control Center 风格完整浮层面板。
        /// </summary>
        public static void PaintPanel(
            Graphics g,
            BatteryReading current,
            BatteryReading lastGood,
            string error,
            bool busy,
            DpiConfig dpi,
            string dpiError,
            bool dpiBusy,
            bool dark,
            float openProgress = 1f,
            int hoverStage = -1,
            bool hoverRefresh = false)
        {
            PaintDevicesPanel(g, current, lastGood, error, busy, dpi, dpiError, dpiBusy,
                dark, null, null, openProgress, hoverStage, hoverRefresh, 0f);
        }

        public static void PaintDevicesPanel(
            Graphics g,
            BatteryReading current,
            BatteryReading lastGood,
            string error,
            bool busy,
            DpiConfig dpi,
            string dpiError,
            bool dpiBusy,
            bool dark,
            HeadsetReading headset,
            AudioReading audio,
            float openProgress = 1f,
            int hoverStage = -1,
            bool hoverRefresh = false,
            float headsetProgress = 1f,
            int hoverAudio = -1)
        {
            if (g == null) return;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            bool showHeadset = ShouldShowHeadset(headset);
            float deviceProgress = showHeadset ? ClampProgress(headsetProgress) : 0f;
            int mouseHeight = MousePanelHeight(dpi);
            int headsetTop = mouseHeight + DeviceCardGap;
            float panelHeight = mouseHeight + (HeadsetSectionHeight + DeviceCardGap) * deviceProgress;

            // 动效微位移（当展开时从上微滑 3px 到位）
            float slideY = (1f - Math.Max(0f, Math.Min(1f, openProgress))) * 3.5f;
            var state = g.Save();
            if (slideY > 0.05f) {
                g.TranslateTransform(0f, slideY);
            }

            // 配色令牌定义（纯正 macOS 系统材质风格）
            Color cardBg = dark ? Color.FromArgb(42, 43, 48) : Color.FromArgb(255, 255, 255);
            Color cardBorder = dark ? Color.FromArgb(22, 255, 255, 255) : Color.FromArgb(232, 234, 238);
            Color fg = dark ? Color.FromArgb(246, 246, 248) : Color.FromArgb(28, 28, 30);
            Color fgSub = dark ? Color.FromArgb(200, 201, 207) : Color.FromArgb(60, 60, 67);
            Color muted = dark ? Color.FromArgb(142, 142, 147) : Color.FromArgb(136, 136, 142);
            Color accentBlue = dark ? Color.FromArgb(10, 132, 255) : Color.FromArgb(0, 122, 255);
            Color accentGreen = dark ? Color.FromArgb(48, 209, 88) : Color.FromArgb(52, 199, 89);
            Color accentAmber = dark ? Color.FromArgb(255, 159, 10) : Color.FromArgb(245, 140, 0);
            Color pillBg = dark ? Color.FromArgb(50, 51, 57) : Color.FromArgb(255, 255, 255);
            Color pillHover = dark ? Color.FromArgb(62, 63, 70) : Color.FromArgb(242, 243, 246);
            Color pillBorder = dark ? Color.FromArgb(26, 255, 255, 255) : Color.FromArgb(223, 225, 230);

            // 每台设备有自己的外卡；两张卡片之间保留透明桌面间距。
            DrawOuterCard(g, _panelSize.Width, mouseHeight, dark);

            // 2. 顶部设备标题栏 (Y = 14 ~ 48)
            DrawDeviceIcon(g, 16f, 15f, fg);
            DrawText(g, "ATK A9 Air+", 13.5f, FontStyle.Bold, fg, new RectangleF(45f, 14f, 185f, 19f), StringAlignment.Near);
            DrawText(g, "2.4 GHz 无线连接", 10f, FontStyle.Regular, muted, new RectangleF(45f, 33f, 185f, 15f), StringAlignment.Near);

            // 刷新按钮 (RefreshRect: 240, 16, 26, 26)
            bool showBusy=busy&&current==null&&lastGood==null;
            DrawRefreshButton(g, _refreshRect, hoverRefresh, showBusy, dark, fg, muted, accentBlue);

            // 3. 电量核心卡片 (Y = 56, H = 66, Radius = 10)
            RectangleF cardRect = new RectangleF(14f, 56f, 252f, 66f);
            using (var cardPath = RoundRect(cardRect.X, cardRect.Y, cardRect.Width, cardRect.Height, 10f)) {
                using (var b = new LinearGradientBrush(cardRect,cardBg,dark?Color.FromArgb(38,40,46):Color.FromArgb(248,250,253),LinearGradientMode.Vertical)) g.FillPath(b, cardPath);
                using (var p = new Pen(cardBorder, 1f)) g.DrawPath(p, cardPath);
            }

            // 确定电量显示源与状态
            BatteryReading shown = current ?? lastGood;
            bool isStale = (current == null && lastGood != null);
            int? pct = (shown != null) ? (int?)shown.Percent : null;
            bool isCharging = (current != null && current.Charging);

            // 环形电量进度表盘 (X = 26, Y = 68, 42x42)
            DrawHeroBatteryRing(g, new RectangleF(26f, 68f, 42f, 42f), pct, isCharging, isStale, dark, fg, accentBlue, accentGreen, accentAmber);

            // 电量大字 (22px Bold)
            string pctStr = pct.HasValue ? (pct.Value + "%") : "--%";
            Color pctColor = isStale ? Color.FromArgb(170, fg) : fg;
            DrawText(g, pctStr, 22f, FontStyle.Bold, pctColor, new RectangleF(78f, 66f, 96f, 28f), StringAlignment.Near);

            // 电量辅助说明文字（严格遵守：读取超时不称休眠/故障，缓存值必须标明上次确认）
            string batteryStatusText;
            Color statusColor = muted;
            if (showBusy) {
                batteryStatusText = "正在同步电量...";
                statusColor = accentBlue;
            } else if (current != null) {
                if (current.Charging) {
                    batteryStatusText = "正在充电";
                    statusColor = accentGreen;
                } else {
                    batteryStatusText = "使用电池";
                    statusColor = fgSub;
                }
            } else if (lastGood != null) {
                batteryStatusText = "上次确认 · " + lastGood.TimestampUtc.ToLocalTime().ToString("HH:mm");
                statusColor = accentAmber;
            } else {
                batteryStatusText = "等待设备回读";
            }
            DrawText(g, batteryStatusText, 10f, FontStyle.Regular, statusColor, new RectangleF(79f, 94f, 168f, 16f), StringAlignment.Near);

            // 右上角连接状态指示胶囊标签
            DrawConnectionPill(g, new RectangleF(186f, 68f, 70f, 19f), current != null, isStale, dark, accentGreen, accentAmber, muted);

            // 4. DPI 区域标题栏 (Y = 132, H = 18)
            DrawDpiHeading(g, fg);

            // DPI 状态副标题（支持长文字与动态报错呈现）
            bool dpiReady = (dpi != null && dpi.Stages != null && dpi.Stages.Count > 0 && dpi.ActiveStage >= 0 && dpi.ActiveStage < dpi.Stages.Count);
            string dpiStateText;
            Color dpiStateColor = muted;
            if (dpiBusy) {
                dpiStateText = "切换中...";
                dpiStateColor = accentBlue;
            } else if (!dpiReady) {
                dpiStateText = "等待设备回读";
            } else if (!string.IsNullOrEmpty(dpiError)) {
                dpiStateText = "上次确认 · 等待回读";
                dpiStateColor = accentAmber;
            } else if (dpiReady) {
                DpiStage activeStage = dpi.Stages[dpi.ActiveStage];
                string valStr = (activeStage.XDpi == activeStage.YDpi) ? activeStage.XDpi.ToString() : (activeStage.XDpi + "/" + activeStage.YDpi);
                dpiStateText = "档位 " + (dpi.ActiveStage + 1) + " · " + valStr + " DPI";
            } else {
                dpiStateText = "等待鼠标在线";
            }
            DrawText(g, dpiStateText, 10f, FontStyle.Regular, dpiStateColor, new RectangleF(90f, 132f, 176f, 18f), StringAlignment.Far);

            // 5. 真实 DPI 胶囊控制组 (支持 1~8 档、长数值与 X/Y 异步档位)
            int stageCount = dpiReady ? dpi.Stages.Count : 4;
            for (int i = 0; i < stageCount; i++) {
                RectangleF pillRect = StageRect(i, stageCount);
                bool available = dpiReady && (i < dpi.Stages.Count);
                bool selected = available && (dpi.ActiveStage == i);
                bool hovered = (hoverStage == i) && available;

                string line1 = null;
                string line2 = null;
                if (available) {
                    DpiStage st = dpi.Stages[i];
                    if (st.XDpi == st.YDpi) {
                        line1 = st.XDpi.ToString();
                    } else {
                        line1 = "X " + st.XDpi;
                        line2 = "Y " + st.YDpi;
                    }
                } else {
                    line1 = "--";
                }

                DrawDpiPill(g, pillRect, line1, line2, selected, available, hovered, dark, fg, fgSub, muted, accentBlue, pillBg, pillHover, pillBorder);
            }

            if (showHeadset && deviceProgress > 0.001f) {
                // 第二组只在已露出的区域绘制，收起时不会盖住鼠标控件。
                var clipState = g.Save();
                {
                    g.SetClip(new RectangleF(0f, headsetTop, _panelSize.Width,
                        Math.Max(0f, panelHeight - headsetTop)), CombineMode.Intersect);
                    float layerScale;
                    using (var transform = g.Transform) {
                        float[] matrix = transform.Elements;
                        layerScale = Math.Max(1f, (float)Math.Sqrt(matrix[0] * matrix[0] + matrix[1] * matrix[1]));
                    }
                    using (var layer = new Bitmap((int)Math.Ceiling(_panelSize.Width * layerScale),
                        (int)Math.Ceiling(HeadsetSectionHeight * layerScale), PixelFormat.Format32bppArgb)) {
                        using (var layerGraphics = Graphics.FromImage(layer)) {
                            layerGraphics.SmoothingMode = SmoothingMode.HighQuality;
                            // 淡入层使用灰阶抗锯齿，避免透明文字带彩色边缘。
                            layerGraphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                            layerGraphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            layerGraphics.ScaleTransform(layerScale, layerScale);
                            DrawHeadsetSection(layerGraphics, headset, audio, dark, hoverAudio);
                        }
                        using (var attributes = new ImageAttributes()) {
                            var opacity = new ColorMatrix();
                            opacity.Matrix33 = deviceProgress;
                            attributes.SetColorMatrix(opacity, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
                            int slide = (int)Math.Round((1f - deviceProgress) * 7f);
                            g.DrawImage(layer, new Rectangle(0, headsetTop + slide, _panelSize.Width, HeadsetSectionHeight),
                                0, 0, layer.Width, layer.Height, GraphicsUnit.Pixel, attributes);
                        }
                    }
                }
                g.Restore(clipState);
            }

            g.Restore(state);
        }

        #endregion

        #region 内部绘制辅助方法

        private static float ClampProgress(float value) {
            if (float.IsNaN(value) || float.IsInfinity(value)) return 0f;
            return Math.Max(0f, Math.Min(1f, value));
        }

        private static void DrawOuterCard(Graphics g, float width, float height, bool dark) {
            Color border = dark ? Color.FromArgb(55, 255, 255, 255) : Color.FromArgb(220, 222, 227);
            Color top = dark ? Color.FromArgb(244, 43, 45, 51) : Color.FromArgb(244, 253, 254, 255);
            Color bottom = dark ? Color.FromArgb(248, 27, 29, 34) : Color.FromArgb(248, 239, 242, 247);
            using (var path = RoundRect(0.5f, 0.5f, width - 1f, height - 1f, PanelRadius)) {
                using (var brush = new LinearGradientBrush(new RectangleF(0, 0, width, height), top, bottom, LinearGradientMode.Vertical)) g.FillPath(brush, path);
                using (var pen = new Pen(border, 1f)) g.DrawPath(pen, path);
                using (var inner = RoundRect(1.5f, 1.5f, width - 3f, height - 3f, PanelRadius - 1f))
                using (var light = new LinearGradientBrush(new RectangleF(0, 0, width, height), Color.FromArgb(dark ? 60 : 185, Color.White), Color.FromArgb(0, Color.White), LinearGradientMode.Vertical))
                using (var pen = new Pen(light, 1f)) g.DrawPath(pen, inner);
            }
        }

        private static void DrawDpiHeading(Graphics g, Color color) {
            // Latin 与中文采用各自的原生字形，按同一条可见基线对齐。
            // 不再让整串文字落入雅黑的 Latin 字形，也不受 DrawString 边衬影响。
            float x = 14f;
            float baseline = 145.7f;
            x += DrawBaselineRun(g, "DPI", "Segoe UI Semibold", 11.5f, FontStyle.Regular, color, x, baseline);
            DrawBaselineRun(g, "档位", "Microsoft YaHei UI", 11.5f, FontStyle.Bold, color, x + 4f, baseline);
        }

        private static float DrawBaselineRun(Graphics g, string text, string family, float size,
            FontStyle style, Color color, float x, float baseline) {
            using (var font = new Font(family, size, style, GraphicsUnit.Pixel))
            using (var path = new GraphicsPath())
            using (var format = new StringFormat(StringFormat.GenericTypographic)) {
                format.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
                path.AddString(text, font.FontFamily, (int)font.Style, size, PointF.Empty, format);
                RectangleF ink = path.GetBounds();
                using (var transform = new Matrix()) {
                    transform.Translate(x - ink.Left, baseline - ink.Bottom);
                    path.Transform(transform);
                }
                using (var brush = new SolidBrush(color)) g.FillPath(brush, path);
                return ink.Width;
            }
        }

        private static void DrawHeadsetSection(Graphics g, HeadsetReading headset, AudioReading audio, bool dark, int hoverAudio) {
            Color fg = dark ? Color.FromArgb(246, 246, 248) : Color.FromArgb(28, 28, 30);
            Color fgSub = dark ? Color.FromArgb(200, 201, 207) : Color.FromArgb(60, 60, 67);
            Color muted = dark ? Color.FromArgb(142, 142, 147) : Color.FromArgb(136, 136, 142);
            Color blue = dark ? Color.FromArgb(10, 132, 255) : Color.FromArgb(0, 122, 255);
            Color green = dark ? Color.FromArgb(48, 209, 88) : Color.FromArgb(52, 199, 89);
            Color amber = dark ? Color.FromArgb(255, 159, 10) : Color.FromArgb(245, 140, 0);
            Color cardBg = dark ? Color.FromArgb(42, 43, 48) : Color.White;
            Color cardBorder = dark ? Color.FromArgb(22, 255, 255, 255) : Color.FromArgb(232, 234, 238);

            DrawOuterCard(g, _panelSize.Width, HeadsetSectionHeight, dark);

            var iconState = g.Save();
            g.TranslateTransform(16f, 7f);
            g.ScaleTransform(0.78f, 0.78f);
            DrawHeadsetIcon(g, 0f, 0f, fg);
            g.Restore(iconState);
            bool radioVerified = headset != null && headset.ConnectionVerified && headset.Connected;
            bool radioCached = !radioVerified && headset != null && headset.Connected;
            DrawText(g, radioVerified ? "HyperX Cloud Alpha" : "HyperX 音频控制", 13.5f, FontStyle.Bold, fg,
                new RectangleF(45f, 8f, 212f, 19f), StringAlignment.Near);

            RectangleF cardRect = new RectangleF(14f, 34f, 252f, 50f);
            using (var cardPath = RoundRect(cardRect.X, cardRect.Y, cardRect.Width, cardRect.Height, 10f)) {
                using (var brush = new LinearGradientBrush(cardRect, cardBg,
                    dark ? Color.FromArgb(38, 40, 46) : Color.FromArgb(248, 250, 253), LinearGradientMode.Vertical))
                    g.FillPath(brush, cardPath);
                using (var border = new Pen(cardBorder, 1f)) g.DrawPath(border, cardPath);
            }

            int? percent = headset != null && headset.Percent.HasValue && headset.Percent.Value >= 0 && headset.Percent.Value <= 100
                ? headset.Percent : null;
            DateTime confirmedAt = headset != null ? headset.BatteryTimestampUtc : DateTime.MinValue;
            bool hasTimestamp = confirmedAt != DateTime.MinValue;
            bool batteryFresh = hasTimestamp && (DateTime.UtcNow - confirmedAt).TotalSeconds <= 90;
            bool stale = percent.HasValue && (!radioVerified || !batteryFresh);
            bool chargeFresh = headset != null && headset.ChargingTimestampUtc != DateTime.MinValue
                && (DateTime.UtcNow - headset.ChargingTimestampUtc).TotalSeconds <= 90;
            bool charging = headset != null && radioVerified && chargeFresh && headset.Charging == true;
            DrawHeroBatteryRing(g, new RectangleF(29f, 41f, 36f, 36f), percent,
                charging, stale, dark, fg, blue, green, amber, true);
            DrawText(g, percent.HasValue ? percent.Value + "%" : "--", 21f, FontStyle.Bold,
                stale ? Color.FromArgb(170, fg) : fg, new RectangleF(78f, 37f, 96f, 25f), StringAlignment.Near);

            string status = "电量未读取";
            Color statusColor = muted;
            if (stale) {
                status = hasTimestamp ? "上次确认 · " + confirmedAt.ToLocalTime().ToString("HH:mm") : "电量已缓存";
                statusColor = radioCached ? muted : amber;
            } else if (charging) {
                status = percent.HasValue ? "正在充电" : "正在充电 · 电量未读取";
                statusColor = green;
            } else if (percent.HasValue) {
                status = headset.Charging.HasValue ? "使用电池" : "电量已同步";
                statusColor = fgSub;
            }
            DrawText(g, status, 10f, FontStyle.Regular, statusColor,
                new RectangleF(79f, 63f, 168f, 16f), StringAlignment.Near);
            DrawStatusPill(g, new RectangleF(186f, 40f, 70f, 19f),
                radioVerified ? "已连接" : radioCached ? "已缓存" : "未确认",
                radioVerified ? green : muted, dark);

            bool speakerAvailable = audio != null && audio.SpeakerAvailable;
            bool headsetAvailable = audio != null && audio.HeadsetAvailable;
            bool micAvailable = headsetAvailable && audio.MicrophoneMuted.HasValue;
            bool speakerSelected = audio != null && audio.ActiveOutput == "speaker";
            bool headsetSelected = audio != null && audio.ActiveOutput == "headset";
            bool micMuted = audio != null && audio.MicrophoneMuted == true;
            DrawAudioPill(g, new RectangleF(14f, 94f, 92f, 26f), "Realtek 扬声器", speakerSelected,
                speakerAvailable, hoverAudio == 0, dark, fg, muted, blue);
            DrawAudioPill(g, new RectangleF(112f, 94f, 92f, 26f), "HyperX 耳机", headsetSelected,
                headsetAvailable, hoverAudio == 1, dark, fg, muted, blue);
            DrawMicrophonePill(g, new RectangleF(210f, 94f, 56f, 26f), micMuted,
                micAvailable, hoverAudio == 2, dark, fg, muted, blue);
        }

        private static void DrawAudioPill(Graphics g, RectangleF rect, string text, bool selected,
            bool available, bool hover, bool dark, Color fg, Color muted, Color blue) {
            Color fill = selected ? blue : dark ? Color.FromArgb(50, 51, 57) : Color.White;
            if (!available) fill = dark ? Color.FromArgb(16, Color.White) : Color.FromArgb(10, Color.Black);
            else if (hover && !selected) fill = dark ? Color.FromArgb(62, 63, 70) : Color.FromArgb(239, 242, 247);
            Color border = selected ? Color.FromArgb(60, Color.White)
                : hover && available ? Color.FromArgb(dark ? 90 : 130, blue)
                : dark ? Color.FromArgb(26, Color.White) : Color.FromArgb(223, 225, 230);
            using (var path = RoundRect(rect.X, rect.Y, rect.Width, rect.Height, 7f)) {
                using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
                using (var pen = new Pen(border, 1f)) g.DrawPath(pen, path);
            }
            DrawText(g, text, 9.7f, selected ? FontStyle.Bold : FontStyle.Regular,
                !available ? muted : selected ? Color.White : fg, rect, StringAlignment.Center);
        }

        private static void DrawMicrophonePill(Graphics g, RectangleF rect, bool muted,
            bool available, bool hover, bool dark, Color fg, Color disabled, Color blue) {
            Color selected = dark ? Color.FromArgb(255, 105, 97) : Color.FromArgb(216, 60, 55);
            Color fill = muted && available ? dark ? Color.FromArgb(74, 39, 42) : Color.FromArgb(254, 231, 230)
                : dark ? Color.FromArgb(50, 51, 57) : Color.White;
            if (hover && available && !muted) fill = dark ? Color.FromArgb(62, 63, 70) : Color.FromArgb(239, 242, 247);
            Color color = !available ? disabled : muted ? selected : fg;
            using (var path = RoundRect(rect.X, rect.Y, rect.Width, rect.Height, 7f)) {
                using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
                using (var border = new Pen(muted && available ? Color.FromArgb(110, selected)
                    : dark ? Color.FromArgb(26, Color.White) : Color.FromArgb(223, 225, 230), 1f)) g.DrawPath(border, path);
            }
            float x = rect.X + 7f;
            float y = rect.Y + 6f;
            using (var pen = new Pen(color, 1.15f)) {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                using (var mic = RoundRect(x + 2f, y, 4.5f, 8f, 2f)) g.DrawPath(pen, mic);
                g.DrawArc(pen, x, y + 3.5f, 8.5f, 8f, 0f, 180f);
                g.DrawLine(pen, x + 4.25f, y + 11.5f, x + 4.25f, y + 14f);
                g.DrawLine(pen, x + 1.8f, y + 14f, x + 6.7f, y + 14f);
                if (muted) g.DrawLine(pen, x - 1f, y + 1f, x + 9f, y + 12f);
            }
            DrawText(g, muted ? "静音" : "麦克风", 9f, FontStyle.Regular, color,
                new RectangleF(rect.X + 19f, rect.Y, rect.Width - 21f, rect.Height), StringAlignment.Center);
        }

        private static void DrawHeadsetIcon(Graphics g, float x, float y, Color color) {
            using (var pen = new Pen(color, 1.6f)) {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                g.DrawArc(pen, new RectangleF(x + 1f, y, 20f, 25f), 180f, 180f);
                using (var left = RoundRect(x, y + 12f, 5.5f, 12f, 2.5f)) g.DrawPath(pen, left);
                using (var right = RoundRect(x + 16.5f, y + 12f, 5.5f, 12f, 2.5f)) g.DrawPath(pen, right);
                using (var boom = new GraphicsPath()) {
                    boom.AddBezier(x + 18f, y + 24f, x + 17f, y + 28f, x + 13f, y + 28f, x + 10f, y + 28f);
                    g.DrawPath(pen, boom);
                }
                g.DrawLine(pen, x + 9f, y + 28f, x + 11f, y + 28f);
            }
        }

        private static void DrawDeviceIcon(Graphics g, float x, float y, Color color) {
            // 绘制精细 macOS 风格鼠标轮廓
            using (var pen = new Pen(color, 1.6f)) {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                using (var body = RoundRect(x, y, 18f, 28f, 8.5f)) {
                    g.DrawPath(pen, body);
                }
                // 滚轮小药丸与中缝
                using (var wheel = RoundRect(x + 7.5f, y + 5.5f, 3f, 6.5f, 1.5f)) {
                    using (var fill = new SolidBrush(color)) g.FillPath(fill, wheel);
                }
                g.DrawLine(pen, x + 9f, y + 13.5f, x + 9f, y + 17f);
            }
        }

        private static void DrawRefreshButton(Graphics g, RectangleF rect, bool hover, bool busy, bool dark, Color fg, Color muted, Color accent) {
            Color fill=dark?Color.FromArgb(54,57,64):Color.FromArgb(237,240,245);
            if(hover)fill=dark?Color.FromArgb(67,72,82):Color.FromArgb(224,231,240);
            using(var shape=RoundRect(rect.X+.5f,rect.Y+.5f,rect.Width-1f,rect.Height-1f,8f)) {
                using(var brush=new SolidBrush(fill))g.FillPath(brush,shape);
                using(var border=new Pen(dark?Color.FromArgb(65,Color.White):Color.FromArgb(214,221,231),.8f))g.DrawPath(border,shape);
            }
            Color arrowColor = busy ? accent : fg;
            using (var pen = new Pen(arrowColor, 1.8f)) {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                // Standard clockwise refresh: open arc terminating at the
                // corner of a right-angle arrowhead, rather than a closed ring.
                using(var arrow=new GraphicsPath()) {
                    arrow.AddArc(new RectangleF(rect.X+6.5f,rect.Y+6.5f,13f,13f),20f,300f);
                    arrow.AddLine(rect.X+18f,rect.Y+8.8f,rect.X+20f,rect.Y+10f);
                    arrow.StartFigure();arrow.AddLines(new PointF[]{new PointF(rect.X+20f,rect.Y+5.5f),new PointF(rect.X+20f,rect.Y+10f),new PointF(rect.X+15.5f,rect.Y+10f)});
                    pen.LineJoin=LineJoin.Round;g.DrawPath(pen,arrow);
                }
            }
        }

        private static void DrawHeroBatteryRing(Graphics g, RectangleF rect, int? percent, bool charging, bool stale, bool dark, Color fg, Color accentBlue, Color accentGreen, Color accentAmber, bool neutralStale = false) {
            Color trackColor = dark ? Color.FromArgb(30, 255, 255, 255) : Color.FromArgb(20, 0, 0, 0);
            float strokeW = 3.6f;
            using (var trackPen = new Pen(trackColor, strokeW)) {
                g.DrawEllipse(trackPen, rect);
            }

            if (percent.HasValue && percent.Value > 0) {
                Color arcColor = TrayGlyph.LevelColor(percent,dark);

                if (stale) arcColor = neutralStale ? dark ? Color.FromArgb(142, 142, 147) : Color.FromArgb(136, 136, 142) : Color.FromArgb(140, arcColor);

                float sweep = Math.Max(5f, percent.Value * 3.6f);
                using (var pen = new Pen(arcColor, strokeW)) {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    g.DrawArc(pen, rect, -90f, sweep);
                }
            }

            // 表盘中心
            if (charging) {
                using (var b = new SolidBrush(accentGreen)) {
                    float cx = rect.X + rect.Width / 2f;
                    float cy = rect.Y + rect.Height / 2f;
                    PointF[] bolt = new PointF[] {
                        new PointF(cx + 1.0f, cy - 8.5f),
                        new PointF(cx - 4.5f, cy + 0.5f),
                        new PointF(cx - 0.2f, cy + 0.5f),
                        new PointF(cx - 1.5f, cy + 8.5f),
                        new PointF(cx + 4.5f, cy - 0.5f),
                        new PointF(cx + 0.5f, cy - 0.5f)
                    };
                    g.FillPolygon(b, bolt);
                }
            } else {
                // 未充电：中心清爽小标，电量低显示警示微点
                if (percent.HasValue && percent.Value <= 20) {
                    using (var b = new SolidBrush(stale && neutralStale
                        ? dark ? Color.FromArgb(142, 142, 147) : Color.FromArgb(136, 136, 142)
                        : TrayGlyph.LevelColor(percent,dark))) {
                        g.FillEllipse(b, rect.X + rect.Width / 2f - 2f, rect.Y + rect.Height / 2f - 2f, 4f, 4f);
                    }
                } else if (!percent.HasValue) {
                    using (var b = new SolidBrush(Color.FromArgb(dark ? 90 : 120, fg))) {
                        g.FillEllipse(b, rect.X + rect.Width / 2f - 2f, rect.Y + rect.Height / 2f - 2f, 4f, 4f);
                    }
                }
            }
        }

        private static void DrawConnectionPill(Graphics g, RectangleF rect, bool connected, bool isStale, bool dark, Color accentGreen, Color accentAmber, Color muted) {
            Color dotColor = connected ? accentGreen : (isStale ? accentAmber : muted);
            string text = connected ? "已连接" : (isStale ? "已缓存" : "未就绪");
            DrawStatusPill(g, rect, text, dotColor, dark);
        }

        private static void DrawStatusPill(Graphics g, RectangleF rect, string text, Color dotColor, bool dark) {
            Color fill = dark ? Color.FromArgb(20, 255, 255, 255) : Color.FromArgb(14, 0, 0, 0);

            using (var path = RoundRect(rect.X, rect.Y, rect.Width, rect.Height, rect.Height / 2f)) {
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
            }
            // 状态实心圆点
            using (var b = new SolidBrush(dotColor)) {
                g.FillEllipse(b, rect.X + 7f, rect.Y + (rect.Height - 5.5f) / 2f, 5.5f, 5.5f);
            }
            DrawText(g, text, 9.5f, FontStyle.Regular, Color.FromArgb(dark ? 210 : 90, dark ? Color.White : Color.Black),
                new RectangleF(rect.X + 16f, rect.Y, rect.Width - 18f, rect.Height), StringAlignment.Center);
        }

        private static void DrawDpiPill(Graphics g, RectangleF rf, string line1, string line2, bool selected, bool available, bool hovered,
            bool dark, Color fg, Color fgSub, Color muted, Color accentBlue, Color pillBg, Color pillHover, Color pillBorder)
        {
            float radius = (rf.Height <= 26f) ? 6.5f : 8.5f;

            using (var path = RoundRect(rf.X, rf.Y, rf.Width, rf.Height, radius)) {
                if (selected) {
                    // macOS 选中蓝胶囊，搭配纯白加粗文本
                    using (var b = new SolidBrush(accentBlue)) g.FillPath(b, path);
                    // 极细微高光勾边增加深度质感
                    using (var p = new Pen(Color.FromArgb(40, 255, 255, 255), 1f)) {
                        using (var inner = RoundRect(rf.X + 0.5f, rf.Y + 0.5f, rf.Width - 1f, rf.Height - 1f, radius - 0.5f)) {
                            g.DrawPath(p, inner);
                        }
                    }
                    if (!string.IsNullOrEmpty(line2)) {
                        float subSize = (rf.Height <= 26f) ? 7.8f : 8.8f;
                        float rowH = rf.Height / 2f;
                        RectangleF r1 = new RectangleF(rf.X, rf.Y + 1f, rf.Width, rowH - 0.5f);
                        RectangleF r2 = new RectangleF(rf.X, rf.Y + rowH - 0.5f, rf.Width, rowH - 0.5f);
                        DrawText(g, line1, subSize, FontStyle.Bold, Color.White, r1, StringAlignment.Center);
                        DrawText(g, line2, subSize, FontStyle.Bold, Color.White, r2, StringAlignment.Center);
                    } else {
                        float fontSize = (line1.Length >= 5) ? 9.5f : 11f;
                        DrawText(g, line1, fontSize, FontStyle.Bold, Color.White, rf, StringAlignment.Center);
                    }
                } else if (available) {
                    // 未选中常态或 Hover
                    Color fill = hovered ? pillHover : pillBg;
                    Color border = hovered ? Color.FromArgb(dark ? 90 : 130, accentBlue) : pillBorder;
                    using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                    using (var p = new Pen(border, 1f)) g.DrawPath(p, path);

                    Color textCol = hovered ? fg : fgSub;
                    if (!string.IsNullOrEmpty(line2)) {
                        float subSize = (rf.Height <= 26f) ? 7.8f : 8.8f;
                        float rowH = rf.Height / 2f;
                        RectangleF r1 = new RectangleF(rf.X, rf.Y + 1f, rf.Width, rowH - 0.5f);
                        RectangleF r2 = new RectangleF(rf.X, rf.Y + rowH - 0.5f, rf.Width, rowH - 0.5f);
                        DrawText(g, line1, subSize, FontStyle.Regular, textCol, r1, StringAlignment.Center);
                        DrawText(g, line2, subSize, FontStyle.Regular, textCol, r2, StringAlignment.Center);
                    } else {
                        float fontSize = (line1.Length >= 5) ? 9.5f : 10.5f;
                        DrawText(g, line1, fontSize, FontStyle.Regular, textCol, rf, StringAlignment.Center);
                    }
                } else {
                    // 禁用占位符态
                    Color fill = dark ? Color.FromArgb(16, 255, 255, 255) : Color.FromArgb(10, 0, 0, 0);
                    using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                    using (var p = new Pen(Color.FromArgb(15, muted), 1f)) g.DrawPath(p, path);
                    DrawText(g, "--", 10.5f, FontStyle.Regular, Color.FromArgb(90, muted), rf, StringAlignment.Center);
                }
            }
        }

        private static void DrawText(Graphics g, string s, float pixelSize, FontStyle style, Color color, RectangleF rect, StringAlignment align) {
            string family = "Segoe UI";
            for (int i = 0; i < s.Length; i++) {
                if (s[i] >= 0x2E80 && s[i] <= 0x9FFF) { family = "Microsoft YaHei UI"; break; }
            }
            using (var f = new Font(family, pixelSize, style, GraphicsUnit.Pixel))
            using (var b = new SolidBrush(color))
            using (var sf = new StringFormat {
                Alignment = align,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap
            }) {
                g.DrawString(s, f, b, rect, sf);
            }
        }

        public static GraphicsPath RoundRect(float x, float y, float width, float height, float radius) {
            var path = new GraphicsPath();
            float d = radius * 2f;
            if (d > width) d = width;
            if (d > height) d = height;
            path.AddArc(x, y, d, d, 180, 90);
            path.AddArc(x + width - d, y, d, d, 270, 90);
            path.AddArc(x + width - d, y + height - d, d, d, 0, 90);
            path.AddArc(x, y + height - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        #endregion
    }
}
