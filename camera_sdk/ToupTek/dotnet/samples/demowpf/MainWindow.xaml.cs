using System;
using System.IO;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace demowpf
{
    public partial class MainWindow : Window
    {
        private Toupcam cam_ = null;
        private WriteableBitmap bmp_ = null;
        private bool started_ = false;
        private DispatcherTimer timer_ = null;
        private uint count_ = 0;

        public MainWindow()
        {
            InitializeComponent();

            snap_.IsEnabled = false;
            combo_.IsEnabled = false;
            auto_exposure_.IsEnabled = false;
            white_balance_once_.IsEnabled = false;
            slider_expotime_.IsEnabled = false;
            slider_temp_.IsEnabled = false;
            slider_tint_.IsEnabled = false;
            slider_temp_.Minimum = Toupcam.TEMP_MIN;
            slider_temp_.Maximum = Toupcam.TEMP_MAX;
            slider_tint_.Minimum = Toupcam.TINT_MIN;
            slider_tint_.Maximum = Toupcam.TINT_MAX;

            Closing += (sender, e) =>
            {
                cam_?.Close();
                cam_ = null;
            };
        }

        private void OnEventError()
        {
            cam_.Close();
            cam_ = null;
            MessageBox.Show("Generic error.");
        }

        private void OnEventDisconnected()
        {
            cam_.Close();
            cam_ = null;
            MessageBox.Show("Camera disconnect.");
        }

        private void OnEventExposure()
        {
            uint nTime = 0;
            if (cam_.get_ExpoTime(out nTime))
            {
                slider_expotime_.Value = (int)nTime;
                label_expotime_.Content = nTime.ToString();
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
                    bmp_.Lock();
                    try
                    {
                        bOK = cam_.PullImage(bmp_.BackBuffer, 0, 32, bmp_.BackBufferStride, out info); // check the return value
                        bmp_.AddDirtyRect(new Int32Rect(0, 0, bmp_.PixelWidth, bmp_.PixelHeight));
                    }
                    finally
                    {
                        bmp_.Unlock();
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.ToString());
                }
            }
        }

        private void SaveToFile(BitmapSource bmp)
        {
            using (FileStream fileStream = new FileStream(string.Format("demowpf_{0}.jpg", ++count_), FileMode.Create))
            {
                if (fileStream != null)
                {
                    BitmapEncoder encoder = new JpegBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bmp));
                    encoder.Save(fileStream);
                }
            }
        }

        private void OnEventStillImage()
        {
            Toupcam.FrameInfoV4 info = new Toupcam.FrameInfoV4();
            if (cam_.PullImage(IntPtr.Zero, 1, 32, 0, out info))   /* peek the width and height */
            {
                WriteableBitmap bmp = new WriteableBitmap((int)info.v3.width, (int)info.v3.height, 0, 0, PixelFormats.Bgr32, null);
                bool bOK = false;
                try
                {
                    bmp.Lock();
                    try
                    {
                        bOK = cam_.PullImage(bmp.BackBuffer, 1, 32, bmp.BackBufferStride, out info); // check the return value
                    }
                    finally
                    {
                        bmp.Unlock();
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.ToString());
                }
                if (bOK)
                    SaveToFile(bmp);
            }
        }

        private void DelegateOnEventCallback(Toupcam.eEVENT evt)
        {
            /* this is called by internal thread of toupcam.dll which is NOT the same of UI thread.
             * So we use BeginInvoke
             */
            Dispatcher.BeginInvoke((Action)(() =>
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

        private void OnEventTempTint()
        {
            int nTemp = 0, nTint = 0;
            if (cam_.get_TempTint(out nTemp, out nTint))
            {
                label_temp_.Content = nTemp.ToString();
                label_tint_.Content = nTint.ToString();
                slider_temp_.Value = nTemp;
                slider_tint_.Value = nTint;
            }
        }

        private void startDevice(string camId)
        {
            cam_ = Toupcam.Open(camId);
            if (cam_ != null)
            {
                auto_exposure_.IsEnabled = true;
                combo_.IsEnabled = true;
                snap_.IsEnabled = true;
                auto_exposure_.IsEnabled = true;
                InitExpoTime();
                if (cam_.MonoMode)
                {
                    slider_temp_.IsEnabled = false;
                    slider_tint_.IsEnabled = false;
                    white_balance_once_.IsEnabled = false;
                }
                else
                {
                    slider_temp_.IsEnabled = true;
                    slider_tint_.IsEnabled = true;
                    white_balance_once_.IsEnabled = true;
                    OnEventTempTint();
                }

                uint resnum = cam_.ResolutionNumber;
                uint eSize = 0;
                if (cam_.get_eSize(out eSize))
                {
                    for (uint i = 0; i < resnum; ++i)
                    {
                        int w = 0, h = 0;
                        if (cam_.get_Resolution(i, out w, out h))
                            combo_.Items.Add(w.ToString() + "*" + h.ToString());
                    }
                    combo_.SelectedIndex = (int)eSize;

                    int width = 0, height = 0;
                    if (cam_.get_Size(out width, out height))
                    {
                        cam_.put_Option(Toupcam.eOPTION.OPTION_RGB, 2); // RGB32
                        /* The backend of WPF/UWP/WinUI is Direct3D/Direct2D, which is different from Winform's backend GDI.
                         * We use their respective native formats, Bgr32 in WPF/UWP/WinUI, and Bgr24 in Winform
                         */
                        bmp_ = new WriteableBitmap(width, height, 0, 0, PixelFormats.Bgr32, null);
                        if (!cam_.StartPullModeWithCallback(new Toupcam.DelegateEventCallback(DelegateOnEventCallback)))
                            MessageBox.Show("Failed to start camera");
                        else
                        {
                            bool autoexpo = true;
                            cam_.get_AutoExpoEnable(out autoexpo);
                            auto_exposure_.IsChecked = autoexpo;
                            slider_expotime_.IsEnabled = !autoexpo;
                        }

                        timer_ = new DispatcherTimer() { Interval = new TimeSpan(0, 0, 1) };
                        timer_.Tick += (sender, e) =>
                        {
                            if (cam_ != null)
                            {
                                uint nFrame = 0, nTime = 0, nTotalFrame = 0;
                                if (cam_.get_FrameRate(out nFrame, out nTime, out nTotalFrame) && (nTime > 0))
                                    label_fps_.Content = string.Format("{0}; fps = {1:#.0}", nTotalFrame, ((double)nFrame) * 1000.0 / (double)nTime);
                            }
                        };
                        timer_.Start();
                        image_.Source = bmp_;
                        started_ = true;
                    }
                }
            }
        }

        private void onClick_start(object sender, RoutedEventArgs e)
        {
            if (cam_ != null)
                return;

            Toupcam.DeviceV2[] arr = Toupcam.EnumV2();
            if (arr.Length <= 0)
                MessageBox.Show("No camera found.");
            else if (1 == arr.Length)
                startDevice(arr[0].id);
            else
            {
                ContextMenu menu = new ContextMenu() { PlacementTarget = start_, Placement = PlacementMode.Bottom };
                for (int i = 0; i < arr.Length; ++i)
                {
                    MenuItem mitem = new MenuItem() { Header = arr[i].displayname, CommandParameter = arr[i].id };
                    mitem.Click += (nsender, ne) =>
                    {
                        string camId = (string)(((MenuItem)nsender).CommandParameter);
                        if ((camId != null) && (camId.Length > 0))
                            startDevice(camId);
                    };
                    menu.Items.Add(mitem);
                }
                menu.IsOpen = true;
            }
        }

        private void onClick_whitebalanceonce(object sender, RoutedEventArgs e)
        {
            if (started_)
                cam_?.AwbOnce();
        }

        private void onChanged_temptint(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if ((cam_ != null) && started_)
            {
                cam_.put_TempTint((int)slider_temp_.Value, (int)slider_tint_.Value);
                label_temp_.Content = ((int)slider_temp_.Value).ToString();
                label_tint_.Content = ((int)slider_tint_.Value).ToString();
            }
        }

        private void onChanged_expotime(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if ((cam_ != null) && started_)
            {
                cam_.put_ExpoTime((uint)slider_expotime_.Value);
                label_expotime_.Content = ((uint)slider_expotime_.Value).ToString();
            }
        }

        private void onClick_auto_exposure(object sender, RoutedEventArgs e)
        {
            if (started_)
            {
                cam_?.put_AutoExpoEnable(auto_exposure_.IsChecked ?? false);
                slider_expotime_.IsEnabled = !auto_exposure_.IsChecked ?? false;
            }
        }

        private void OnClick_snap(object sender, RoutedEventArgs e)
        {
            if (cam_ != null)
            {
                if (cam_.StillResolutionNumber <= 0)
                {
                    if (bmp_ != null)
                        SaveToFile(bmp_);
                }
                else
                {
                    ContextMenu menu = new ContextMenu() { PlacementTarget = snap_, Placement = PlacementMode.Bottom };
                    for (uint i = 0; i < cam_.ResolutionNumber; ++i)
                    {
                        int w = 0, h = 0;
                        cam_.get_Resolution(i, out w, out h);
                        MenuItem mitem = new MenuItem() { Header = string.Format("{0} * {1}", w, h), CommandParameter = i }; //inbox
                        mitem.Click += (nsender, ne) =>
                        {
                            uint k = (uint)(((MenuItem)nsender).CommandParameter); //unbox
                            if (k < cam_.StillResolutionNumber)
                                cam_.Snap(k);
                        };
                        menu.Items.Add(mitem);
                    }
                    menu.IsOpen = true;
                }
            }
        }

        private void InitExpoTime()
        {
            if (cam_ == null)
                return;

            uint nMin = 0, nMax = 0, nDef = 0;
            if (cam_.get_ExpTimeRange(out nMin, out nMax, out nDef))
            {
                slider_expotime_.Minimum = nMin;
                slider_expotime_.Maximum = nMax;
            }
            OnEventExposure();
        }

        private void onSelchange_combo(object sender, SelectionChangedEventArgs e)
        {
            if (cam_ != null)
            {
                uint eSize = 0;
                if (cam_.get_eSize(out eSize))
                {
                    if (eSize != combo_.SelectedIndex)
                    {
                        cam_.Stop();
                        cam_.put_eSize((uint)combo_.SelectedIndex);

                        InitExpoTime();
                        OnEventTempTint();

                        int width = 0, height = 0;
                        if (cam_.get_Size(out width, out height))
                        {
                            bmp_ = new WriteableBitmap(width, height, 0, 0, PixelFormats.Bgr32, null);
                            cam_.StartPullModeWithCallback(new Toupcam.DelegateEventCallback(DelegateOnEventCallback));
                        }
                    }
                }
            }
        }
    }
}
