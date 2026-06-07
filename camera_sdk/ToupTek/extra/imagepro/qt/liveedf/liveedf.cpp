#include <QApplication>
#include "liveedf.h"

MainWidget::MainWidget(QWidget* parent)
    : QWidget(parent), m_timer(new QTimer(this)), m_hcam(nullptr), m_edf(nullptr)
    , m_lbl_edf(nullptr), m_lbl_video(nullptr), m_lbl_frame(nullptr), m_imgWidth(0), m_imgHeight(0)
    , m_pVideoData(nullptr), m_pEdfData(nullptr)
{
    setMinimumSize(1024, 768);

    m_btn_open = new QPushButton("Open");
    connect(m_btn_open, &QPushButton::clicked, this, &MainWidget::onBtnOpen);
    m_cbox_auto = new QCheckBox("Auto exposure");
    m_cbox_auto->setCheckState(Qt::Checked);
    connect(m_cbox_auto, &QCheckBox::stateChanged, this, [this](bool state)
    {
        if (m_hcam)
            Toupcam_put_AutoExpoEnable(m_hcam, state ? 1 : 0);
    });

    QHBoxLayout* hlayout = new QHBoxLayout();
    {
        QVBoxLayout* vlayout = new QVBoxLayout();
        m_lbl_frame = new QLabel();
        vlayout->addWidget(m_btn_open);
        vlayout->addWidget(m_cbox_auto);
        vlayout->addWidget(m_lbl_frame);
        hlayout->addLayout(vlayout, 1);
    }
    {
        QVBoxLayout* vlayout = new QVBoxLayout();
        m_lbl_video = new QLabel();
        vlayout->addWidget(m_lbl_video);
        m_lbl_edf = new QLabel();
        vlayout->addWidget(m_lbl_edf);
        hlayout->addLayout(vlayout, 4);
    }
    setLayout(hlayout);

    connect(this, &MainWidget::cameraCallback, this, [this](unsigned nEvent)
    {
        /* this run in the UI thread */
        if (m_hcam)
        {
            if (TOUPCAM_EVENT_IMAGE == nEvent)
                handleImageEvent();
            else if (TOUPCAM_EVENT_ERROR == nEvent)
            {
                closeCamera();
                QMessageBox::warning(this, "Warning", "Generic error.");
            }
            else if (TOUPCAM_EVENT_DISCONNECTED == nEvent)
            {
                closeCamera();
                QMessageBox::warning(this, "Warning", "Camera disconnect.");
            }
        }
    });

    connect(this, &MainWidget::edfCallback, this, [this](eImageproEdfEvent evt)
    {
        /* this run in the UI thread */
        if (eImageproEdf_NOMEM == evt)
            QMessageBox::warning(this, "Warning", "Edf out of memory.");
        else if (eImageproEdf_ERROR == evt)
            QMessageBox::warning(this, "Warning", "Edf generic error.");
    });

    connect(m_timer, &QTimer::timeout, this, [this]()
    {
        unsigned nFrame = 0, nTime = 0, nTotalFrame = 0;
        if (m_hcam && SUCCEEDED(Toupcam_get_FrameRate(m_hcam, &nFrame, &nTime, &nTotalFrame)) && (nTime > 0))
            m_lbl_frame->setText(QString::asprintf("%u, fps = %.1f", nTotalFrame, nFrame * 1000.0 / nTime));
    });
    m_timer->start(1000);
}

void MainWidget::closeCamera()
{
    if (m_hcam)
    {
        Toupcam_Close(m_hcam);
        m_hcam = nullptr;
    }
    if (m_edf)
    {
        imagepro_edf_delete(m_edf);
        m_edf = nullptr;
    }
    delete[] m_pVideoData;
    m_pVideoData = nullptr;
    delete[] m_pEdfData;
    m_pEdfData = nullptr;
}

void MainWidget::closeEvent(QCloseEvent*)
{
    closeCamera();
}

void MainWidget::onBtnOpen()
{
    if (m_hcam)
        return;

    ToupcamDeviceV2 arr[TOUPCAM_MAX] = { 0 };
    const unsigned count = Toupcam_EnumV2(arr);
    if (0 == count)
        QMessageBox::warning(this, "Warning", "No camera found.");
    else
    {
		m_hcam = Toupcam_Open(arr[0].id);
		if (m_hcam)
		{
			Toupcam_get_Size(m_hcam, &m_imgWidth, &m_imgHeight);
			Toupcam_put_Option(m_hcam, TOUPCAM_OPTION_BYTEORDER, 0); //Qimage use RGB byte order
            Toupcam_put_AutoExpoEnable(m_hcam, m_cbox_auto->isChecked() ? 1 : 0);
	
            m_pVideoData = new uchar[TDIBWIDTHBYTES(m_imgWidth * 24) * m_imgHeight];
            m_pEdfData = new uchar[TDIBWIDTHBYTES(m_imgWidth * 24) * m_imgHeight];
            m_edf = imagepro_edf_newV2(eImageproFormat_RGB24, eImageproEdfM_Pyr_Max, EdfCallback, EdfECallback, this);
            imagepro_edf_start(m_edf);
            if (FAILED(Toupcam_StartPullModeWithCallback(m_hcam, CameraCallBack, this)))
			{
				closeCamera();
				QMessageBox::warning(this, "Warning", "Failed to start camera.");
            }
        }
    }
}

void MainWidget::CameraCallBack(unsigned nEvent, void* pCallbackCtx)
{
    MainWidget* pthis = reinterpret_cast<MainWidget*>(pCallbackCtx);
    emit pthis->cameraCallback(nEvent);
}

void MainWidget::EdfCallback(void* /*ctx*/, int /*result*/, void* /*outData*/, int /*stride*/, int /*outW*/, int /*outH*/, int /*outType*/)
{
    /* nothing to do */
}

void MainWidget::EdfECallback(void* ctx, eImageproEdfEvent evt)
{
    MainWidget* pthis = reinterpret_cast<MainWidget*>(ctx);
    emit pthis->edfCallback(evt);
}

void MainWidget::handleImageEvent()
{
    ToupcamFrameInfoV4 info = { 0 };
    imagepro_edf_pullV4(m_edf, m_hcam, 1, m_pVideoData, 24, 0, &info);
    {
        QImage image(m_pVideoData, info.v3.width, info.v3.height, QImage::Format_RGB888);
        QImage newimage = image.scaled(m_lbl_video->width(), m_lbl_video->height(), Qt::KeepAspectRatio, Qt::FastTransformation);
        m_lbl_video->setPixmap(QPixmap::fromImage(newimage));
    }
    {
        imagepro_edf_readdata(m_edf, m_pEdfData, TDIBWIDTHBYTES(m_imgWidth * 24));
        QImage image(m_pEdfData, info.v3.width, info.v3.height, QImage::Format_RGB888);
        QImage newimage = image.scaled(m_lbl_edf->width(), m_lbl_edf->height(), Qt::KeepAspectRatio, Qt::FastTransformation);
        m_lbl_edf->setPixmap(QPixmap::fromImage(newimage));
    }
}

int main(int argc, char* argv[])
{
    Toupcam_GigeEnable(nullptr, nullptr);
    QApplication a(argc, argv);
    MainWidget w;
    w.show();
    return a.exec();
}
