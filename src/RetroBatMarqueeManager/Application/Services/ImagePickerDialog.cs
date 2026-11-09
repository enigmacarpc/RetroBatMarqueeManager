using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace RetroBatMarqueeManager.Application.Services
{
    /// <summary>
    /// EN: One small window with "Logo", "Background" and "Cancel" buttons; Logo / Background then open an Explorer file picker.
    /// The previously focused window (the frontend) gets the focus back when the window closes, so game selection keeps working.
    /// FR: Une petite fenêtre "Logo", "Background", "Cancel" ; Logo / Background ouvrent ensuite l'explorateur de fichiers.
    /// </summary>
    public static class ImagePickerDialog
    {
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);

        public static (string? File, bool IsBackground) Pick(string gameName, string initialDir, IEnumerable<string> formats)
        {
            string? chosen = null;
            bool isBackground = false;
            IntPtr previous = GetForegroundWindow();
            var patterns = formats.Select(f => "*." + f.Trim().TrimStart('.')).ToList();

            var thread = new Thread(() =>
            {
                try
                {
                    using var form = new Form
                    {
                        Text = "Add image",
                        FormBorderStyle = FormBorderStyle.FixedDialog,
                        StartPosition = FormStartPosition.CenterScreen,
                        TopMost = true,
                        ShowInTaskbar = false,
                        MinimizeBox = false,
                        MaximizeBox = false,
                        ClientSize = new Size(380, 120),
                        BackColor = Color.FromArgb(40, 40, 40),
                        ForeColor = Color.White,
                        KeyPreview = true
                    };

                    var label = new Label
                    {
                        Text = $"Add an image for '{gameName}'",
                        AutoSize = false,
                        TextAlign = ContentAlignment.MiddleCenter,
                        Dock = DockStyle.Top,
                        Height = 50
                    };

                    Button Make(string text, int x) => new Button
                    {
                        Text = text,
                        Location = new Point(x, 66),
                        Size = new Size(110, 34),
                        FlatStyle = FlatStyle.System,
                        ForeColor = Color.Black
                    };

                    var logoBtn = Make("Logo", 12);
                    var bgBtn = Make("Background", 135);
                    var cancelBtn = Make("Cancel", 258);
                    form.CancelButton = cancelBtn;

                    void Browse(bool background)
                    {
                        using var dlg = new OpenFileDialog
                        {
                            Title = background ? $"Choose the BACKGROUND for {gameName}" : $"Choose the LOGO for {gameName}",
                            InitialDirectory = initialDir,
                            Filter = $"Images ({string.Join(", ", patterns)})|{string.Join(";", patterns)}|All files (*.*)|*.*",
                            CheckFileExists = true,
                            RestoreDirectory = true
                        };
                        if (dlg.ShowDialog(form) == DialogResult.OK)
                        {
                            chosen = dlg.FileName;
                            isBackground = background;
                            form.Close();
                        }
                    }

                    logoBtn.Click += (_, _) => Browse(false);
                    bgBtn.Click += (_, _) => Browse(true);
                    cancelBtn.Click += (_, _) => form.Close();

                    form.Controls.Add(label);
                    form.Controls.Add(logoBtn);
                    form.Controls.Add(bgBtn);
                    form.Controls.Add(cancelBtn);
                    form.Shown += (_, _) => { form.Activate(); SetForegroundWindow(form.Handle); };
                    form.ShowDialog();
                }
                catch { /* picker is best effort */ }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            // EN: give the focus back to the frontend, otherwise it stays unresponsive.
            if (previous != IntPtr.Zero) SetForegroundWindow(previous);
            return (chosen, isBackground);
        }
    }
}
