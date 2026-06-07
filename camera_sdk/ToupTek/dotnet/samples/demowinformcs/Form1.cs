using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using static Toupcam;

namespace demowinformcs
{
    public partial class Form1 : Form
    {
        private Toupcam cam_ = null;
        private Bitmap bmp_ = null;
        private uint count_ = 0;
        private int[] piximapval_ = new int[30];

        private void OnClose()
        {
            button1.Text = "Start";

            button2.Enabled = false;
            button3.Enabled = false;
            button4.Enabled = false;
            button5.Enabled = false;
            trackBar1.Enabled = false;
            trackBar2.Enabled = false;
            trackBar3.Enabled = false;
            checkBox1.Enabled = false;
            comboBox1.Enabled = false;
            comboBox2.Enabled = false;
            comboBox3.Enabled = false;
            comboBox4.Enabled = false;
            radioButton1.Enabled = false;
            radioButton2.Enabled = false;
            radioButton1.Select();
            cam_.Close();
            cam_ = null;
            comboBox1.Items.Clear();
            comboBox2.Items.Clear();
            comboBox3.Items.Clear();
            comboBox4.Items.Clear();
        }

        private void OnEventError()
        {
            OnClose();
            MessageBox.Show("Generic error.");
        }

        private void OnEventDisconnected()
        {
            OnClose();
            MessageBox.Show("Camera disconnect.");
        }

        private void OnEventExposure()
        {
            uint nTime = 0;
            if (cam_.get_ExpoTime(out nTime))
            {
                trackBar1.Value = (int)nTime;
                label1.Text = nTime.ToString();
            }
        }

        private void OnEventImage()
        {
            if (bmp_ != null)
            {
                Toupcam.FrameInfoV4 info = new Toupcam.FrameInfoV4();
                bool bOK = false;
                try
                {
                    BitmapData bmpdata = bmp_.LockBits(new Rectangle(0, 0, bmp_.Width, bmp_.Height), ImageLockMode.WriteOnly, bmp_.PixelFormat);
                    try
                    {
                        bOK = cam_.PullImage(bmpdata.Scan0, 0, 24, bmpdata.Stride, out info); // check the return value
                    }
                    finally
                    {
                        bmp_.UnlockBits(bmpdata);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.ToString());
                }
                if (bOK)
                    pictureBox1.Invalidate();
            }
        }

        private void OnEventStillImage()
        {
            Toupcam.FrameInfoV4 info = new Toupcam.FrameInfoV4();
            if (cam_.PullImage(IntPtr.Zero, 1, 24, 0, out info))   /* peek the width and height */
            {
                Bitmap sbmp = new Bitmap((int)info.v3.width, (int)info.v3.height, PixelFormat.Format24bppRgb);
                bool bOK = false;
                try
                {
                    BitmapData bmpdata = sbmp.LockBits(new Rectangle(0, 0, sbmp.Width, sbmp.Height), ImageLockMode.WriteOnly, sbmp.PixelFormat);
                    try
                    {
                        bOK = cam_.PullImage(bmpdata.Scan0, 1, 24, bmpdata.Stride, out info); // check the return value
                    }
                    finally
                    {
                        sbmp.UnlockBits(bmpdata);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.ToString());
                }
                if (bOK)
                    sbmp.Save(string.Format("demowinformcs_{0}.jpg", ++count_), ImageFormat.Jpeg);
            }
        }

        public Form1()
        {
            InitializeComponent();
            pictureBox1.Width = ClientRectangle.Right - pictureBox1.Left - button1.Top;
            pictureBox1.Height = ClientRectangle.Height - 2 * button1.Top;
        }

        private void Form_SizeChanged(object sender, EventArgs e)
        {
            pictureBox1.Width = ClientRectangle.Right - pictureBox1.Left - button1.Top;
            pictureBox1.Height = ClientRectangle.Height - 2 * button1.Top;
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            button2.Enabled = false;
            button3.Enabled = false;
            button4.Enabled = false;
            button5.Enabled = false;
            trackBar1.Enabled = false;
            trackBar2.Enabled = false;
            trackBar3.Enabled = false;
            checkBox1.Enabled = false;
            comboBox1.Enabled = false;
            comboBox2.Enabled = false;
            comboBox3.Enabled = false;
            comboBox4.Enabled = false;
            trackBar4.Enabled = false;
            radioButton1.Enabled = false;
            radioButton2.Enabled = false;
            trackBar2.SetRange(Toupcam.TEMP_MIN, Toupcam.TEMP_MAX);
            trackBar3.SetRange(Toupcam.TINT_MIN, Toupcam.TINT_MAX);
            Toupcam.GigeEnable(null);
        }

        private void DelegateOnEventCallback(Toupcam.eEVENT evt)
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
                        default:
                            break;
                    }
                }
            }));
        }

        private void OnStart(object sender, EventArgs e)
        {
            if (cam_ != null)
                OnClose();
            else
            {
                Toupcam.DeviceV2[] arr = Toupcam.EnumV2();
                if (arr.Length <= 0)
                    MessageBox.Show("No camera found.");
                else if (1 == arr.Length)
                    startDevice(arr[0].id);
                else
                {
                    ContextMenuStrip ctxmenu = new ContextMenuStrip();
                    ctxmenu.ItemClicked += (nsender, ne) =>
                    {
                        startDevice((string)(ne.ClickedItem.Tag));
                    };
                    for (int i = 0; i < arr.Length; ++i)
                        ctxmenu.Items.Add(arr[i].displayname).Tag = arr[i].id;
                    ctxmenu.Show(button1, 0, 0);
                }

                if (cam_ != null)
                    button1.Text = "Stop";
            }
        }

        private void startDevice(string camId)
        {
            cam_ = Toupcam.Open(camId);
            if (cam_ != null)
            {
                checkBox1.Enabled = true;
                comboBox1.Enabled = true;
                comboBox2.Enabled = true;
                button2.Enabled = true;
                radioButton1.Enabled = true;
                radioButton2.Enabled = true;
                radioButton1.Select();
                InitExpoTimeRange();
                if (cam_.MonoMode)
                {
                    trackBar2.Enabled = false;
                    trackBar3.Enabled = false;
                    button3.Enabled = false;
                }
                else
                {
                    trackBar2.Enabled = true;
                    trackBar3.Enabled = true;
                    button3.Enabled = true;
                    OnEventTempTint();
                }

                //Trigger
                comboBox3.Items.Add("Isolated input");
                comboBox3.Items.Add("GPIO0");
                comboBox3.Items.Add("GPIO1");
                comboBox3.Items.Add("Counter");
                comboBox3.Items.Add("PWM");
                comboBox3.Items.Add("Software");

                //output mode
                comboBox4.Items.Add("Frame Trigger Wait");
                comboBox4.Items.Add("Exposure Active");
                comboBox4.Items.Add("Strobe");
                comboBox4.Items.Add("User Output");
                comboBox4.Items.Add("Counter Output");
                comboBox4.Items.Add("Timer Output");
                int noutputmode = 0;
                if (cam_.IoControl(1, eIoControType.IOCONTROLTYPE_GET_OUTPUTMODE, 0, out noutputmode))
                {
                    comboBox4.Enabled = true;
                    comboBox4.SelectedIndex = noutputmode;
                }

                //TEC
                int tecMode = 0;
                if (cam_.get_Option(eOPTION.OPTION_TEC, out tecMode))
                {
                    button4.Enabled = true;
                    if (tecMode == 1)
                    {
                        button4.Text = "TEC OFF";
                        trackBar4.Enabled = true;
                        int nTec;
                        cam_.get_Option(eOPTION.OPTION_TECTARGET, out nTec);
                        label5.Text = string.Format("TEC target = {0:#0.0}", ((double)nTec) / 10.0);
                    }
                    else
                    {
                        button4.Text = "TEC ON";
                        trackBar4.Enabled = false;
                    }
                }
                InitTecTargetRange();

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

                    /* pixel format (bitdepth)*/
                    int pixelFormatnum;
                    cam_.get_PixelFormatSupport(-1, out pixelFormatnum);
                    for (sbyte i = 0; i < pixelFormatnum; ++i)
                    {
                        int pixelFormatval;
                        cam_.get_PixelFormatSupport(i, out pixelFormatval);
                        piximapval_[i] = pixelFormatval;
                        comboBox2.Items.Add(Toupcam.PixelFormatName((ePIXELFORMAT)pixelFormatval));
                    }
                    comboBox2.SelectedIndex = 0;


                    int width = 0, height = 0;
                    if (cam_.get_Size(out width, out height))
                    {
                        /* The backend of Winform is GDI, which is different from WPF/UWP/WinUI's backend Direct3D/Direct2D.
                         * We use their respective native formats, Bgr24 in Winform, and Bgr32 in WPF/UWP/WinUI
                         */
                        bmp_ = new Bitmap(width, height, PixelFormat.Format24bppRgb);
                        if (!cam_.StartPullModeWithCallback(new Toupcam.DelegateEventCallback(DelegateOnEventCallback)))
                            MessageBox.Show("Failed to start camera.");
                        else
                        {
                            bool autoexpo = true;
                            cam_.get_AutoExpoEnable(out autoexpo);
                            checkBox1.Checked = autoexpo;
                            trackBar1.Enabled = !autoexpo;
                        }
                    }
                }

                timer1.Start();
            }
        }

        private void InitExpoTimeRange()
        {
            uint nMin = 0, nMax = 0, nDef = 0;
            if (cam_.get_ExpTimeRange(out nMin, out nMax, out nDef))
            {
                if (nMax > 10000000)
                    nMax = 10000000;//limit to 10s
                trackBar1.SetRange((int)nMin, (int)nMax);
            }
                
            OnEventExposure();
        }

        private void InitTecTargetRange()
        {
            int tecRange = 0;
            if (cam_.get_Option(eOPTION.OPTION_TECTARGET_RANGE, out tecRange))
                trackBar4.SetRange((short)(tecRange & 0xffff), (short)((tecRange >> 16) & 0xffff));
        }

        private void OnSnap(object sender, EventArgs e)
        {
            if (cam_ != null)
            {
                if (cam_.StillResolutionNumber <= 0)
                    bmp_?.Save(string.Format("demowinformcs_{0}.jpg", ++count_), ImageFormat.Jpeg);
                else
                {
                    ContextMenuStrip ctxmenu = new ContextMenuStrip();
                    ctxmenu.ItemClicked += (nsender, ne) =>
                    {
                        uint k = (uint)(ne.ClickedItem.Tag); //unbox
                        if (k < cam_.StillResolutionNumber)
                            cam_.Snap(k);
                    };
                    for (uint i = 0; i < cam_.ResolutionNumber; ++i)
                    {
                        int w = 0, h = 0;
                        cam_.get_Resolution(i, out w, out h);
                        ctxmenu.Items.Add(string.Format("{0} * {1}", w, h)).Tag = i; // box
                    }
                    ctxmenu.Show(button2, 0, 0);
                }
            }
        }

        private void OnClosing(object sender, FormClosingEventArgs e)
        {
            cam_?.Close();
            cam_ = null;
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
                        cam_.Stop();
                        cam_.put_eSize((uint)comboBox1.SelectedIndex);

                        InitExpoTimeRange();
                        OnEventTempTint();

                        int width = 0, height = 0;
                        if (cam_.get_Size(out width, out height))
                        {
                            bmp_ = new Bitmap(width, height, PixelFormat.Format24bppRgb);
                            cam_.StartPullModeWithCallback(new Toupcam.DelegateEventCallback(DelegateOnEventCallback));
                        }
                    }
                }
            }
        }

        private void checkBox1_CheckedChanged(object sender, EventArgs e)
        {
            cam_?.put_AutoExpoEnable(checkBox1.Checked);
            trackBar1.Enabled = !checkBox1.Checked;
        }

        private void OnExpoValueChange(object sender, EventArgs e)
        {
            if ((!checkBox1.Checked) && (cam_ != null))
            {
                uint n = (uint)trackBar1.Value;
                cam_.put_ExpoTime(n);
                label1.Text = n.ToString();
            }
        }

        private void OnEventTempTint()
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

        private void OnWhiteBalanceOnce(object sender, EventArgs e)
        {
            cam_?.AwbOnce();
        }

        private void OnTempTintChanged(object sender, EventArgs e)
        {
            cam_?.put_TempTint(trackBar2.Value, trackBar3.Value);
            label2.Text = trackBar2.Value.ToString();
            label3.Text = trackBar3.Value.ToString();
        }

        private void OnTimer1(object sender, EventArgs e)
        {
            if (cam_ != null)
            {
                uint nFrame = 0, nTime = 0, nTotalFrame = 0;
                short nTec = 0;
                if (cam_.get_FrameRate(out nFrame, out nTime, out nTotalFrame) && (nTime > 0))
                    label4.Text = string.Format("Total = {0}; fps = {1:#0.0}", nTotalFrame, ((double)nFrame) * 1000.0 / (double)nTime);
                if (cam_.get_Temperature(out nTec))
                    label4.Text += string.Format("\nTemperature = {0:#0.0}", ((double)nTec) / 10.0);
            }
        }

        private void OnSelectPixelFormat(object sender, EventArgs e)
        {
            if (cam_ != null)
            {
                int pixelFormat = comboBox2.SelectedIndex;
                cam_.put_Option(eOPTION.OPTION_PIXEL_FORMAT, piximapval_[pixelFormat]);
            }
        }

        private void OnStartTEC(object sender, EventArgs e)
        {
            int tecMode = 0;
            cam_.get_Option(eOPTION.OPTION_TEC, out tecMode);
            if (tecMode == 1)
            {
                button4.Text = "TEC ON";
                trackBar4.Enabled = false;
                cam_.put_Option(eOPTION.OPTION_TEC, 0);
            }
            else
            {
                button4.Text = "TEC OFF";
                trackBar4.Enabled = true;
                cam_.put_Option(eOPTION.OPTION_TEC, 1);
            }
        }

        private void OnTecTargetChanged(object sender, EventArgs e)
        {
            int nTec = trackBar4.Value;
            cam_.put_Option(eOPTION.OPTION_TECTARGET, nTec);
            label5.Text = string.Format("TEC target = {0:#.0}", ((double)nTec) / 10.0);
        }

        private void OnVideoMode(object sender, EventArgs e)
        {
            cam_.put_Option(eOPTION.OPTION_TRIGGER, 0);
            comboBox3.Enabled = false;
            button5.Enabled = false;
        }

        private void OnTriggerMode(object sender, EventArgs e)
        {
            int nval;
            if (cam_.IoControl(0, eIoControType.IOCONTROLTYPE_GET_TRIGGERSOURCE, 0, out nval))
            {
                cam_.put_Option(eOPTION.OPTION_TRIGGER, 2);
                comboBox3.Enabled = true;
                comboBox3.SelectedIndex = nval;
                if (nval == 5)
                    button5.Enabled = true;
            }
            else
            {
                cam_.put_Option(eOPTION.OPTION_TRIGGER, 1);
                nval = 5;
                comboBox3.Enabled = false;
                button5.Enabled = true;
            }
        }

        private void OnTriggerOne(object sender, EventArgs e)
        {
            cam_.Trigger(1);
        }

        private void OnTriggerSourceSelect(object sender, EventArgs e)
        {
            int nval = comboBox3.SelectedIndex;
            cam_.IoControl(0, eIoControType.IOCONTROLTYPE_SET_TRIGGERSOURCE, nval);
            if (nval == 5)
                button5.Enabled = true;
            else
                button5.Enabled = false;
        }

        private void OnOutputModeSelect(object sender, EventArgs e)
        {
            int nval = comboBox4.SelectedIndex;
            cam_.IoControl(1, eIoControType.IOCONTROLTYPE_SET_OUTPUTMODE, nval);
        }

        private void pictureBox1_Paint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            if (bmp_ != null)
            {
                g.DrawImage(bmp_, pictureBox1.ClientRectangle);
                g.DrawString(label4.Text, this.Font, SystemBrushes.ControlText, 0, 0);
            }
        }
    }
}
