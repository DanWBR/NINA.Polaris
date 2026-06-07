Imports System
Imports System.Drawing
Imports System.Drawing.Imaging
Imports System.Windows.Forms

Public Class Form1
    Private Delegate Sub DelegateEvent(evt As Toupcam.eEVENT())
    Private cam_ As Toupcam = Nothing
    Private bmp_ As Bitmap = Nothing
    Private ev_ As DelegateEvent = Nothing
    Private count_ As UInteger = 0

    Private Sub OnEventError()
        cam_.Close()
        cam_ = Nothing
        MessageBox.Show("Generic error.")
    End Sub

    Private Sub OnEventDisconnected()
        cam_.Close()
        cam_ = Nothing
        MessageBox.Show("Camera disconnect.")
    End Sub

    Private Sub OnEventExposure()
        Dim nTime As UInteger = 0
        If cam_.get_ExpoTime(nTime) Then
            trackBar1.Value = CInt(nTime)
            label1.Text = nTime.ToString()
        End If
    End Sub

    Private Sub OnEventImage()
        If bmp_ IsNot Nothing Then
            Dim info As New Toupcam.FrameInfoV4
            Dim bOK As Boolean = False
            Try
                Dim bmpdata As BitmapData = bmp_.LockBits(New Rectangle(0, 0, bmp_.Width, bmp_.Height), ImageLockMode.[WriteOnly], bmp_.PixelFormat)
                Try
                    bOK = cam_.PullImage(bmpdata.Scan0, 0, 24, bmpdata.Stride, info) ' check the return value
                Finally
                    bmp_.UnlockBits(bmpdata)
                End Try
            Catch ex As Exception
                MessageBox.Show(ex.ToString())
            End Try
            If bOK Then
                pictureBox1.Invalidate()
            End If
        End If
    End Sub

    Private Sub OnEventStillImage()
        Dim info As New Toupcam.FrameInfoV4
        If cam_.PullImage(IntPtr.Zero, 1, 24, 0, info) Then ' peek the width and height
            Dim sbmp As New Bitmap(CInt(info.v3.width), CInt(info.v3.height), PixelFormat.Format24bppRgb)
            Dim bOK As Boolean = False
            Try
                Dim bmpdata As BitmapData = sbmp.LockBits(New Rectangle(0, 0, sbmp.Width, sbmp.Height), ImageLockMode.[WriteOnly], sbmp.PixelFormat)
                Try
                    bOK = cam_.PullImage(bmpdata.Scan0, 1, 24, bmpdata.Stride, info) ' check the return value
                Finally
                    sbmp.UnlockBits(bmpdata)
                End Try
            Catch ex As Exception
                MessageBox.Show(ex.ToString())
            End Try
            If bOK Then
                count_ = count_ + 1
                sbmp.Save(String.Format("demowinformvb_{0}.jpg", count_), ImageFormat.Jpeg)
            End If
        End If
    End Sub

    Public Sub New()
        InitializeComponent()
        pictureBox1.Width = ClientRectangle.Right - pictureBox1.Left - button1.Top
        pictureBox1.Height = ClientRectangle.Height - 2 * button1.Top
        Toupcam.GigeEnable(Nothing)
    End Sub

    Private Sub Form_SizeChanged(sender As Object, e As EventArgs) Handles MyBase.SizeChanged
        pictureBox1.Width = ClientRectangle.Right - pictureBox1.Left - button1.Top
        pictureBox1.Height = ClientRectangle.Height - 2 * button1.Top
    End Sub

    'this run in the UI thread
    Private Sub DelegateOnEvent(evt As Toupcam.eEVENT())
        If cam_ IsNot Nothing Then
            Select Case (evt(0))
                Case Toupcam.eEVENT.EVENT_ERROR
                    OnEventError()
                Case Toupcam.eEVENT.EVENT_DISCONNECTED
                    OnEventDisconnected()
                Case Toupcam.eEVENT.EVENT_EXPOSURE
                    OnEventExposure()
                Case Toupcam.eEVENT.EVENT_IMAGE
                    OnEventImage()
                Case Toupcam.eEVENT.EVENT_STILLIMAGE
                    OnEventStillImage()
                Case Toupcam.eEVENT.EVENT_TEMPTINT
                    OnEventTempTint()
            End Select
        End If
    End Sub

    Private Sub DelegateOnEventCallback(evt As Toupcam.eEVENT)
        ' this is called by internal thread of toupcam.dll which Is Not the same of UI thread.
        ' Why we use BeginInvoke, Please see:
        ' http//msdn.microsoft.com/en-us/magazine/cc300429.aspx
        ' http://msdn.microsoft.com/en-us/magazine/cc188732.aspx
        ' http://stackoverflow.com/questions/1364116/avoiding-the-woes-of-invoke-begininvoke-in-cross-thread-winform-event-handling
        BeginInvoke(ev_, New Toupcam.eEVENT() {evt})
    End Sub

    Private Sub InitExpoTimeRange()
        Dim nMin As UInteger = 0, nMax As UInteger = 0, nDef As UInteger = 0
        If cam_.get_ExpTimeRange(nMin, nMax, nDef) Then
            trackBar1.SetRange(CInt(nMin), CInt(nMax))
        End If
        OnEventExposure()
    End Sub

    Private Overloads Sub OnClosing(sender As Object, e As FormClosingEventArgs) Handles MyBase.FormClosing
        If cam_ IsNot Nothing Then
            cam_.Close()
            cam_ = Nothing
        End If
    End Sub

    Private Sub OnEventTempTint()
        Dim nTemp As Integer = 0, nTint As Integer = 0
        If cam_.get_TempTint(nTemp, nTint) Then
            label2.Text = nTemp.ToString()
            label3.Text = nTint.ToString()
            trackBar2.Value = nTemp
            trackBar3.Value = nTint
        End If
    End Sub

    Private Sub Form1_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        Me.button2.Enabled = False
        Me.button3.Enabled = False
        Me.trackBar1.Enabled = False
        Me.trackBar2.Enabled = False
        Me.trackBar3.Enabled = False
        Me.checkBox1.Enabled = False
        Me.comboBox1.Enabled = False
        Me.trackBar2.SetRange(Toupcam.TEMP_MIN, Toupcam.TEMP_MAX)
        Me.trackBar3.SetRange(Toupcam.TINT_MIN, Toupcam.TINT_MAX)
    End Sub

    Private Sub StartMenuClickedHandler(sender As Object, e As ToolStripItemClickedEventArgs)
        Dim camId As String = CType(e.ClickedItem.Tag, String)
        startDevice(camId)
    End Sub

    Private Sub OnStart(sender As Object, e As EventArgs) Handles button1.Click
        If cam_ IsNot Nothing Then
            Return
        End If

        Dim arr As Toupcam.DeviceV2() = Toupcam.[EnumV2]()
        If arr.Length <= 0 Then
            MessageBox.Show("No camera found.")
        ElseIf arr.Length = 1 Then
            startDevice(arr(0).id)
        Else
            Dim ctxmenu As ContextMenuStrip = New ContextMenuStrip()
            AddHandler ctxmenu.ItemClicked, AddressOf Me.StartMenuClickedHandler
            For i As Integer = 0 To arr.Length - 1
                ctxmenu.Items.Add(arr(i).displayname).Tag = arr(i).id
            Next
            ctxmenu.Show(button1, 0, 0)
        End If
    End Sub

    Private Sub startDevice(camId As String)
        cam_ = Toupcam.Open(camId)
        If cam_ IsNot Nothing Then
            checkBox1.Enabled = True
            comboBox1.Enabled = True
            button2.Enabled = True
            InitExpoTimeRange()
            If cam_.MonoMode Then
                trackBar2.Enabled = False
                trackBar3.Enabled = False
                button3.Enabled = False
            Else
                trackBar2.Enabled = True
                trackBar3.Enabled = True
                button3.Enabled = True
                OnEventTempTint()
            End If

            Dim resnum As UInteger = cam_.ResolutionNumber
            Dim eSize As UInteger = 0
            If cam_.get_eSize(eSize) Then
                For i As UInteger = 0 To resnum - 1
                    Dim w As Integer = 0, h As Integer = 0
                    If cam_.get_Resolution(i, w, h) Then
                        comboBox1.Items.Add(w.ToString() & "*" & h.ToString())
                    End If
                Next
                comboBox1.SelectedIndex = CInt(eSize)

                Dim width As Integer = 0, height As Integer = 0
                If cam_.get_Size(width, height) Then
                    ' The backend of Winform is GDI, which is different from WPF/UWP/WinUI's backend Direct3D/Direct2D.
                    ' We use their respective native formats, Bgr24 in Winform, and Bgr32 in WPF/UWP/WinUI
                    '
                    bmp_ = New Bitmap(width, height, PixelFormat.Format24bppRgb)
                    ev_ = New DelegateEvent(AddressOf Me.DelegateOnEvent)
                    If Not cam_.StartPullModeWithCallback(New Toupcam.DelegateEventCallback(AddressOf Me.DelegateOnEventCallback)) Then
                        MessageBox.Show("Failed to start camera.")
                    Else
                        Dim autoexpo As Boolean = True
                        cam_.get_AutoExpoEnable(autoexpo)
                        checkBox1.Checked = autoexpo
                        trackBar1.Enabled = Not autoexpo
                    End If
                End If
            End If

            Timer1.Start()
        End If
    End Sub

    Private Sub OnSnap(sender As Object, e As EventArgs) Handles button2.Click
        If cam_ IsNot Nothing Then
            If cam_.StillResolutionNumber <= 0 Then
                If bmp_ IsNot Nothing Then
                    count_ = count_ + 1
                    bmp_.Save(String.Format("demowinformvb_{0}.jpg", count_), ImageFormat.Jpeg)
                End If
            Else
                Dim ctxmenu As ContextMenuStrip = New ContextMenuStrip()
                AddHandler ctxmenu.ItemClicked, AddressOf Me.SnapMenuClickedHandler
                For i As UInteger = 0 To cam_.ResolutionNumber - 1
                    Dim w As Integer = 0, h As Integer = 0
                    cam_.get_Resolution(i, w, h)
                    ctxmenu.Items.Add(String.Format("{0} * {1}", w, h)).Tag = i 'inbox
                Next
                ctxmenu.Show(button2, 0, 0)
            End If
        End If
    End Sub

    Private Sub SnapMenuClickedHandler(sender As Object, e As ToolStripItemClickedEventArgs)
        Dim k As UInteger = CType(e.ClickedItem.Tag, UInteger) 'unbox
        If k < cam_.StillResolutionNumber Then
            cam_.Snap(k)
        End If
    End Sub

    Private Sub OnSelectResolution(sender As Object, e As EventArgs) Handles comboBox1.SelectedIndexChanged
        If cam_ IsNot Nothing Then
            Dim eSize As UInteger = 0
            If cam_.get_eSize(eSize) Then
                If eSize <> comboBox1.SelectedIndex Then
                    cam_.[Stop]()
                    cam_.put_eSize(CUInt(comboBox1.SelectedIndex))

                    InitExpoTimeRange()
                    OnEventTempTint()

                    Dim width As Integer = 0, height As Integer = 0
                    If cam_.get_Size(width, height) Then
                        bmp_ = New Bitmap(width, height, PixelFormat.Format24bppRgb)
                        ev_ = New DelegateEvent(AddressOf Me.DelegateOnEvent)
                        cam_.StartPullModeWithCallback(New Toupcam.DelegateEventCallback(AddressOf Me.DelegateOnEventCallback))
                    End If
                End If
            End If
        End If
    End Sub

    Private Sub checkBox1_CheckedChanged(sender As Object, e As EventArgs) Handles checkBox1.CheckedChanged
        If cam_ IsNot Nothing Then
            cam_.put_AutoExpoEnable(checkBox1.Checked)
        End If
        trackBar1.Enabled = Not checkBox1.Checked
    End Sub

    Private Sub OnExpoValueChange(sender As Object, e As EventArgs) Handles trackBar1.ValueChanged
        If Not checkBox1.Checked Then
            If cam_ IsNot Nothing Then
                Dim n As UInteger = CUInt(trackBar1.Value)
                cam_.put_ExpoTime(n)
                label1.Text = n.ToString()
            End If
        End If
    End Sub

    Private Sub OnWhiteBalanceOnce(sender As Object, e As EventArgs) Handles button3.Click
        If cam_ IsNot Nothing Then
            cam_.AwbOnce()
        End If
    End Sub

    Private Sub OnTempTintChanged(sender As Object, e As EventArgs) Handles trackBar2.ValueChanged, trackBar3.ValueChanged
        If cam_ IsNot Nothing Then
            cam_.put_TempTint(trackBar2.Value, trackBar3.Value)
        End If
        label2.Text = trackBar2.Value.ToString()
        label3.Text = trackBar3.Value.ToString()
    End Sub

    Private Sub OnTimer1(sender As Object, e As EventArgs) Handles Timer1.Tick
        If cam_ IsNot Nothing Then
            Dim nFrame As UInteger = 0, nTime As UInteger = 0, nTotalFrame As UInteger = 0
            If cam_.get_FrameRate(nFrame, nTime, nTotalFrame) AndAlso nTime > 0 Then
                label4.Text = String.Format("{0}; fps = {1:#.0}", nTotalFrame, CType(nFrame, Double) * 1000.0 / CType(nTime, Double))
            End If
        End If
    End Sub

    Private Sub pictureBox1_Paint(sender As Object, e As PaintEventArgs) Handles pictureBox1.Paint
        Dim g As Graphics = e.Graphics
        If (bmp_ IsNot Nothing) Then
            g.DrawImage(bmp_, pictureBox1.ClientRectangle)
            g.DrawString(label4.Text, Me.Font, SystemBrushes.ControlText, 0, 0)
        End If
    End Sub
End Class