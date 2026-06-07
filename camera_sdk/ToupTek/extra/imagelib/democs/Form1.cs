using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using System.Windows.Forms;
using System.Runtime.InteropServices;

namespace democs
{
    public partial class Form1 : Form
    {
        private Toupcam.DeviceV2 dev_;
        private Toupcam cam_ = null;
        private Bitmap bmp_ = null;
        private uint MSG_CAMEVENT = 0x8001; // WM_APP = 0x8000

        private void ImageLibSave(IntPtr data, int width, int height)
        {
            ImageLib.BITMAPINFOHEADER h = new ImageLib.BITMAPINFOHEADER();
            h.biSize = (uint)Marshal.SizeOf(typeof(ImageLib.BITMAPINFOHEADER));
            h.biWidth = width;
            h.biHeight = height;
            h.biPlanes = 1;
            h.biBitCount = 24;

            ImageLib.XIMAGEINFO info = new ImageLib.XIMAGEINFO();
            info.cCamera = dev_.model.name; /* just to demo exif */
            info.cSN = cam_.SerialNumber;
            IntPtr dib = Marshal.AllocCoTaskMem((int)h.biSize + ImageLib.TDIBWIDTHBYTES(width * 24) * height);
            Toupcam.memcpy(Toupcam.IncIntPtr(dib, (int)h.biSize), data, new IntPtr(ImageLib.TDIBWIDTHBYTES(width * 24) * height));
            Marshal.StructureToPtr(h, dib, false);
            ImageLib.Save("democs.jpg", dib, ref info);
            Marshal.FreeCoTaskMem(dib);
        }

        private void ImageLibSaveDng(IntPtr data, int width, int height, ushort bitdepth, uint fourcc)
        {
            ImageLib.BITMAPINFOHEADER h = new ImageLib.BITMAPINFOHEADER();
            h.biSize = (uint)Marshal.SizeOf(typeof(ImageLib.BITMAPINFOHEADER));
            h.biWidth = width;
            h.biHeight = height;
            h.biPlanes = 1;
            h.biBitCount = bitdepth;
            h.biCompression = fourcc;

            ImageLib.XIMAGEINFO info = new ImageLib.XIMAGEINFO();
            info.iCodec = 0; /* 0->lossless jpeg compress, 1->none */
            info.cCamera = dev_.model.name; /* just to demo exif */
            info.cSN = cam_.SerialNumber;
            IntPtr dib = Marshal.AllocCoTaskMem((int)h.biSize + width * height * (bitdepth > 8 ? 2 : 1));
            Toupcam.memcpy(Toupcam.IncIntPtr(dib, (int)h.biSize), data, new IntPtr(width * height * (bitdepth > 8 ? 2 : 1)));
            Marshal.StructureToPtr(h, dib, false);
            ImageLib.Save("democs.dng", dib, ref info);
            Marshal.FreeCoTaskMem(dib);
        }

        private void OnEventError()
        {
            if (cam_ != null)
            {
                cam_.Close();
                cam_ = null;
            }
            MessageBox.Show("Error");
        }

        private void OnEventDisconnected()
        {
            if (cam_ != null)
            {
                cam_.Close();
                cam_ = null;
            }
            MessageBox.Show("The camera is disconnected, maybe has been pulled out.");
        }

        private void OnEventExposure()
        {
            if (cam_ != null)
            {
                uint nTime = 0;
                if (cam_.get_ExpoTime(out nTime))
                {
                    trackBar1.Value = (int)nTime;
                    label1.Text = (nTime / 1000).ToString() + " ms";
                }
            }
        }

        private void OnEventImage()
        {
            if (bmp_ != null)
            {
                BitmapData bmpdata = bmp_.LockBits(new Rectangle(0, 0, bmp_.Width, bmp_.Height), ImageLockMode.WriteOnly, bmp_.PixelFormat);

                Toupcam.FrameInfoV2 info = new Toupcam.FrameInfoV2();
                cam_.PullImageV2(bmpdata.Scan0, 24, out info);

                bmp_.UnlockBits(bmpdata);

                pictureBox1.Image = bmp_;
                pictureBox1.Invalidate();
            }
        }

        private void OnEventStillImage()
        {
            Toupcam.FrameInfoV2 info = new Toupcam.FrameInfoV2();
            if (cam_.PullStillImageV2(IntPtr.Zero, 24, out info))   /* peek the width and height */
            {
                Bitmap sbmp = new Bitmap((int)info.width, (int)info.height, PixelFormat.Format24bppRgb);

                BitmapData bmpdata = sbmp.LockBits(new Rectangle(0, 0, sbmp.Width, sbmp.Height), ImageLockMode.WriteOnly, sbmp.PixelFormat);
                cam_.PullStillImageV2(bmpdata.Scan0, 24, out info);
                ImageLibSave(bmpdata.Scan0, sbmp.Width, sbmp.Height);
                sbmp.UnlockBits(bmpdata);
            }
        }

        public Form1()
        {
            InitializeComponent();
            pictureBox1.Width = ClientRectangle.Right - button1.Bounds.Right - 20;
            pictureBox1.Height = ClientRectangle.Height - 8;
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            button2.Enabled = button3.Enabled = trackBar1.Enabled = trackBar2.Enabled = trackBar3.Enabled = checkBox1.Enabled = comboBox1.Enabled = false;
        }

        [System.Security.Permissions.PermissionSet(System.Security.Permissions.SecurityAction.Demand, Name = "FullTrust")]
        protected override void WndProc(ref Message m)
        {
            if (MSG_CAMEVENT == m.Msg)
            {
                switch ((Toupcam.eEVENT)m.WParam.ToInt32())
                {
                    case Toupcam.eEVENT.EVENT_ERROR:
                        OnEventError();
                        break;
                    case Toupcam.eEVENT.EVENT_DISCONNECTED:
                        OnEventDisconnected();
                        break;
                    case Toupcam.eEVENT.EVENT_EXPOSURE:
                        OnEventExposure();
                        break;
                    case Toupcam.eEVENT.EVENT_IMAGE:
                        OnEventImage();
                        break;
                    case Toupcam.eEVENT.EVENT_STILLIMAGE:
                        OnEventStillImage();
                        break;
                    case Toupcam.eEVENT.EVENT_TEMPTINT:
                        OnEventTempTint();
                        break;
                }
                return;
            }
            base.WndProc(ref m);
        }

        private void OnStart(object sender, EventArgs e)
        {
            if (cam_ != null)
                return;

            Toupcam.DeviceV2[] arr = Toupcam.EnumV2();
            if (arr.Length <= 0)
                MessageBox.Show("no device");
            else
            {
                dev_ = arr[0];
                cam_ = Toupcam.Open(dev_.id);
                if (cam_ != null)
                {
                    checkBox1.Enabled = trackBar1.Enabled = trackBar2.Enabled = trackBar3.Enabled = comboBox1.Enabled = button2.Enabled = button3.Enabled = true;
                    button2.ContextMenuStrip = null;
                    InitSnapContextMenuAndExpoTimeRange();

                    trackBar2.SetRange(2000, 15000);
                    trackBar3.SetRange(200, 2500);
                    OnEventTempTint();

                    uint resnum = cam_.ResolutionNumber;
                    uint eSize = 0;
                    if (cam_.get_eSize(out eSize))
                    {
                        for (uint i = 0; i < resnum; ++i)
                        {
                            int w = 0, h = 0;
                            if (cam_.get_Resolution(i, out w, out h))
                                comboBox1.Items.Add(w.ToString() + "*" + h.ToString());
                        }
                        comboBox1.SelectedIndex = (int)eSize;

                        int width = 0, height = 0;
                        if (cam_.get_Size(out width, out height))
                        {
                            bmp_ = new Bitmap(width, height, PixelFormat.Format24bppRgb);
                            if (!cam_.StartPullModeWithWndMsg(this.Handle, MSG_CAMEVENT))
                                MessageBox.Show("failed to start device");
                            else
                            {
                                bool autoexpo = true;
                                cam_.get_AutoExpoEnable(out autoexpo);
                                checkBox1.Checked = autoexpo;
                                trackBar1.Enabled = !checkBox1.Checked;
                            }
                        }
                    }
                }
            }
        }

        private void SnapClickedHandler(object sender, ToolStripItemClickedEventArgs e)
        {
            int k = button2.ContextMenuStrip.Items.IndexOf(e.ClickedItem);
            if (k >= 0)
                cam_.Snap((uint)k);
        }

        private void InitSnapContextMenuAndExpoTimeRange()
        {
            if (cam_ == null)
                return;

            uint nMin = 0, nMax = 0, nDef = 0;
            if (cam_.get_ExpTimeRange(out nMin, out nMax, out nDef))
                trackBar1.SetRange((int)nMin, (int)nMax);
            OnEventExposure();

            if (cam_.StillResolutionNumber <= 0)
                return;
            
            button2.ContextMenuStrip = new ContextMenuStrip();
            button2.ContextMenuStrip.ItemClicked += new ToolStripItemClickedEventHandler(this.SnapClickedHandler);

            if (cam_.StillResolutionNumber < cam_.ResolutionNumber)
            {
                uint eSize = 0;
                if (cam_.get_eSize(out eSize))
                {
                    if (0 == eSize)
                    {
                        StringBuilder sb = new StringBuilder();
                        int w = 0, h = 0;
                        cam_.get_Resolution(eSize, out w, out h);
                        sb.Append(w);
                        sb.Append(" * ");
                        sb.Append(h);
                        button2.ContextMenuStrip.Items.Add(sb.ToString());
                        return;
                    }
                }
            }

            for (uint i = 0; i < cam_.ResolutionNumber; ++i)
            {
                StringBuilder sb = new StringBuilder();
                int w = 0, h = 0;
                cam_.get_Resolution(i, out w, out h);
                sb.Append(w);
                sb.Append(" * ");
                sb.Append(h);
                button2.ContextMenuStrip.Items.Add(sb.ToString());
            }
        }

        private void OnSnap(object sender, EventArgs e)
        {
            if (cam_ != null)
            {
                if (cam_.StillResolutionNumber <= 0)
                {
                    if (bmp_ != null)
                    {
                        BitmapData bmpdata = bmp_.LockBits(new Rectangle(0, 0, bmp_.Width, bmp_.Height), ImageLockMode.WriteOnly, bmp_.PixelFormat);
                        ImageLibSave(bmpdata.Scan0, bmp_.Width, bmp_.Height);
                        bmp_.UnlockBits(bmpdata);
                    }
                }
                else
                {
                    if (button2.ContextMenuStrip != null)
                        button2.ContextMenuStrip.Show(Cursor.Position);
                }
            }
        }

        private void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (cam_ != null)
            {
                cam_.Close();
                cam_ = null;
            }
        }

        private void OnSelectResolution(object sender, EventArgs e)
        {
            if (cam_ != null)
            {
                uint eSize = 0;
                if (cam_.get_eSize(out eSize))
                {
                    if (eSize != comboBox1.SelectedIndex)
                    {
                        button2.ContextMenuStrip = null;

                        cam_.Stop();
                        cam_.put_eSize((uint)comboBox1.SelectedIndex);

                        InitSnapContextMenuAndExpoTimeRange();
                        OnEventTempTint();

                        int width = 0, height = 0;
                        if (cam_.get_Size(out width, out height))
                        {
                            bmp_ = new Bitmap(width, height, PixelFormat.Format24bppRgb);
                            cam_.StartPullModeWithWndMsg(this.Handle, MSG_CAMEVENT);
                        }
                    }
                }
            }
        }

        private void checkBox1_CheckedChanged(object sender, EventArgs e)
        {
            if (cam_ != null)
                cam_.put_AutoExpoEnable(checkBox1.Checked);
            trackBar1.Enabled = !checkBox1.Checked;
        }

        private void OnExpoValueChange(object sender, EventArgs e)
        {
            if (!checkBox1.Checked)
            {
                if (cam_ != null)
                {
                    uint n = (uint)trackBar1.Value;
                    cam_.put_ExpoTime(n);
                    label1.Text = (n / 1000).ToString() + " ms";
                }
            }
        }

        private void Form_SizeChanged(object sender, EventArgs e)
        {
            pictureBox1.Width = ClientRectangle.Right - button1.Bounds.Right - 20;
            pictureBox1.Height = ClientRectangle.Height - 8;
        }

        private void OnEventTempTint()
        {
            if (cam_ != null)
            {
                int nTemp = 0, nTint = 0;
                if (cam_.get_TempTint(out nTemp, out nTint))
                {
                    label2.Text = nTemp.ToString();
                    label3.Text = nTint.ToString();
                    trackBar2.Value = nTemp;
                    trackBar3.Value = nTint;
                }
            }
        }

        private void OnWhiteBalanceOnce(object sender, EventArgs e)
        {
            if (cam_ != null)
                cam_.AwbOnce();
        }

        private void OnTempTintChanged(object sender, EventArgs e)
        {
            if (cam_ != null)
                cam_.put_TempTint(trackBar2.Value, trackBar3.Value);
            label2.Text = trackBar2.Value.ToString();
            label3.Text = trackBar3.Value.ToString();
        }
    }
}
