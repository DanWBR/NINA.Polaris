using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using static Livestitch;

namespace livestitch
{
    public struct StitchArgs
    {
        public int outW;
        public int outH;
        public int curW;
        public int curH;
        public int posX;
        public int posY;
        public int bSize;
    }

    public partial class Form1 : Form
    {
        private Toupcam cam_;
        private Bitmap bmp1_;
        private Bitmap bmp2_;
        private Livestitch stitch_;
        private StitchArgs stitchinfo_;
        private Pen pen_;
        private Rectangle rect_ = Rectangle.Empty;
        private Object lock_ = new Object();
        private double framerate_;
        private bool bstitch_;
        private int crop_;
        private bool isRedraw_;
        private float sharpness_;
        private static Livestitch.IMAGEPRO_MALLOC ipmalloc;
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
                    try
                    {
                        stitch_?.Pull(cam_.Handle, Convert.ToInt32(bstitch_), bmpdata1.Scan0, 24, 0, out info); // check the return value
                    }
                    finally
                    {
                        bmp1_.UnlockBits(bmpdata1);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.ToString());
                }
                if (bstitch_)
                    pictureBox2.Invalidate(rect_);
                else
                    pictureBox1.Invalidate(true);
            }
        }

        public Form1()
        {
            InitializeComponent();
            bstitch_ = false;
            crop_ = 1;
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            checkBox1.Enabled = false;
            checkBox2.Enabled = false;
        }

        private void OnStart(object sender, EventArgs e)
        {
            if (cam_ != null)
            {
                cam_?.Close();
                cam_ = null;
                stitch_?.Close();
                stitch_ = null;
                bstitch_ = false;
                timer1.Stop();
                bmp1_?.Dispose();
                bmp1_ = null;
                bmp2_?.Dispose();
                bmp2_ = null;
                checkBox1.Enabled = false;
                checkBox2.Enabled = false;
                button2.Enabled = false;
                button1.Text = "open";
                button2.Text = "Start Stitch";
            }
            else
            {
                Toupcam.DeviceV2[] arr = Toupcam.EnumV2();
                if (arr.Length <= 0)
                    MessageBox.Show("No camera found.");
                else
                {
                    if (arr.Length > 0)
                        startDevice(arr[0].id);
                    checkBox1.Enabled = true;
                    checkBox1.Checked = true;
                    checkBox2.Enabled = true;
                    checkBox2.Checked = true;
                    button2.Enabled = true;
                    timer1.Start();
                    button1.Text = "close";
                }
            }
        }
        private void OnStitch(object sender, EventArgs e)
        {
            if (bstitch_)
            {
                button2.Text = "Start Stitch";
                bstitch_ = false;
                checkBox1.Enabled = true;
                checkBox2.Enabled = true;
                backgroundWorker1.CancelAsync();
                Livestitch.BitmapInfo info = new Livestitch.BitmapInfo();
                stitch_?.Stop(1, crop_, ref info);
                Bitmap result = new Bitmap(info.bmiHeader.biWidth, info.bmiHeader.biHeight, ((info.bmiHeader.biWidth * 3 + 3) & ~3), PixelFormat.Format24bppRgb, info.buffer);
                DateTime now = DateTime.Now;
                string filename = string.Format("stitch_{0}{1}{2}.bmp", now.Hour, now.Second, now.Millisecond);
                result.Save(filename);
                bmp2_?.Dispose();
                bmp2_ = null;
            }
            else
            {
                button2.Text = "Stop Stitch";
                bstitch_ = true;
                checkBox1.Checked = false;
                checkBox1.Enabled = false;
                checkBox2.Enabled = false;
                pen_ = new Pen(Color.Red, 3);
                sharpness_ = 0.0f;
                stitch_?.Start();
            }
        }

        private void startDevice(string camId)
        {
            cam_ = Toupcam.Open(camId);
            if (cam_ != null)
            {
                ipmalloc = new IMAGEPRO_MALLOC(
                    (IntPtr size) =>
                    {
                        return Marshal.AllocHGlobal(size);
                    }
                    );
                Livestitch.init(ipmalloc);
                int width = 0, height = 0;
                if (cam_.get_Size(out width, out height))
                {
                    bmp1_ = new Bitmap(width, height, PixelFormat.Format24bppRgb);
                    cam_.StartPullModeWithCallback(
                        (Toupcam.eEVENT evt) =>
                        {
                            BeginInvoke((Action)(() =>
                            {
                                switch (evt)
                                {
                                    case Toupcam.eEVENT.EVENT_IMAGE:
                                        OnEventImage();
                                        break;
                                    case Toupcam.eEVENT.EVENT_ERROR:
                                        OnCamError("Generic Error");
                                        break;
                                    case Toupcam.eEVENT.EVENT_DISCONNECTED:
                                        OnCamError("Camera disconnect");
                                        break;
                                }
                            }));
                        }
                        );
                    stitch_ = Livestitch.New(Livestitch.eFormat.eRGB24, 1, width, height, 0,
                        (IntPtr outData, int stride, int outW, int outH, int curW, int curH, int curType,
                                        int posX, int posY, eQuality quality, float sharpness, int bUpdate, int bSize) =>
                        {
                            BeginInvoke((Action)(() =>
                            {
                                switch (quality)
                                {
                                    case eQuality.eZERO:
                                        pen_.Color = Color.Red;
                                        label2.Text = "state is zero";
                                        break;
                                    case eQuality.eGOOD:
                                        pen_.Color = Color.Green;
                                        label2.Text = "state is good";
                                        break;
                                    case eQuality.eCAUTION:
                                        pen_.Color = Color.Yellow;
                                        label2.Text = "state is caution";
                                        break;
                                    case eQuality.eBAD:
                                        pen_.Color = Color.Red;
                                        label2.Text = "state is bad";
                                        break;
                                    case eQuality.eWARNING:
                                        pen_.Color = Color.Yellow;
                                        label2.Text = "state is warning";
                                        break;
                                }
                                stitchinfo_.outW = outW;
                                stitchinfo_.outH = outH;
                                stitchinfo_.curW = curW;
                                stitchinfo_.curH = curH;
                                stitchinfo_.posX = posX;
                                stitchinfo_.posY = posY;
                                stitchinfo_.bSize = bSize;
                                sharpness_ = sharpness;
                                float zoomX = 1.0f * pictureBox2.Width / outW;
                                float zoomY = 1.0f * pictureBox2.Height / outH;
                                Rectangle rect = new Rectangle();
                                rect.X = (int)(posX * zoomX);
                                rect.Y = (int)(posY * zoomY);
                                rect.Width = (int)(curW * zoomX);
                                rect.Height = (int)(curH * zoomY);
                                bool isReadData = false;
                                if (rect_ != rect && quality == eQuality.eGOOD)
                                {
                                    rect_ = rect;
                                    isReadData = true;
                                }
                                if (bmp2_ == null || bSize != 0)
                                {
                                    isReadData = true;
                                    if (bmp2_ != null)
                                        bmp2_.Dispose();
                                    bmp2_ = new Bitmap(outW, outH, PixelFormat.Format24bppRgb);
                                }
                                if (!backgroundWorker1.IsBusy && isReadData)
                                    backgroundWorker1.RunWorkerAsync();
                            }));
                        },
                        (Livestitch.eEvent evt) =>
                        {
                            BeginInvoke((Action)(() =>
                            {
                                /* this run in the UI thread */
                                switch (evt)
                                {
                                    case Livestitch.eEvent.eERROR:
                                        MessageBox.Show("stitch generic error");
                                        break;
                                    case Livestitch.eEvent.eNOMEM:
                                        MessageBox.Show("stitch out of memory");
                                        break;
                                    default:
                                        break;
                                }
                            }));
                        });
                    if (stitch_ == null)
                        MessageBox.Show("Failed to new stitch");
                    else
                        GC.KeepAlive(stitch_);
                }
                timer1.Start();
            }
        }

        private void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (bstitch_)
            {
                Livestitch.BitmapInfo info = new Livestitch.BitmapInfo();
                stitch_?.Stop(1, crop_, ref info);
            }
            if (bmp1_ != null)
            {
                bmp1_.Dispose();
                bmp1_ = null;
            }
            if (bmp2_ != null)
            {
                bmp2_.Dispose();
                bmp2_ = null;
            }
            stitch_ = null;
            cam_?.Close();
            cam_ = null;
        }

        private void checkBox1_CheckedChanged(object sender, EventArgs e)
        {
            cam_?.put_AutoExpoEnable(checkBox1.Checked);
        }

        private void checkBox2_CheckedChanged(object sender, EventArgs e)
        {
            crop_ = Convert.ToInt32(checkBox2.Checked);
        }

        private void OnTimer1(object sender, EventArgs e)
        {
            if (cam_ != null)
            {
                uint nFrame = 0, nTime = 0, nTotalFrame = 0;
                if (cam_.get_FrameRate(out nFrame, out nTime, out nTotalFrame) && (nTime > 0))
                {
                    framerate_ = ((double)nFrame) * 1000.0 / (double)nTime;
                    label1.Text = string.Format("{0}; fps = {1:#.0}", nTotalFrame, framerate_);
                }
            }
        }

        private void pictureBox1_Paint(object sender, PaintEventArgs e)
        {
            if (bstitch_)
                return;
            if (bmp1_ != null)
                e.Graphics.DrawImage(bmp1_, e.ClipRectangle);
        }
		
        private void pictureBox2_Paint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            if (bmp1_ != null && bmp2_ != null)
            {
                if (isRedraw_ && e.ClipRectangle != rect_)
                {
                    lock (lock_) {
                        g.DrawImage(bmp2_, e.ClipRectangle);
                    }
                    isRedraw_ = false;
                }
                g.DrawImage(bmp1_, rect_, new Rectangle(0, 0, stitchinfo_.curW, stitchinfo_.curH), GraphicsUnit.Pixel);
                g.DrawRectangle(pen_, rect_);
                g.DrawString(string.Format("{0:#.0}%", sharpness_ * 100),this.Font, SystemBrushes.ControlText, rect_.X, rect_.Y);
            }
        }
		
        private void backgroundWorker1_DoWork(object sender, System.ComponentModel.DoWorkEventArgs e)
        {
            if (bmp2_ != null && bstitch_)
            {
                try
                {
                    lock (lock_) {
                        BitmapData bmpdata = bmp2_.LockBits(new Rectangle(0, 0, stitchinfo_.outW, stitchinfo_.outH), ImageLockMode.WriteOnly, bmp2_.PixelFormat);
                        try {
                            stitch_.ReadData(bmpdata.Scan0, stitchinfo_.outW, stitchinfo_.outH, 0, 0, 0, 0);
                        }
                        finally {
                            bmp2_.UnlockBits(bmpdata);
                        }
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.ToString());
                }
                e.Result = true;
            }
        }
		
        private void backgroundWorker1_RunWorkerCompleted(object sender, System.ComponentModel.RunWorkerCompletedEventArgs e)
        {
            if (e.Cancelled == true || e.Result == null)
                return;
            if ((bool)e.Result)
            {
                isRedraw_ = true;
                pictureBox2.Invalidate();
            }
        }

        private void button3_Click(object sender, EventArgs e)
        {
            cam_?.AwbOnce();
        }
    }
}