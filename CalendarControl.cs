using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace TraeSign
{
    public struct CalendarCell
    {
        public DateTime Date;
        public bool InMonth;
        public CalendarCell(DateTime date, bool inMonth) { Date = date; InMonth = inMonth; }
    }

    /// <summary>自绘月历：签到成功日绿色圆角块，今日红色描边，支持翻月（只读展示）</summary>
    public class CalendarControl : UserControl
    {
        private const int NavH = 24;
        private const int HeaderH = 24;
        private static readonly Color GreenDay = Color.FromArgb(76, 175, 80);
        private static readonly Color TodayBorder = Color.FromArgb(229, 57, 53);
        private static readonly Color InMonthText = Color.FromArgb(55, 55, 55);
        private static readonly Color OutMonthText = Color.FromArgb(190, 190, 190);
        private static readonly Color HeaderText = Color.FromArgb(120, 120, 120);

        private DateTime _month;
        private Dictionary<string, bool> _successByDate;

        public CalendarControl()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = Color.White;
            _month = new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1);
            _successByDate = new Dictionary<string, bool>();
            MouseClick += OnMouseClick;
        }

        public event EventHandler MonthChanged;

        public void SetMonth(DateTime m) { _month = new DateTime(m.Year, m.Month, 1); Invalidate(); }
        public DateTime CurrentMonth { get { return _month; } }
        public void SetData(Dictionary<string, bool> data) { _successByDate = data ?? new Dictionary<string, bool>(); Invalidate(); }

        /// <summary>纯逻辑：生成 42 格（6x7），周一为首列，跨月显示上月尾/下月头</summary>
        public static List<CalendarCell> BuildCells(DateTime month)
        {
            var first = new DateTime(month.Year, month.Month, 1);
            int offset = ((int)first.DayOfWeek + 6) % 7;
            var start = first.AddDays(-offset);
            var list = new List<CalendarCell>(42);
            for (int i = 0; i < 42; i++)
            {
                var d = start.AddDays(i);
                list.Add(new CalendarCell(d, d.Year == month.Year && d.Month == month.Month));
            }
            return list;
        }

        private static void FillRoundedRect(Graphics g, RectangleF r, float radius, Color color)
        {
            using (var path = BuildRoundedPath(r, radius))
            using (var b = new SolidBrush(color))
                g.FillPath(b, path);
        }

        private static void DrawRoundedOutline(Graphics g, RectangleF r, float radius, Color color, float width)
        {
            using (var path = BuildRoundedPath(r, radius))
            using (var p = new Pen(color, width))
                g.DrawPath(p, path);
        }

        private static GraphicsPath BuildRoundedPath(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = radius * 2f;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);

            int w = ClientSize.Width, h = ClientSize.Height;
            if (w < 140 || h < 100) return;

            // 顶部：左/右翻月三角 + 年月文字
            using (var b = new SolidBrush(InMonthText))
            {
                // 左侧按钮：尖朝左（上一月）
                var left = new Point[] { new Point(6, NavH / 2), new Point(22, 2), new Point(22, NavH - 2) };
                g.FillPolygon(b, left);
                // 右侧按钮：尖朝右（下一月）
                var right = new Point[] { new Point(w - 6, NavH / 2), new Point(w - 22, 2), new Point(w - 22, NavH - 2) };
                g.FillPolygon(b, right);
            }
            using (var f = new Font(this.Font.FontFamily, 11f, FontStyle.Bold))
                TextRenderer.DrawText(g, _month.ToString("yyyy年M月"), f, new Rectangle(30, 0, w - 60, NavH), InMonthText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            // 星期表头
            string[] week = { "一", "二", "三", "四", "五", "六", "日" };
            int cellW = w / 7;
            for (int i = 0; i < 7; i++)
                TextRenderer.DrawText(g, week[i], this.Font, new Rectangle(i * cellW, NavH, cellW, HeaderH), HeaderText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            int top = NavH + HeaderH;
            int cellH = (h - NavH - HeaderH) / 6;
            string todayKey = DateTime.Now.ToString("yyyy-MM-dd");
            var cells = BuildCells(_month);

            for (int i = 0; i < cells.Count; i++)
            {
                var cell = cells[i];
                int row = i / 7, col = i % 7;
                var rc = new Rectangle(col * cellW, top + row * cellH, cellW, cellH);
                var inner = new RectangleF(rc.X + 3f, rc.Y + 3f, rc.Width - 6f, rc.Height - 6f);
                string key = cell.Date.ToString("yyyy-MM-dd");
                bool success = _successByDate.ContainsKey(key) && _successByDate[key];

                if (success)
                {
                    FillRoundedRect(g, inner, 10f, GreenDay);
                    using (var f = new Font(this.Font.FontFamily, 10f, FontStyle.Bold))
                        TextRenderer.DrawText(g, cell.Date.Day.ToString(), f, rc, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
                else
                {
                    Color tc = cell.InMonth ? InMonthText : OutMonthText;
                    bool isToday = (key == todayKey);
                    using (var f = new Font(this.Font.FontFamily, 10f, isToday ? FontStyle.Bold : FontStyle.Regular))
                        TextRenderer.DrawText(g, cell.Date.Day.ToString(), f, rc, tc, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }

                if (key == todayKey)
                    DrawRoundedOutline(g, RectangleF.Inflate(inner, -1f, -1f), 10f, TodayBorder, 2f);
            }
        }

        private void OnMouseClick(object sender, MouseEventArgs e)
        {
            if (e.Y < 0 || e.Y > NavH) return;
            int w = ClientSize.Width;
            if (e.X < 28)
            {
                SetMonth(_month.AddMonths(-1));
                RaiseMonthChanged();
            }
            else if (e.X > w - 28)
            {
                SetMonth(_month.AddMonths(1));
                RaiseMonthChanged();
            }
        }

        private void RaiseMonthChanged()
        {
            if (MonthChanged != null) MonthChanged(this, EventArgs.Empty);
        }
    }
}
