using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace liveedf
{
    public partial class Form1 : Form
    {
        private Toupcam cam_;
        private Bitmap bmp1_;
		private Bitmap bmp2_;
        private Liveedf edf_;

        private void OnCamError(string str)
        {
            cam_?.Close();
			cam_ = null;
            MessageBox.Show(str);
        }

        private void OnEventImage()
        {
            if (bmp1_ != null)
            {
                Toupcam.FrameInfoV4 info = new Toupcam.FrameInfoV4();
                try
                {
                    BitmapData bmpdata1 = bmp1_.LockBits(new Rectangle(0, 0, bmp1_.Width, bmp1_.Height), ImageLockMode.WriteOnly, bmp1_.PixelFormat);
                    BitmapData bmpdata2 = bmp2_.LockBits(new Rectangle(0, 0, bmp2_.Width, bmp2_.Height), ImageLockMode.WriteOnly, bmp2_.PixelFormat);
                    try
                    {
                        edf_.Pull(cam_.Handle, 1, bmpdata1.Scan0, 24, 0, out info); // check the return value
                        edf_.ReadData(bmpdata2.Scan0, bmpdata2.Stride);
                    }
                    finally
                    {
                        bmp1_.UnlockBits(bmpdata1);
                        bmp2_.UnlockBits(bmpdata2);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.ToString());
                }
                pictureBox1.Image = bmp1_;
                pictureBox2.Image = bmp2_;
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
            if (cam_ != null)
                return;

            Toupcam.DeviceV2[] arr = Toupcam.EnumV2();
            if (arr.Length <= 0)
                MessageBox.Show("No camera found.");
            else
            {
                if (arr.Length > 0)
                    startDevice(arr[0].id);
                checkBox1.Enabled = true;
                checkBox1.Checked = true;
                timer1.Start();
            }
        }

        private void startDevice(string camId)
        {
            cam_ = Toupcam.Open(camId);
            if (cam_ != null)
            {
                int width = 0, height = 0;
                if (cam_.get_Size(out width, out height))
                {
                    /* The backend of Winform is GDI, which is different from WPF/UWP/WinUI's backend Direct3D/Direct2D.
                     * We use their respective native formats, Bgr24 in Winform, and Bgr32 in WPF/UWP/WinUI
                     */
                    bmp1_ = new Bitmap(width, height, PixelFormat.Format24bppRgb);
                    bmp2_ = new Bitmap(width, height, PixelFormat.Format24bppRgb);
                    edf_ = Liveedf.New(Liveedf.eFormat.eRGB24, Liveedf.eMethod.ePyr_Max,
                        (int result, IntPtr outData, int stride, int outW, int outH, int outType) =>
                        {
                            //nothing to do, we use ReadData instead of callback delegate
                        },
                        (Liveedf.eEvent evt) =>
                        {
                            BeginInvoke((Action)(() =>
                            {
                                /* this run in the UI thread */
                                switch (evt)
                                {
                                    case Liveedf.eEvent.eERROR:
                                        MessageBox.Show("Edf generic error");
                                        break;
                                    case Liveedf.eEvent.eNOMEM:
                                        MessageBox.Show("Edf out of memory");
                                        break;
                                    default:
                                        break;
                                }
                            }));
                        });
                    if (edf_ == null)
                        MessageBox.Show("Failed to new edf");
                    else
                    {
                        edf_.Start();
                        if (!cam_.StartPullModeWithCallback((Toupcam.eEVENT evt) =>
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
                                if (cam_ != null)
                                {
                                    switch (evt)
                                    {
                                        case Toupcam.eEVENT.EVENT_ERROR:
                                            OnCamError("Generic error");
                                            break;
                                        case Toupcam.eEVENT.EVENT_DISCONNECTED:
                                            OnCamError("Camera disconnect");
                                            break;
                                        case Toupcam.eEVENT.EVENT_IMAGE:
                                            OnEventImage();
                                            break;
                                        default:
                                            break;
                                    }
                                }
                            }));
                        }))
                            MessageBox.Show("Failed to start camera");
                    }
                }

                timer1.Start();
            }
        }

        private void OnClosing(object sender, FormClosingEventArgs e)
        {
            edf_?.Stop();
            edf_ = null;
            cam_?.Close();
            cam_ = null;
        }

        private void checkBox1_CheckedChanged(object sender, EventArgs e)
        {
            cam_?.put_AutoExpoEnable(checkBox1.Checked);
        }

        private void OnTimer1(object sender, EventArgs e)
        {
            if (cam_ != null)
            {
                uint nFrame = 0, nTime = 0, nTotalFrame = 0;
                if (cam_.get_FrameRate(out nFrame, out nTime, out nTotalFrame) && (nTime > 0))
                    label1.Text = string.Format("{0}; fps = {1:#.0}", nTotalFrame, ((double)nFrame) * 1000.0 / (double)nTime);
            }
        }
    }
}
