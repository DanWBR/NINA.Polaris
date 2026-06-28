<Global.Microsoft.VisualBasic.CompilerServices.DesignerGenerated()> _
Partial Class Form1
    Inherits System.Windows.Forms.Form

    'Form overrides dispose to clean up the component list.
    <System.Diagnostics.DebuggerNonUserCode()> _
    Protected Overrides Sub Dispose(ByVal disposing As Boolean)
        Try
            If disposing AndAlso components IsNot Nothing Then
                components.Dispose()
            End If
        Finally
            MyBase.Dispose(disposing)
        End Try
    End Sub

    'Required by the Windows Form Designer
    Private components As System.ComponentModel.IContainer

    'NOTE: The following procedure is required by the Windows Form Designer
    'It can be modified using the Windows Form Designer.  
    'Do not modify it using the code editor.
    <System.Diagnostics.DebuggerStepThrough()> _
    Private Sub InitializeComponent()
        Me.components = New System.ComponentModel.Container()
        Me.trackBar3 = New System.Windows.Forms.TrackBar()
        Me.trackBar2 = New System.Windows.Forms.TrackBar()
        Me.label3 = New System.Windows.Forms.Label()
        Me.label2 = New System.Windows.Forms.Label()
        Me.button3 = New System.Windows.Forms.Button()
        Me.pictureBox1 = New System.Windows.Forms.PictureBox()
        Me.label1 = New System.Windows.Forms.Label()
        Me.trackBar1 = New System.Windows.Forms.TrackBar()
        Me.checkBox1 = New System.Windows.Forms.CheckBox()
        Me.comboBox1 = New System.Windows.Forms.ComboBox()
        Me.button2 = New System.Windows.Forms.Button()
        Me.button1 = New System.Windows.Forms.Button()
        Me.label4 = New System.Windows.Forms.Label()
        Me.Timer1 = New System.Windows.Forms.Timer(Me.components)
        CType(Me.trackBar3, System.ComponentModel.ISupportInitialize).BeginInit()
        CType(Me.trackBar2, System.ComponentModel.ISupportInitialize).BeginInit()
        CType(Me.pictureBox1, System.ComponentModel.ISupportInitialize).BeginInit()
        CType(Me.trackBar1, System.ComponentModel.ISupportInitialize).BeginInit()
        Me.SuspendLayout()
        '
        'trackBar3
        '
        Me.trackBar3.Location = New System.Drawing.Point(11, 358)
        Me.trackBar3.Name = "trackBar3"
        Me.trackBar3.Size = New System.Drawing.Size(166, 45)
        Me.trackBar3.TabIndex = 24
        Me.trackBar3.TickStyle = System.Windows.Forms.TickStyle.None
        '
        'trackBar2
        '
        Me.trackBar2.Location = New System.Drawing.Point(11, 294)
        Me.trackBar2.Name = "trackBar2"
        Me.trackBar2.Size = New System.Drawing.Size(166, 45)
        Me.trackBar2.TabIndex = 23
        Me.trackBar2.TickStyle = System.Windows.Forms.TickStyle.None
        '
        'label3
        '
        Me.label3.Location = New System.Drawing.Point(9, 342)
        Me.label3.Name = "label3"
        Me.label3.Size = New System.Drawing.Size(162, 12)
        Me.label3.TabIndex = 22
        Me.label3.Text = "Tint"
        Me.label3.TextAlign = System.Drawing.ContentAlignment.TopCenter
        '
        'label2
        '
        Me.label2.Location = New System.Drawing.Point(9, 279)
        Me.label2.Name = "label2"
        Me.label2.Size = New System.Drawing.Size(164, 12)
        Me.label2.TabIndex = 21
        Me.label2.Text = "Temp"
        Me.label2.TextAlign = System.Drawing.ContentAlignment.TopCenter
        '
        'button3
        '
        Me.button3.Location = New System.Drawing.Point(11, 249)
        Me.button3.Name = "button3"
        Me.button3.Size = New System.Drawing.Size(166, 23)
        Me.button3.TabIndex = 20
        Me.button3.Text = "White Balance Once"
        Me.button3.UseVisualStyleBackColor = True
        '
        'pictureBox1
        '
        Me.pictureBox1.Location = New System.Drawing.Point(183, 10)
        Me.pictureBox1.Name = "pictureBox1"
        Me.pictureBox1.Size = New System.Drawing.Size(595, 547)
        Me.pictureBox1.SizeMode = System.Windows.Forms.PictureBoxSizeMode.Zoom
        Me.pictureBox1.TabIndex = 19
        Me.pictureBox1.TabStop = False
        '
        'label1
        '
        Me.label1.Location = New System.Drawing.Point(11, 173)
        Me.label1.Name = "label1"
        Me.label1.Size = New System.Drawing.Size(166, 12)
        Me.label1.TabIndex = 18
        Me.label1.Text = "Expo"
        Me.label1.TextAlign = System.Drawing.ContentAlignment.TopCenter
        '
        'trackBar1
        '
        Me.trackBar1.Location = New System.Drawing.Point(11, 198)
        Me.trackBar1.Name = "trackBar1"
        Me.trackBar1.Size = New System.Drawing.Size(166, 45)
        Me.trackBar1.TabIndex = 17
        Me.trackBar1.TickStyle = System.Windows.Forms.TickStyle.None
        '
        'checkBox1
        '
        Me.checkBox1.AutoSize = True
        Me.checkBox1.Location = New System.Drawing.Point(11, 137)
        Me.checkBox1.Name = "checkBox1"
        Me.checkBox1.Size = New System.Drawing.Size(102, 16)
        Me.checkBox1.TabIndex = 16
        Me.checkBox1.Text = "Auto Exposure"
        Me.checkBox1.UseVisualStyleBackColor = True
        '
        'comboBox1
        '
        Me.comboBox1.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList
        Me.comboBox1.FormattingEnabled = True
        Me.comboBox1.Location = New System.Drawing.Point(11, 95)
        Me.comboBox1.Name = "comboBox1"
        Me.comboBox1.Size = New System.Drawing.Size(166, 20)
        Me.comboBox1.TabIndex = 15
        '
        'button2
        '
        Me.button2.Location = New System.Drawing.Point(11, 53)
        Me.button2.Name = "button2"
        Me.button2.Size = New System.Drawing.Size(166, 23)
        Me.button2.TabIndex = 14
        Me.button2.Text = "Snap"
        Me.button2.UseVisualStyleBackColor = True
        '
        'button1
        '
        Me.button1.Location = New System.Drawing.Point(11, 10)
        Me.button1.Name = "button1"
        Me.button1.Size = New System.Drawing.Size(166, 23)
        Me.button1.TabIndex = 13
        Me.button1.Text = "Start"
        Me.button1.UseVisualStyleBackColor = True
        '
        'label4
        '
        Me.label4.Location = New System.Drawing.Point(9, 406)
        Me.label4.Name = "label4"
        Me.label4.Size = New System.Drawing.Size(162, 12)
        Me.label4.TabIndex = 25
        Me.label4.Text = "0, fps = 0.0"
        Me.label4.TextAlign = System.Drawing.ContentAlignment.TopCenter
        '
        'Timer1
        '
        Me.Timer1.Interval = 1000
        '
        'Form1
        '
        Me.AutoScaleDimensions = New System.Drawing.SizeF(6.0!, 12.0!)
        Me.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font
        Me.ClientSize = New System.Drawing.Size(784, 448)
        Me.Controls.Add(Me.label4)
        Me.Controls.Add(Me.trackBar3)
        Me.Controls.Add(Me.trackBar2)
        Me.Controls.Add(Me.label3)
        Me.Controls.Add(Me.label2)
        Me.Controls.Add(Me.button3)
        Me.Controls.Add(Me.pictureBox1)
        Me.Controls.Add(Me.label1)
        Me.Controls.Add(Me.trackBar1)
        Me.Controls.Add(Me.checkBox1)
        Me.Controls.Add(Me.comboBox1)
        Me.Controls.Add(Me.button2)
        Me.Controls.Add(Me.button1)
        Me.MinimumSize = New System.Drawing.Size(777, 435)
        Me.Name = "Form1"
        Me.Text = "demowinformvb"
        CType(Me.trackBar3, System.ComponentModel.ISupportInitialize).EndInit()
        CType(Me.trackBar2, System.ComponentModel.ISupportInitialize).EndInit()
        CType(Me.pictureBox1, System.ComponentModel.ISupportInitialize).EndInit()
        CType(Me.trackBar1, System.ComponentModel.ISupportInitialize).EndInit()
        Me.ResumeLayout(False)
        Me.PerformLayout()

    End Sub
    Private WithEvents trackBar3 As System.Windows.Forms.TrackBar
    Private WithEvents trackBar2 As System.Windows.Forms.TrackBar
    Private WithEvents label3 As System.Windows.Forms.Label
    Private WithEvents label2 As System.Windows.Forms.Label
    Private WithEvents button3 As System.Windows.Forms.Button
    Private WithEvents pictureBox1 As System.Windows.Forms.PictureBox
    Private WithEvents label1 As System.Windows.Forms.Label
    Private WithEvents trackBar1 As System.Windows.Forms.TrackBar
    Private WithEvents checkBox1 As System.Windows.Forms.CheckBox
    Private WithEvents comboBox1 As System.Windows.Forms.ComboBox
    Private WithEvents button2 As System.Windows.Forms.Button
    Private WithEvents button1 As System.Windows.Forms.Button
    Private WithEvents label4 As Label
    Friend WithEvents Timer1 As Timer
End Class
