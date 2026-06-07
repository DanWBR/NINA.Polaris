using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection.Emit;
using System.Windows.Forms;

namespace demotwocs
{
    public partial class Form1 : Form
    {
        private Toupcam[] cam_ = new Toupcam[2];
        private Bitmap[] bmp_ = new Bitmap[2];
        private uint[] count_ = new uint[2];

        private void OnEventError(int idx)
        {
            cam_[idx].Close();
            cam_[idx] = null;
            MessageBox.Show("Generic error: " + (idx + 1).ToString());
        }

        private void OnEventDisconnected(int idx)
        {
            cam_[idx].Close();
            cam_[idx] = null;
            MessageBox.Show("Camera disconnect: " + (idx + 1).ToString());
        }

        private void OnEventImage(int idx)
        {
            if (bmp_[idx] != null)
            {
                Toupcam.FrameInfoV4 info = new Toupcam.FrameInfoV4();
                bool bOK = false;
                try
                {
                    BitmapData bmpdata = bmp_[idx].LockBits(new Rectangle(0, 0, bmp_[idx].Width, bmp_[idx].Height), ImageLockMode.WriteOnly, bmp_[idx].PixelFormat);
                    try
                    {
                        bOK = cam_[idx].PullImage(bmpdata.Scan0, 0, 24, bmpdata.Stride, out info); // check the return value
                    }
                    finally
                    {
                        bmp_[idx].UnlockBits(bmpdata);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.ToString());
                }
                if (bOK)
                {
                    if (0 == idx)
                        pictureBox1.Invalidate();
                    else
                        pictureBox2.Invalidate();
                }
            }
        }

        public Form1()
        {
            InitializeComponent();
            Form_SizeChanged(null, null);
        }

        private void Form_SizeChanged(object sender, EventArgs e)
        {
            pictureBox1.Width = pictureBox2.Width = ClientRectangle.Right - pictureBox1.Left - button1.Top;
            pictureBox1.Height = pictureBox2.Height = (ClientRectangle.Height - 3 * button1.Top) / 2;
            pictureBox2.Top = pictureBox1.Bottom + button1.Top;
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            checkBox1.Enabled = false;
        }

        private void OnStart(object sender, EventArgs e)
        {
            if (cam_[0] != null || cam_[1] != null)
                return;

            Toupcam.DeviceV2[] arr = Toupcam.EnumV2();
            if (arr.Length <= 0)
                MessageBox.Show("No camera found.");
            else
            {
                if (arr.Length > 0)
                    startDevice(arr[0].id, 0);
                if (arr.Length > 1)
                    startDevice(arr[1].id, 1);

                checkBox1.Enabled = true;
                checkBox1.Checked = true;
                timer1.Start();
            }
        }

        private void startDevice(string camId, int idx)
        {
            cam_[idx] = Toupcam.Open(camId);
            if (cam_[idx] != null)
            {
                int width = 0, height = 0;
                if (cam_[idx].get_Size(out width, out height))
                {
                    /* The backend of Winform is GDI, which is different from WPF/UWP/WinUI's backend Direct3D/Direct2D.
                     * We use their respective native formats, Bgr24 in Winform, and Bgr32 in WPF/UWP/WinUI
                     */
                    bmp_[idx] = new Bitmap(width, height, PixelFormat.Format24bppRgb);
                    if (!cam_[idx].StartPullModeWithCallback((Toupcam.eEVENT evt) =>
                        {
                            /* this is call by internal thread of toupcam.dll which is NOT the same of UI thread.
                             * Why we use BeginInvoke, Please see:
                             * http://msdn.microsoft.com/en-us/magazine/cc300429.aspx
                             * http://msdn.microsoft.com/en-us/magazine/cc188732.aspx
                             * http://stackoverflow.com/questions/1364116/avoiding-the-woes-of-invoke-begininvoke-in-cross-thread-winform-event-handling
                             */
                            BeginInvoke((Action)(() =>
                            {
                                /* this run in the UI thread */
                                if (cam_[idx] != null)
                                {
                                    switch (evt)
                                    {
                                        case Toupcam.eEVENT.EVENT_ERROR:
                                            OnEventError(idx);
                                            break;
                                        case Toupcam.eEVENT.EVENT_DISCONNECTED:
                                            OnEventDisconnected(idx);
                                            break;
                                        case Toupcam.eEVENT.EVENT_IMAGE:
                                            OnEventImage(idx);
                                            break;
                                        default:
                                            break;
                                    }
                                }
                            }));
                        }))
                        MessageBox.Show("Failed to start camera.");
                }

                timer1.Start();
            }
        }

        private void OnClosing(object sender, FormClosingEventArgs e)
        {
            cam_[0]?.Close();
            cam_[1]?.Close();
            cam_ = null;
        }

        private void checkBox1_CheckedChanged(object sender, EventArgs e)
        {
            cam_[0]?.put_AutoExpoEnable(checkBox1.Checked);
            cam_[1]?.put_AutoExpoEnable(checkBox1.Checked);
        }

        private void OnTimer1(object sender, EventArgs e)
        {
            if (cam_[0] != null)
            {
                uint nFrame = 0, nTime = 0, nTotalFrame = 0;
                if (cam_[0].get_FrameRate(out nFrame, out nTime, out nTotalFrame) && (nTime > 0))
                    label1.Text = string.Format("{0}; fps = {1:#.0}", nTotalFrame, ((double)nFrame) * 1000.0 / (double)nTime);
            }
            if (cam_[1] != null)
            {
                uint nFrame = 0, nTime = 0, nTotalFrame = 0;
                if (cam_[1].get_FrameRate(out nFrame, out nTime, out nTotalFrame) && (nTime > 0))
                    label2.Text = string.Format("{0}; fps = {1:#.0}", nTotalFrame, ((double)nFrame) * 1000.0 / (double)nTime);
            }
        }

        private void pictureBox1_Paint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            if (bmp_[0] != null)
            {
                g.DrawImage(bmp_[0], pictureBox1.ClientRectangle);
                g.DrawString(label1.Text, this.Font, SystemBrushes.ControlText, 0, 0);
            }
        }

        private void pictureBox2_Paint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            if (bmp_[1] != null)
            {
                g.DrawImage(bmp_[1], pictureBox2.ClientRectangle);
                g.DrawString(label2.Text, this.Font, SystemBrushes.ControlText, 0, 0);
            }
        }
    }
}
