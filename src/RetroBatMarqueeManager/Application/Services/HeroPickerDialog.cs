using System;
using System.Collections.Generic;
using System.Drawing;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RetroBatMarqueeManager.Application.Services
{
    /// <summary>
    /// EN: A window with thumbnails of the backgrounds found on SteamGridDB; click one to use it. The previously focused
    /// window (the frontend) gets the focus back when the window closes, so game selection keeps working.
    /// </summary>
    public static class HeroPickerDialog
    {
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);

        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        /// <summary>Returns the index of the chosen item, or -1 when cancelled.</summary>
        public static int Pick(string gameName, IReadOnlyList<(string ThumbUrl, string Label, bool Animated)> items)
        {
            int chosen = -1;
            IntPtr previous = GetForegroundWindow();

            var thread = new Thread(() =>
            {
                try
                {
                    using var form = new Form
                    {
                        Text = $"Choose a background for {gameName}",
                        StartPosition = FormStartPosition.CenterScreen,
                        TopMost = true,
                        ShowInTaskbar = false,
                        ClientSize = new Size(1100, 700),
                        BackColor = Color.FromArgb(30, 30, 30),
                        ForeColor = Color.White,
                        KeyPreview = true
                    };

                    int animatedCount = 0;
                    foreach (var it in items) if (it.Animated) animatedCount++;
                    var header = new Label
                    {
                        Text = $"SteamGridDB backgrounds ({animatedCount} animated, {items.Count - animatedCount} still) - click one to use it, Esc to cancel",
                        Dock = DockStyle.Fill,
                        TextAlign = ContentAlignment.MiddleCenter
                    };
                    var animOnly = new CheckBox { Text = "Animated only", Dock = DockStyle.Right, Width = 130, ForeColor = Color.White, Enabled = animatedCount > 0 };
                    var top = new Panel { Dock = DockStyle.Top, Height = 36 };
                    top.Controls.Add(header);
                    top.Controls.Add(animOnly);
                    var cancel = new Button { Text = "Cancel", Dock = DockStyle.Bottom, Height = 36, FlatStyle = FlatStyle.System, ForeColor = Color.Black };
                    var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = true, Padding = new Padding(8) };

                    cancel.Click += (_, _) => form.Close();
                    form.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) form.Close(); };

                    var boxes = new List<PictureBox>();
                    for (int i = 0; i < items.Count; i++)
                    {
                        int index = i;
                        var box = new PictureBox
                        {
                            Width = 340,
                            Height = 190,
                            Margin = new Padding(6),
                            SizeMode = PictureBoxSizeMode.Zoom,
                            BackColor = Color.FromArgb(55, 55, 55),
                            Cursor = Cursors.Hand
                        };
                        var tip = new ToolTip();
                        tip.SetToolTip(box, items[i].Label);
                        box.Click += (_, _) => { chosen = index; form.Close(); };
                        box.MouseEnter += (_, _) => box.BackColor = Color.FromArgb(0, 120, 215);
                        box.MouseLeave += (_, _) => box.BackColor = Color.FromArgb(55, 55, 55);
                        if (items[i].Animated)
                        {
                            // EN: orange "ANIMATED" badge; when the preview cannot be shown (webm) the badge explains it.
                            box.Paint += (_, e) =>
                            {
                                using var f = new Font("Segoe UI", 9f, FontStyle.Bold);
                                if (box.Image == null) TextRenderer.DrawText(e.Graphics, "Animated (no preview)", f, box.ClientRectangle, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                                var r = new Rectangle(4, 4, 84, 20);
                                using var br = new SolidBrush(Color.FromArgb(230, 230, 120, 0));
                                e.Graphics.FillRectangle(br, r);
                                TextRenderer.DrawText(e.Graphics, "ANIMATED", f, r, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                            };
                        }
                        boxes.Add(box);
                        panel.Controls.Add(box);
                    }

                    animOnly.CheckedChanged += (_, _) =>
                    {
                        for (int i = 0; i < boxes.Count; i++) boxes[i].Visible = !animOnly.Checked || items[i].Animated;
                    };
                    form.Controls.Add(panel);
                    form.Controls.Add(top);
                    form.Controls.Add(cancel);

                    form.Shown += (_, _) =>
                    {
                        form.Activate();
                        WindowFocus.KeepOnTop(form);
                        // EN: thumbnails load in the background so the window opens immediately.
                        for (int i = 0; i < items.Count; i++)
                        {
                            int index = i;
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    var bytes = await Http.GetByteArrayAsync(items[index].ThumbUrl);
                                    using var ms = new System.IO.MemoryStream(bytes);
                                    var img = Image.FromStream(ms);
                                    var copy = new Bitmap(img);
                                    img.Dispose();
                                    if (!form.IsDisposed)
                                        form.BeginInvoke(new Action(() => { if (!boxes[index].IsDisposed) boxes[index].Image = copy; }));
                                }
                                catch { /* leave the grey box */ }
                            });
                        }
                    };
                    form.ShowDialog();
                }
                catch { /* picker is best effort */ }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (previous != IntPtr.Zero) SetForegroundWindow(previous);
            return chosen;
        }
    }
}
